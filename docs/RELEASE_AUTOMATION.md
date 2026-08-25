# 自動發版規則

mySQLPunk 採「小修改先累積、大更新可立即發布」的發版閘門。`master` 每次 push 與每日排程都會重新判斷，但不是每個 commit 都建立 Release。

## 累積分數

| Commit 類型 | 分數 | 說明 |
|---|---:|---|
| `feat` | 2 | 使用者看得到的新功能 |
| `fix`、`perf`、`refactor`、`revert` | 1 | 修正、效能或程式行為調整 |
| `docs`、`test`、`style`、`chore` | 0 | 不單獨觸發產品發版 |

累積到 5 分會自動發布。若已有至少一筆有效程式變更，但七天仍未達門檻，每日排程會把這批小修改一起發布，避免改動永遠卡著。

## 單一大更新立即發布

大更新不需要湊滿 5 分。在繁體中文 Conventional Commit 的 body 最後加一行機器指令：

```text
feat(model): 新增完整模型設計器

原因：補上資料庫模型的建立、編輯與逆向工程。
調整：新增模型畫布、關聯線與 DDL 預覽。
影響：使用者可以直接從現有資料庫建立並修改模型。

Release-Now: true
```

`type(scope): 繁體中文主旨` 與原因／調整／影響仍維持繁體中文；`Release-Now: true` 是給 workflow 看的固定 trailer。`feat(scope)!:` 或 `BREAKING CHANGE:` 也會立即發布，這兩種只用在不相容變更。

## 發版前提

自動發布前必須先在 `CHANGELOG.md` 建立下一版本段落，並包含：

- `### 🚀 新增功能`
- `### 🛠️ 問題修正與優化`
- 至少一個粗體重點項目，供 Release 標題使用

目前採四段版號，會從 `v1.0.0.15` 自動遞增為 `v1.0.0.16`。達門檻後，workflow 會：

1. 更新 `AssemblyInfo.cs` 與 README 版號。
2. 建立 `chore(release): 發佈 v...` 繁體中文 commit。
3. 建立並 push tag。
4. 明確 dispatch `release.yml`，建置、打包並發布單一 Windows x64 setup EXE。

若 changelog 尚未準備好，判斷會顯示原因並繼續累積，不會發布半成品。

## 手動方式

在 GitHub Actions 執行 `Auto Release Gate`，勾選 `force` 可以忽略分數與七天門檻；changelog、版號與建置驗證仍不可略過。原本直接 push `v*` tag 或手動執行 `Release` workflow 的方式也保留，供緊急修正版使用。

本機可先檢查目前是否達門檻：

```powershell
.\scripts\Invoke-AutoReleaseDecision.ps1
.\tests\Test-AutoReleasePolicy.ps1
```
