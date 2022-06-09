﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Headstart.Common.Models;
using OrderCloud.Catalyst;
using OrderCloud.Integrations.CosmosDB;
using OrderCloud.Integrations.Reporting.Models;
using OrderCloud.Integrations.Reporting.Repositories;
using OrderCloud.SDK;

namespace Headstart.Jobs
{
    public class ReceiveRecentLineItemsJob : BaseReportJob
    {
        private readonly IOrderCloudClient oc;
        private readonly ILineItemDetailDataRepo lineItemDetailDataRepo;

        public ReceiveRecentLineItemsJob(IOrderCloudClient oc, ILineItemDetailDataRepo lineItemDetailDataRepo)
        {
            this.oc = oc;
            this.lineItemDetailDataRepo = lineItemDetailDataRepo;
        }

        protected override async Task<ResultCode> ProcessJobAsync(string message)
        {
            try
            {
                await UpsertLineItemDetail(message);
                return ResultCode.Success;
            }
            catch (Exception ex)
            {
                LogFailure($"{ex.Message} {ex?.InnerException?.Message} {ex.StackTrace}");
                return ResultCode.PermanentFailure;
            }
        }

        private async Task UpsertLineItemDetail(string orderID)
        {
            var orderWorksheet = await oc.IntegrationEvents.GetWorksheetAsync<HSOrderWorksheet>(OrderDirection.Incoming, orderID);

            var lineItems = await oc.LineItems.ListAllAsync<HSLineItem>(OrderDirection.Incoming, orderID);

            var buyer = await oc.Buyers.GetAsync<HSBuyer>(orderWorksheet.Order.FromCompanyID);

            var lineItemsWithMiscFields = await BuildLineItemsMiscFields(lineItems, orderWorksheet, buyer.Name);

            var lineItemsWithPurchaseOrders = await BuildLineItemsWithPurchaseOrders(orderID);

            var orderLineItemData = new HSOrderLineItemData()
            {
                Order = orderWorksheet.Order,
                LineItems = lineItems,
                LineItemsWithMiscFields = lineItemsWithMiscFields,
                LineItemsWithPurchaseOrderFields = lineItemsWithPurchaseOrders,
            };

            var queryable = lineItemDetailDataRepo.GetQueryable().Where(order => order.PartitionKey == "PartitionValue");

            var requestOptions = BuildQueryRequestOptions();

            var cosmosLineItemOrder = new LineItemDetailData()
            {
                PartitionKey = "PartitionValue",
                OrderID = orderID,
                Data = orderLineItemData,
            };

            var listOptions = BuildListOptions(orderID);

            CosmosListPage<LineItemDetailData> currentLineItemListPage = await lineItemDetailDataRepo.GetItemsAsync(queryable, requestOptions, listOptions);

            var cosmosID = string.Empty;
            if (currentLineItemListPage.Items.Count() == 1)
            {
                cosmosID = cosmosLineItemOrder.id = currentLineItemListPage.Items[0].id;
            }

            await lineItemDetailDataRepo.UpsertItemAsync(cosmosID, cosmosLineItemOrder);
        }

        private async Task<List<LineItemsWithPurchaseOrderFields>> BuildLineItemsWithPurchaseOrders(string orderID)
        {
            // returns POs
            var orders = await oc.Orders.ListAllAsync<HSOrder>(OrderDirection.Outgoing, filters: $"ID={orderID}-*");

            // loop through orders, get line items, pass those.
            List<LineItemsWithPurchaseOrderFields> orderLineItemBySupplierID = await GetLineItemsFromPurchaseOrdersAsync(orders);

            return orderLineItemBySupplierID;
        }

        private async Task<List<LineItemsWithPurchaseOrderFields>> GetLineItemsFromPurchaseOrdersAsync(List<HSOrder> orders)
        {
            var result = new List<LineItemsWithPurchaseOrderFields>() { };

            foreach (HSOrder order in orders)
            {
                List<HSLineItem> lineItemsBySupplier = await oc.LineItems.ListAllAsync<HSLineItem>(OrderDirection.Outgoing, order.ID);

                if (lineItemsBySupplier.Count() <= 0)
                {
                    continue;
                }

                foreach (HSLineItem lineItem in lineItemsBySupplier)
                {
                    var lineItemWithPurchaseOrder = new LineItemsWithPurchaseOrderFields
                    {
                        ID = lineItem.ID,
                        OrderID = order.ID,
                        Subtotal = order.Subtotal,
                        Total = order.Total,
                        UnitPrice = lineItem.UnitPrice,
                        SupplierID = lineItem.SupplierID,
                    };
                    result.Add(lineItemWithPurchaseOrder);
                }
            }

            return result;
        }

        private async Task<List<LineItemMiscReportFields>> BuildLineItemsMiscFields(List<HSLineItem> lineItems, HSOrderWorksheet orderWorksheet, string buyerName)
        {
            var lineItemsWithMiscFields = new List<LineItemMiscReportFields>();

            foreach (var lineItem in lineItems)
            {
                var lineItemSupplier = await oc.Suppliers.GetAsync<HSSupplier>(lineItem.SupplierID);
                var lineItemWithMiscFields = new LineItemMiscReportFields
                {
                    ID = lineItem.ID,
                    SupplierName = lineItemSupplier?.Name,
                    BrandName = buyerName,
                };

                if (orderWorksheet.OrderCalculateResponse != null && orderWorksheet.OrderCalculateResponse.xp != null && orderWorksheet.OrderCalculateResponse.xp.TaxCalculation.ExternalTransactionID != "NotTaxable")
                {
                    var lineTax = orderWorksheet.OrderCalculateResponse.xp.TaxCalculation.LineItems.FirstOrDefault(line => line.LineItemID == lineItem.ID);
                    lineItemWithMiscFields.Tax = lineTax?.LineItemTotalTax;
                    lineItemWithMiscFields.LineTaxAvailable = lineTax != null;
                }
                else
                {
                    lineItemWithMiscFields.Tax = null;
                    lineItemWithMiscFields.LineTaxAvailable = false;
                }

                lineItemsWithMiscFields.Add(lineItemWithMiscFields);
            }

            return lineItemsWithMiscFields;
        }
    }
}
