# 下一批發布準備（尚未核准發布）

建議候選版號 `1.0.0.23`，保持目前四段版本規則；這不是已發布版本，也不在本輪改 AssemblyVersion／README 發版版本、打 tag、加 Release-Now 或 dispatch force。

候選重點：已合併的連線 ownership、操作 gate、checkpoint、DDL trigger 補正，SQLite SQL 背景執行與真正取消，以及受限雙向／跨引擎邏輯搬移。Release 說明必須明列 schema/data 子集與真機矩陣限制，不能寫成「所有資料庫都可無損還原」。

發布前還需要本輪獨立驗收，以及待發布精確 SHA 的 Windows／四架構 CI 成功。SQLite 修復 head `187b5c3ba55deef07c9d82bead45fa03df56984d` 的 PR #44 CI 已通過：Windows `38507339195`、跨平台 `38507339160`；後續功能 head 的證據另存交付記錄，不以初輪 CI 替代。

收到同一工作階段的最終發布指示後，沿用 `Auto Release Gate` 的 `force` 原子版更完整 Unreleased 批次；流程會同步 CHANGELOG、AssemblyInfo、README、tag 及既有 Release workflow。繼續要求 Windows setup、四種 self-contained 壓縮檔、四個 SHA-256 共九資產一次齊全，沿用既有簽章／macOS ad-hoc 狀態，不換發布目的地、不繞過建置或簽章檢查。
