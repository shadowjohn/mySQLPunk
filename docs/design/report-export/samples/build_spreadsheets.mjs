import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Workbook, SpreadsheetFile } from '@oai/artifact-tool';

const root = path.dirname(fileURLToPath(import.meta.url));
const data = JSON.parse(await fs.readFile(path.join(root,'demo-schema.json'),'utf8'));
const qa = path.join(root,'_qa');
await fs.mkdir(qa,{recursive:true});
const head = ['欄位名稱','欄位說明','型態','鍵','NULL','預設值','其他屬性／參照'];

function makeSheet(wb,name,title,context,headers,rows,widths){
 const sheet=wb.worksheets.add(name); sheet.showGridLines=false;
 const end=String.fromCharCode(64+headers.length), last=6+rows.length;
 sheet.getRange(`A1:${end}${last}`).format.font={name:'Microsoft JhengHei',size:11,color:'#26374F'};
 sheet.getRange(`A1:${end}${last}`).format.verticalAlignment='center';
 sheet.getRange('A2').values=[[title]];
 sheet.getRange('A2').format.font={name:'Microsoft JhengHei',size:16,bold:true,color:'#183354'};
 sheet.getRange('A3').values=[['樣版示例／示範結構；完全虛構，未包含資料列。']];
 sheet.getRange('A4').values=[[context]];
 sheet.getRange(`A6:${end}6`).values=[headers];
 sheet.getRange(`A7:${end}${last}`).setNumberFormat('@');
 sheet.getRange(`A7:${end}${last}`).values=rows;
 sheet.getRange(`A6:${end}${last}`).format.rowHeight=28;
 sheet.getRange(`A6:${end}6`).format={fill:'#365C86',font:{name:'Microsoft JhengHei',size:11,bold:true,color:'#FFFFFF'},horizontalAlignment:'center'};
 sheet.getRange(`A7:${end}${last}`).format.wrapText=true;
 rows.forEach((r,i)=>{if(i%2===0) sheet.getRange(`A${i+7}:${end}${i+7}`).format.fill='#EFF4F9';});
 widths.forEach((w,i)=>sheet.getRange(`${String.fromCharCode(65+i)}1:${String.fromCharCode(65+i)}${last}`).format.columnWidthPx=w);
 const table=sheet.tables.add(`A6:${end}${last}`,true,`Schema_${name}`);
 table.showFilterButton=true;
 sheet.freezePanes.freezeRows(6);
 return sheet;
}

for(const mode of ['tables','inventory']){
 const wb=Workbook.create(); const sheets=[];
 if(mode==='tables') for(const t of data.tables){
   sheets.push(makeSheet(wb,t.name,`${t.name} ${t.comment}`,`demo_shop  /  ${t.fields.length} 個欄位`,head,t.fields,[170,170,190,60,70,240,290]));
 }else{
   const rows=data.tables.flatMap(t=>t.fields.map(r=>[t.name,t.comment,...r]));
   sheets.push(makeSheet(wb,'field_inventory','demo_shop 欄位總覽清冊','3 張資料表  /  16 個欄位',['資料表','資料表說明',...head],rows,[150,140,160,160,180,55,65,220,280]));
 }
 wb.recalculate();
 const output=await SpreadsheetFile.exportXlsx(wb); await output.save(path.join(root,`xlsx-${mode}.xlsx`));
 for(const s of sheets){
   const preview=await wb.render({sheetName:s.name,autoCrop:'all',scale:1,format:'png'});
   await fs.writeFile(path.join(qa,`xlsx-${mode}-${s.name}.png`),new Uint8Array(await preview.arrayBuffer()));
 }
 console.log((await wb.inspect({kind:'sheet',include:'id,name',maxChars:2000})).ndjson);
 console.log(`Created xlsx-${mode}.xlsx with ${sheets.length} sheet(s).`);
}
