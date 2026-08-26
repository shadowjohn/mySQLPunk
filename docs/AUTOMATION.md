# 自動執行作業

mySQLPunk 可以把常用的唯讀查詢、查詢結果匯出與資料庫 SQL 備份保存成作業，立即執行或註冊到 Windows 工作排程器每天執行。入口在「工具 > 自動執行作業」。

## 支援的作業

| 類型 | 行為 | 執行紀錄 |
| --- | --- | --- |
| 查詢 | 執行單一唯讀 SQL，不輸出檔案 | 成功／失敗、資料列數、耗時、原因 |
| 匯出 | 執行單一唯讀 SQL，輸出 CSV、XLSX、TSV、JSON、XML、HTML、Markdown 或 SQL | 成功／失敗、資料列數、輸出路徑、耗時、原因 |
| 備份 | 使用既有 `DatabaseDumpService` 建立指定資料庫的邏輯 SQL 備份 | 成功／失敗、輸出路徑、耗時、原因 |

查詢與匯出會先經過保守的唯讀檢查，只接受單一 `SELECT`、`WITH ... SELECT`、`SHOW`、`EXPLAIN`、`DESC` 或 `DESCRIBE`。多段 SQL、DML／DDL、`SELECT INTO` 與可能實際執行查詢的 `EXPLAIN ANALYZE` 都會被拒絕。

## 建立與排程

1. 先在連線設定中保存可使用的連線與密碼。
2. 開啟「工具 > 自動執行作業」，按「新增」。
3. 選擇作業類型、連線設定檔、連線與資料庫；查詢或匯出再填入 SQL。
4. 匯出或備份需指定輸出路徑。相對路徑會放在「文件\mySQLPunk\automation-output」下。
5. 如要每天執行，勾選「啟用每日排程」並選時間。儲存後會建立或更新 Windows 工作排程。

輸出路徑可以使用下列替代文字：

- `{job}`：移除不合法檔名字元後的作業名稱
- `{yyyyMMdd}`：執行日期
- `{yyyyMMdd_HHmmss}`：執行日期與時間

例如 `exports\{job}-{yyyyMMdd_HHmmss}.csv`。

## 認證與執行權限

- 作業 JSON 只保存連線設定檔名稱與連線顯示名稱，不保存主機連線字串、使用者密碼或 API 金鑰。
- Windows 工作使用 `InteractiveToken` 與最低權限執行，不要求系統管理員權限。
- 排程必須在建立排程的同一個 Windows 使用者已登入時執行，才能讀取該使用者的 Windows Credential Manager。
- 若搬動或重新安裝 `mySQLPunk.exe`，請在管理畫面重新註冊排程，讓執行檔路徑同步更新。

## 作業與紀錄位置

預設根目錄為 `%LOCALAPPDATA%\mySQLPunk\automation`：

```text
automation/
├─ jobs/                 一個作業一個 JSON
└─ runs/<job-id>/        每次執行一個 JSON 紀錄
```

執行紀錄不複製 SQL 或認證，只保存作業識別、狀態、時間、耗時、資料列數、輸出路徑與錯誤原因。在管理畫面按「開啟作業資料夾」可以直接查看這些檔案；JSON 作業也可複製到另一台電腦後重新選用當地同名連線。

## 命令列執行

Windows 工作排程器實際呼叫同一個 CLI 入口，也可以手動測試：

```powershell
.\mySQLPunk.exe --run-scheduled-job "$env:LOCALAPPDATA\mySQLPunk\automation\jobs\<job-id>.json"
```

成功回傳 exit code `0`，作業執行失敗回傳 `1`，參數格式錯誤回傳 `2`。不論由畫面、CLI 或工作排程器啟動，都會寫入相同的執行紀錄。

## 目前限制

- 第一版只有每日固定時間，還沒有每小時、每週或複雜觸發條件。
- 尚未加入匯入、跨資料庫傳輸、郵件／Webhook 通知與失敗重試。
- 排程採「使用者已登入才執行」，不適合無人登入的 Windows Server 服務帳號情境。
