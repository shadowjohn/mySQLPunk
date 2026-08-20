# Changelog

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
