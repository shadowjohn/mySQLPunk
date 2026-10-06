# 報表精靈預覽檢查

日期：2026-10-06。範圍為獨立互動設計稿，不是 WinForms 功能驗收。

## 已確認

- 以使用者提供的 Windows 介面為依據，沿用上方功能列、左側連線樹、藍色選取態、細框線與 Segoe UI／微軟正黑體。
- 在 Codex 內建瀏覽器檢視桌面 1024px 與約 320px 的窄視窗；樣版卡片會由並排變成上下排列，控制項與文字可見。確認深色及淺色版面。
- DOCX／Excel／PDF 切換、各格式的兩款樣版選取，以及樣版範例放大預覽均可操作。
- 資料表預設 3 張全勾選；全部取消後「下一步」停用。搜尋 orders 後仍保留 3 張已選資料表，未隱含取消其他表。
- 輸出位置示範選擇可跨上一步／下一步保留；作者與標題保存在本機預覽狀態。
- 模擬匯出進度、取消、完成及返回設定可操作；進度期間重新開啟精靈不會留下失控計時器。
- 執行上述流程時，瀏覽器未回報 JavaScript 錯誤。
- 介面會明示示範資料與模擬進度，完成畫面不宣稱寫入真實報表。

## 樣本與限制

- 六份實際範例、縮圖和驗證資料見 `samples/manifest.json` 與 `samples/README.md`。
- DOCX 縮圖為明示的版面設計示意，DOCX 的 OOXML 與欄位內容已讀回；未使用 Word 或 LibreOffice 驗證實際分頁。
- XLSX 縮圖為 Artifact Tool 渲染，PDF 縮圖由最終 PDF 渲染；未使用真人 Excel 驗收。
- 對話內預覽的「開啟範例」先放大展示樣本頁；實際原檔透過對話檔案連結開啟。正式 WinForms 版預計使用系統預設程式開啟。
- 純示範 UI 不會讀取真實資料庫、寫入使用者指定輸出位置或執行正式取消／覆寫交易。
- 可重現原型來源存於本輪對話的 visualization 目錄；`embed-preview-assets.py` 負責嵌入同一組樣版與 Punky 素材。
- `preview-desktop.png` 是本機瀏覽器實際檢查時保存的畫面。

## 透明動畫追加驗證

- 使用者提供的黑底 MP4 保持不變；全尺寸與預覽 WebM 均實際解碼出 RGBA，六個時間點分別合成白底／深灰底目視檢查。
- 本機 Codex 瀏覽器可播放內嵌的 256px VP9 透明 WebM，讀回 readyState=4、paused=false、currentTime 有前進、muted=true、loop=true；動畫融入進度畫面的底色，沒有黑色方框。
- 預設遵守減少動態效果偏好；進行中可按「播放動畫／暫停動畫」切換。取消及完成後切回靜態圖。
- 已保存實際播放中的 `preview-loading.png`，另保存格式與樣版頁的 `preview-desktop.png`。瀏覽器未回報 JavaScript 錯誤。
- 原 MP4 已壓平 alpha，細髮不能完全還原；原片首尾姿勢本就不連續，本輪未修改動作。完整處理方式與影格證據見 `animation/README.md`。
- 無損 APNG 母版約 50 MB，僅留本機並由 Git ignore 排除。WinForms 播放控制項、實際匯出進度與大型 DB 驗收仍未實作。

final result: passed for preview-only scope
