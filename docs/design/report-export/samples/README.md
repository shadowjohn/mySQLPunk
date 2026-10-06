# 報表樣版示例

所有內容來自 `demo-schema.json` 的虛構 `demo_shop` 結構，包含 3 張資料表與 16 個欄位，沒有資料列、連線資訊或憑證。這些是 UI 設計預覽可開啟的小型樣本，尚未接上 mySQLPunk 匯出功能。

| 範例 | 樣版 | 縮圖來源 |
| --- | --- | --- |
| `docx-classic.docx` | 經典資料字典 | 獨立排版的設計示意；不是 Office 轉檔 |
| `docx-delivery.docx` | 專案交付文件 | 獨立排版的設計示意；不是 Office 轉檔 |
| `xlsx-tables.xlsx` | 一表一工作表，3 個工作表 | Artifact Tool 渲染 customers 工作表 |
| `xlsx-inventory.xlsx` | 欄位總覽清冊，1 個工作表 | Artifact Tool 渲染欄位清冊 |
| `pdf-standard.pdf` | 標準結構手冊，3 頁 | 最終 PDF 第一頁實際渲染 |
| `pdf-compact.pdf` | 精簡審閱版，1 頁 | 最終 PDF 實際渲染 |

每個範例有同檔名的 `.png` 縮圖。DOCX 縮圖包含「設計示意，非 Office 實際轉檔畫面」標示。PDF 已逐頁檢視；XLSX 已檢視全部工作表的 Artifact Tool 預覽。DOCX 已驗證 OOXML 完整性並重新讀取所有表格內容；目前 Windows bundled runtime 未提供 LibreOffice，未啟動 Word，因此 DOCX 實際分頁仍待 Office 驗收。Excel 尚未啟動，Artifact Tool 預覽中的小數預設值可能顯示為 `0`，檔案內保留文字 `0.00`。

`manifest.json` 提供原型可用的檔名、樣版名稱、縮圖性質與驗證紀錄。

## 重新產生

使用 Codex `load_workspace_dependencies` 回傳的 Python、Node 與套件。`build_documents.py` 建立 DOCX/PDF；`build_spreadsheets.mjs` 使用 `@oai/artifact-tool` 建立 XLSX 和工作表預覽，需要將本資料夾的 `node_modules` 暫時連結到 bundled Node 套件目錄。

將 `pdf-standard.pdf`、`pdf-compact.pdf` 及 `_qa/*-illustration.pdf` 用 bundled Poppler 的 `pdftoppm -scale-to 1400 -png` 轉圖到 `_qa/`，前綴分別是 `pdf-standard`、`pdf-compact`、`docx-classic`、`docx-delivery`。逐頁目視檢查後執行 `finalize_samples.py`，產生六張縮圖並逐格比對原始結構與 DOCX/XLSX。

`_qa/` 是可重建的檢查中間檔，不是原型必需資源；`node_modules` 連結不應加入版本控制。
