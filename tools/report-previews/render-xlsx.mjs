import fs from 'node:fs/promises';
import path from 'node:path';
import { FileBlob, SpreadsheetFile } from '@oai/artifact-tool';
import sharp from 'sharp';

const [destinationArg, qaArg] = process.argv.slice(2);
if (!destinationArg || !qaArg) throw new Error('Usage: render-xlsx.mjs <report-templates> <qa-directory>');
const destination = path.resolve(destinationArg), qa = path.resolve(qaArg);
const jobs = JSON.parse(await fs.readFile(path.join(qa, 'xlsx-sheets.json'), 'utf8'));
for (const { stem, sheets } of jobs) {
  const workbook = await SpreadsheetFile.importXlsx(await FileBlob.load(path.join(destination, `${stem}.xlsx`)));
  console.log((await workbook.inspect({ kind: 'sheet', include: 'id,name', maxChars: 2000 })).ndjson);
  for (const [index, sheetName] of sheets.entries()) {
    const blob = await workbook.render({ sheetName, autoCrop: 'all', scale: 1, format: 'png' });
    const png = Buffer.from(await blob.arrayBuffer());
    const filename = path.join(qa, `${stem}-sheet-${index + 1}.png`);
    await fs.writeFile(filename, png);
    if (index === 0) {
      const resized = await sharp(png).resize({ width: 1000, height: 950, fit: 'inside', withoutEnlargement: true }).png().toBuffer();
      const info = await sharp(resized).metadata();
      const label = Buffer.from(`<svg width="${info.width}" height="32"><rect width="100%" height="100%" fill="white"/><text x="8" y="22" font-family="Microsoft JhengHei" font-size="14" fill="#86521b">Artifact Tool 預覽，非 Excel 截圖</text></svg>`);
      await sharp({ create: { width: info.width, height: info.height + 32, channels: 3, background: 'white' } })
        .composite([{ input: label, left: 0, top: 0 }, { input: resized, left: 0, top: 32 }])
        .png().toFile(path.join(destination, `${stem}.png`));
    }
    console.log(`${stem}: rendered ${sheetName}`);
  }
}
