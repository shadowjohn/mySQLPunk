# 協作指南

這份文件講怎麼改這個專案：開發流程、測試、commit、發版，還有文件跟專案描述要怎麼跟著更新。不管是人還是 AI 協作，都照這份走。

## 開發流程

一次改一個明確的功能或修一個 bug，流程固定：

```powershell
git status --short --branch
git pull --rebase origin master

# 改東西

msbuild .\mySQLPunk.sln /p:Configuration=Release /p:Platform="Any CPU" /v:minimal /nologo
.\tests\Run-SmokeTests.ps1

git add <本次相關檔案>
git diff --cached          # 確認沒夾帶不相關的東西
git commit
git push origin master
```

幾條底線：

- 建置失敗、測試沒過，就不 commit、不 push。
- pull --rebase 撞到衝突，先解完、重新測試，再走 commit。
- 不用 `git push --force`。真的要改寫遠端歷史，先講清楚原因跟怎麼救回來。

## 測試

- `tests/Run-SmokeTests.ps1` 是基本盤，任何行為變更都要全過。改了行為導致既有測試的預期跟著變，就把測試一起更新，並在 commit message 說明為什麼。
- 跨平台 Core / Avalonia 有變更時，執行 `dotnet build mySQLPunk.CrossPlatform.sln -c Release` 與 `dotnet run --project mySQLPunk.CrossPlatform.SmokeTests/mySQLPunk.CrossPlatform.SmokeTests.csproj -c Release`；動到 MySQL / PostgreSQL provider 時再跑 `./tests/Run-CrossPlatformLiveTests.sh`。UI 變更還要在 Linux 或 macOS 實際開啟並走一次連線、SQL 與結果網格。
- 動到跨平台打包、安裝或 release workflow 時，至少用 `./scripts/package-cross-platform.sh --version <版本> --runtime linux-x64` 產生實際 self-contained 資產，再執行 `MYSQLPUNK_PACKAGE_RUN_UI=1 ./tests/Test-LinuxCrossPlatformPackage.sh <tar.gz>`，確認隔離安裝、Xvfb 啟動、解除安裝與路徑安全檢查皆通過；Linux x64／ARM64 另由 `ubuntu-24.04`／`ubuntu-24.04-arm` 原生 CI runner 執行安裝、UI 啟動、安全更新與 rollback。macOS app bundle、plist、CPU 架構、codesign、zip metadata、安全套用與啟動健康檢查，則分別由 `macos-15-intel` 與 `macos-15` 原生 runner 驗證。
- 動到 provider 實際行為（匯出匯入、使用者管理、rename）時，視影響跑對應的 Docker 整合測試：`Run-MySqlUserIntegrationTests.ps1`、`Run-MySqlExportRenameIntegrationTests.ps1`、`Run-DatabaseRenameProviderIntegrationTests.ps1`。
- UI 有變的話，除了測試，最好實際開起來看一眼。

## Commit message

- 第一行用白話繁中把「改了什麼」講清楚，讓沒看 diff 的人也知道發生什麼事。例如「連線右鍵重連、引擎形狀徽章，加上一整輪角落修正」。
- 可以加 `feat(scope):`、`fix(scope):` 這類前綴（scope 常用：query、designer、sqlite、sqlserver、oracle、copy、cli、connection、tree、export、ui），但前綴不是重點，第一行講人話才是。
- 內文用口語描述改了什麼、為什麼，不要機械式的逐檔條列。

## CHANGELOG 怎麼寫

`CHANGELOG.md` 不只是紀錄，發版腳本 `scripts/New-ReleaseNotes.ps1` 會直接拿它產生 GitHub Release 說明，格式有硬性檢查，寫錯發版會直接失敗：

1. 平常改動寫進 `## [Unreleased]` 底下，分兩段：`### 🚀 新增功能` 跟 `### 🛠️ 問題修正與優化`。這兩個標題（含 emoji）一個字都不能改。
2. 修正段落底下可以再用 `#### 小標` 分批次，發版腳本不管這層。
3. 發版時把 `## [Unreleased]` 改成 `## [x.y.z]`（版本號要跟 AssemblyInfo 一致），並確認 🚀 段的第一條長這樣：`- **一句話總結這一版**：...`。粗體那句會變成 GitHub Release 的標題。
4. 少了版本段、少了 🚀 或 🛠️ 標題、第一條沒有粗體總結，release workflow 都會中止。

## 發版流程

1. 改 `mySQLPunk/Properties/AssemblyInfo.cs` 的 `AssemblyVersion` 跟 `AssemblyFileVersion`。
2. 照上面的規則把 CHANGELOG 的 Unreleased 段轉成版本段。
3. 更新 README 的「最新版本」。
4. commit、push 之後打 tag：

```powershell
git tag v1.0.0.5
git push origin v1.0.0.5
```

workflow 會檢查 tag 版本跟 `AssemblyFileVersion` 一致，不一致直接失敗（不然程式內的更新檢查會一直以為有新版）。Linux x64、Linux ARM64、macOS Intel 與 macOS Apple Silicon runner 會先產生並上傳四種架構的 immutable workflow artifacts；每個 job 各自套用並啟動同架構 app。Windows runner 下載、核對四個壓縮檔與四個 `.sha256` 後，才會一次建立或更新包含九個資產的公開 Release，避免留下只有部分平台的版本。

## 文件怎麼維護

文件分三層，各管各的：

- **README.md**：給第一次來的人看的。功能概況表、已知限制、建置與測試方式。新功能做完就在功能表加一列或改既有那列；修掉「已知限制」裡的項目就把那條刪掉，發現新限制就補上。README 不放歷史流水帳。
- **docs/FEATURE_NOTES.md**：功能做完的細節紀錄。做完一個功能，實作細節、測試覆蓋、剩下的方向寫在這裡，README 只留一句話的狀態。
- **CHANGELOG.md**：對使用者的版本變更，照上面的規則寫。

一句話版本：CHANGELOG 寫「這版變了什麼」、README 寫「現在是什麼樣子」、FEATURE_NOTES 寫「當初怎麼做的」。

## 專案描述

專案描述有三個地方，改的時候三個一起改，不要讓它們各講各的：

1. **README 開頭那段**：完整版描述。
2. **GitHub repo 的 About**（描述與 topics）：用 `gh repo edit --description "..."` 或到 repo 頁面右上角改。
3. **AssemblyInfo.cs 的 `AssemblyDescription`**：會顯示在 exe 檔案內容裡的那句。

目前的一句話描述是：「免費開源的 Windows 資料庫管理工具（WinForms）：單一介面管理 MySQL / MariaDB、PostgreSQL、SQL Server、Oracle 與 SQLite」。要改方向（比如支援了新資料庫）就從這句改起，三處同步。

## 其它

- `history.md` 是本機筆記，已被 `.gitignore` 忽略；正式狀態以 git 紀錄跟 README 為準。
- `NuGet.Config` 把還原目錄固定在專案的 `packages/`，不要動它，不然別台機器會還原到全域路徑去。
