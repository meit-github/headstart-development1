import { Pipe, PipeTransform } from '@angular/core'

@Pipe({
  name: 'yesNo',
})
export class YesNoFormatPipe implements PipeTransform {
  transform(value: any, ...args: any[]): any {
    const myBool = value === 'true' || value === true
    return myBool ? 'ADMIN.FILTER_OPTIONS.YES' : 'ADMIN.FILTER_OPTIONS.NO'
  }
}
