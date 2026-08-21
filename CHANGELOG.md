# Changelog

## [Unreleased]

### 介面改版

- 新增 `UiKit` / `UiControls` 設計系統：色彩、間距、圓角、字級 token，以及純程式繪製的向量圖示與區塊標題列、分段控制項、空狀態等共用元件。
- `ThemeManager` 重寫為單一樣式來源，工具列、功能表、按鈕、輸入框、分頁、樹狀與資料表格改為自訂繪製，淺色與深色主題共用同一組 token。
- 主功能列與物件工具列改用向量圖示，移除 resx 內的預設佔位圖（原本顯示為破圖），選取狀態由生硬藍色方框改為柔和圓角底加強調色底線。
- 樹狀清單改為全自繪：移除虛線連接線與焦點虛線框，加入整列圓角選取、滑鼠停留回饋與自訂展開箭號。
- 「連線」與「物件詳細資料」面板加上一致的區塊標題列，側欄的資訊／DDL 切換改為分段控制項。
- 樹狀、內容區與側欄在沒有資料時顯示空狀態說明，狀態列右側新增連線數摘要。
- 選取連線類型視窗改用停駐排版，修正搜尋框與檢視切換鈕被推出可視範圍的問題，並替卡片加上滑鼠停留回饋。
- 選項視窗左側導覽改為圓角分頁樣式，動作列加上分隔線與主要按鈕強調。
- 查詢視窗工具列與資料列工具列改用向量圖示，取代原本的文字符號。

### 修正（本輪錯誤掃修）

- 資料表資料分頁：儲存目標改以實際載入資料的 SQL 為準。原本是抓編輯器當下文字，先改文字再按儲存會把修改寫進別張資料表。
- 資料表資料分頁：執行 JOIN 或非單表查詢後結果自動鎖唯讀；主鍵欄位不在結果集或主鍵值被修改時，儲存會明確報錯而不是默默出錯或假裝成功。
- 查詢視窗：F5／Ctrl+Enter 在查詢執行中不再重入（同一條連線同時跑兩個查詢會互咬，取消鈕也會失效）。
- 資料表設計師（MySQL）：修正 `int(10) unsigned` 的 unsigned 被塞進長度、`enum('a','b','c')` 被逗號切爛只剩前兩個值的問題；ALTER 路徑的 DEFAULT 改用與 CREATE 相同的跳脫邏輯，`CURRENT_TIMESTAMP` 與含引號的預設值不再產生錯誤 SQL。
- SQLite：修正把 `docs_archive` 這類正常資料表誤判為 FTS 影子表而藏起來的問題（改為只認已知影子表後綴）；欄位註解讀取失敗時不再清空全部註解（原本會在儲存時把空註解寫回）。
- 樹狀清單：群組節點「重新整理」與複製貼上後改為等載入完成再選取節點（原本會遍歷剛清空的樹，選取永遠失效）；雙擊 Tables/Views 群組節點不再讓狀態列卡在「正在載入資料...」。
- 頁籤：往右拖曳排序不再多跑一格；浮動視窗停靠回主視窗時若有未儲存的變更，不再直接銷毀（原本同名分頁存在時會連未存的編輯一起消失）。
- 靜默失敗浮出：資料表清單載入失敗會清空清單並在狀態列說明（原本會留著上一個資料庫的清單）；選項視窗存檔失敗會跳提示（原本重開程式設定就消失）；自動備份草稿寫檔失敗不再讓程式當掉，且草稿檔名加入分頁標題、同庫多分頁不再互相覆蓋。
- PostgreSQL：連線改用連線設定裡填的初始資料庫（原本寫死連 `postgres`，帳號無權限時直接失敗）。
- 介面框架：修正 `SetGlyph` 會釋放 `Properties.Resources` 共用圖片導致點「使用者」分類時當掉；移除切換主題時逐列逐格設定樣式造成大結果集卡死的程式碼；控制項停用/唯讀狀態改變後顏色會跟著更新；樹狀停留改為只重畫該列減少閃爍；修掉多處事件重複訂閱與 GDI 資源未釋放。
- 移除未被編譯的空殼檔 `lib/oracle_add_edit.cs`。

### 修正

- 修正 `ThemeManager` 對 `ToolStripTextBox` / `ToolStripComboBox` 套用透明背景時會丟出「控制項不支援透明的背景色彩」例外。
- 側欄圖示改為程式繪製，不再以 `Image.FromFile` 長期鎖住 `image/` 下的檔案。

### 驗證

- Windows Release 建置與 47 項 SmokeTests 通過。
- 以 SQLite 測試資料庫實際驗證主視窗、連線精靈、選項視窗、資料表資料視窗在淺色與深色主題下的呈現。

## [1.0.0.4] - 2026-08-20

### 單一 EXE 安裝與更新

- GitHub Release 改為只發布一個 `mySQLPunk-1.0.0.4-win-x64-setup.exe`，不再要求使用者下載 portable ZIP 與獨立 manifest。
- 新增 Inno Setup 安裝流程，將 managed DLL、SQLite／SpatiaLite 原生 runtime、素材與第三方授權檔封裝在同一個 installer 內。
- 程式內更新會讀取 GitHub release asset 的 SHA-256 digest，下載後先完成校驗才啟動安裝程式。
- 發版 workflow 會清除同版本殘留的舊資產，並拒絕發布零個或多個 setup EXE。

### 資料庫功能

- 完成 MySQL／MariaDB 資料庫完整匯出與匯入，支援 Table、View、Function、Procedure、Trigger、資料、索引、外鍵、註解與大型檔案串流處理。
- 完成 Database Tree 的 F2／右鍵改名，並支援 MySQL／MariaDB、SQLite、PostgreSQL 與 SQL Server 的實際資料庫改名流程。
- 完成 MySQL／MariaDB 使用者、角色、權限與資源限制管理。
- 持續補齊檢視選單、選項中心、暗色主題、繁體中文／英文語系與各 provider 的資料操作能力。

### 驗證

- Windows Release 建置與 47 項 SmokeTests 通過。
- MySQL 5.6、5.7、8.0，以及 MariaDB 10.6、10.11、11.4 的匯出、匯入與複製改名整合測試通過。
- SQLite、PostgreSQL 17 與 SQL Server 2022 的資料庫改名整合測試通過。
- 單一 EXE 已實際完成封裝、靜默安裝、必要 runtime 檢查、主視窗啟動、關閉與解除安裝測試。

## [1.0.0.3] - 2026-06-11

### Release Highlights

- Prepared the next public release after the `v1.0.0.2` compliance pass.
- Rebuilt and verified the SpatiaLite runtime package metadata, including `built_at_utc`, `build_tool`, and per-file SHA-256/byte checks.
- Kept `sqlite3.exe`, `libreadline8.dll`, and `libtermcap-0.dll` out of the packaged runtime to avoid shipping the unused Readline-linked SQLite shell.
- Added smoke-test coverage for the committed SpatiaLite runtime manifest so stale hashes or blocked files are caught before release.

### Updates And Packaging

- Added portable ZIP update support that can download a GitHub Release asset, verify it with `release-manifest.json`, generate an apply script, replace the current app after exit, and restart `mySQLPunk.exe`.
- Hardened GitHub Release packaging with bundled root notices, image asset notices, native runtime notices, and NuGet license/notice files, including the Oracle Managed Data Access license.
- Improved release manifest verification for portable update assets and SpatiaLite native runtime files.
- Updated the release workflow so GitHub Release notes can be generated from this changelog instead of a placeholder body.

### Database And UI Improvements

- Added the option to hide database object groups, allowing Tables, Views, Functions, Users, Events, and Queries to appear directly under each database node.
- Improved View/Table copy and provider SQL fallback messages so unsupported or failed conversions explain the reason more consistently.
- Localized many UI, diagnostics, backup, import/export, provider, metadata, SpatiaLite, update, proxy, registration, and error fallback messages in Traditional Chinese and English.
- Normalized empty or missing exception reasons to localized unknown-error messages instead of blank dialogs.

### Notes

- This release focuses on packaging, update flow, localization, diagnostics, and third-party notice cleanup.
- The existing `v1.0.0.2` GitHub Release does not include these changes; publish this release with tag `v1.0.0.3`.
- Installer-based updates are still a future enhancement; the current release asset is the portable ZIP plus `release-manifest.json`.
