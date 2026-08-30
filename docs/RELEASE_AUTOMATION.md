# 自動發版規則

mySQLPunk 採「日常修改持續累積、重大里程碑才發布」的發版閘門。`master` 每次 push 都會重新判斷，但 commit 數量、累積分數或經過天數本身都不會建立 Release。

## 不會自動發版的情況

一般 `feat`、`fix`、`perf`、`refactor` 與其他日常 commit 都只會進入 `[Unreleased]`。Gate 仍會統計尚未發版的程式 commit 與參考分數，方便判斷批次規模，但統計值不會觸發發版；也沒有「七天到了就發版」的排程。

因此，即使連續修正很多小問題，也會保留在同一個未發版批次，直到團隊明確判定已形成值得使用者下載的重大里程碑。

## 重大里程碑自動發布

重大功能批次完成且已驗證後，在繁體中文 Conventional Commit 的 body 最後加一行機器指令：

```text
feat(model): 新增完整模型設計器

原因：補上資料庫模型的建立、編輯與逆向工程。
調整：新增模型畫布、關聯線與 DDL 預覽。
影響：使用者可以直接從現有資料庫建立並修改模型。

Release-Now: true
```

`type(scope): 繁體中文主旨` 與原因／調整／影響仍維持繁體中文；`Release-Now: true` 是給 workflow 看的固定 trailer，只能用在已完成且已驗證的重大里程碑。`feat(scope)!:` 或 `BREAKING CHANGE:` 也會立即發布，這兩種只用在不相容變更。

## 發版前提

平常只需持續維護 `CHANGELOG.md` 的 `[Unreleased]`，並包含：

- `### 🚀 新增功能`
- `### 🛠️ 問題修正與優化`
- 至少一個粗體重點項目，供 Release 標題使用

目前採四段版號，例如從 `v1.0.0.19` 自動遞增為 `v1.0.0.20`。重大里程碑被明確標記後，workflow 會：

1. 把完整 `[Unreleased]` 原子轉成帶日期的下一版本段落，並建立新的空白 `[Unreleased]`。
2. 更新 `AssemblyInfo.cs` 與 README 版號。
3. 建立 `chore(release): 發佈 v...` 繁體中文 commit。
4. 建立並 push tag。
5. 明確 dispatch `release.yml`；Ubuntu／macOS runner 先建置並驗證 Linux x64／ARM64 與 macOS Intel／Apple Silicon self-contained 資產及 SHA-256，Windows job 再完成 setup EXE、核對九個檔案齊全並一次發布。

若 `[Unreleased]` 缺少必要標題或粗體重點，判斷會顯示原因並停止，不會發布半成品。

## 手動方式

在 GitHub Actions 執行 `Auto Release Gate`，勾選 `force` 可發布已人工核准的完整批次；changelog、版號與建置驗證仍不可略過。原本直接 push `v*` tag 或手動執行 `Release` workflow 的方式也保留，供緊急修正版使用。

本機可先檢查目前是否帶有重大里程碑標記：

```powershell
.\scripts\Invoke-AutoReleaseDecision.ps1
.\tests\Test-AutoReleasePolicy.ps1
```
