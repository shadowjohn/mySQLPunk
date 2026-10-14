# 可驗證邏輯搬移：分階段矩陣

邏輯搬移讀取 schema／資料、轉換到目標 DDL、參數化寫入並驗證內容，不是原生 restore。既有一般 DataTransferService 的型別對映及列數 checkpoint、SchemaSyncScriptService 的手動修正 SQL，不具備這個工具的完整預檢與內容保證，沒有直接拿來當跨引擎還原器。

## 已落地方向

| 來源 → 目標 | 程式允許的版本 | 可執行範圍 | 驗證區分 |
| --- | --- | --- | --- |
| SQL Server → SQL Server | 2016/2017/2019/2022/2025 任意雙向及同版本，共 25 組 | Windows 既有專用工具；dbo heap、既有型別白名單、同定序、無 advanced features | 25 組預檢及交易 mock；不是 25 組真引擎驗證 |
| SQLite → SQLite | 3.37–3.53 | STRICT、INTEGER/BLOB、NULL/NOT NULL | 本機實際 SQLite 3.53.3；歷史 minor 版本未逐一跑 |
| SQLite → PostgreSQL | SQLite 3.37–3.53 → PostgreSQL 14–17 | INTEGER → bigint、BLOB → bytea | 既有隔離 CI PostgreSQL 16 fixture 有雙向內容 round-trip 測試；以該 SHA 的 CI 結果為準 |
| PostgreSQL → SQLite | PostgreSQL 14–17 → SQLite 3.37–3.53 | bigint → INTEGER、bytea → BLOB | 同上；14/15/17 未實機跑 |
| PostgreSQL → PostgreSQL | 14–17 雙向及同版本 | 同一 bigint/bytea 子集與全部預檢 | 邏輯共用路徑；沒有完整跨版本真機矩陣 |

SQLite／PostgreSQL 工具入口在 Avalonia 工作台「邏輯搬移」。來源使用目前連線的記憶體設定；目標選已儲存的連線及另建空資料庫，明確選版本，必要密碼只由使用者輸入，不額外讀秘密庫。獨立 provider owner 保有 SSH tunnel，所有開啟／失敗／結束都有 scope 釋放；不從 redacted ConnectionString 重建。

## 寫入前安全檢查

- SQLite 檔案必須已存在；來源唯讀開啟，目標 ReadWrite（不自動建立），使用 private cache、停用 pooling。缺檔、非 UTF-8、非 STRICT、virtual table、生成欄位、default、check、PK/FK、index、trigger、view、內部 sequence 等一律拒絕。
- SQLite CREATE TABLE 只接受可證明完整的 INTEGER/BLOB STRICT grammar；不是從欄位猜 DDL 後丟掉未辨識 clause。PostgreSQL 查正式 catalog：額外 schema、extension、view、sequence、constraint、index、default、identity、trigger（含 database event trigger）、function/procedure、rule、policy、user type、partition、inheritance、RLS、unlogged、large object、publication/subscription、自訂定序／轉換／運算子／文字搜尋物件等都阻擋。查詢失敗也不放行；不自動搬移帳號、權限或安全設定。
- 名稱限制 ASCII identifier 63 字元；轉到 SQLite 另阻擋只差大小寫的表／欄位。文字 encoding/collation、時間與浮點／decimal precision、identity/sequence 的忠實轉換尚未實作，因此不是靜默轉成 TEXT 或捨棄鍵／預設值。
- 預覽顯示實際版本、方向、目標、表、列數、轉換 SQL 與阻擋原因，不顯示資料值。來源內容也納入確認 fingerprint；輸入完整目標名稱才可執行，確認後來源內容（包括相同列數下的變更）／結構／實際引擎識別／目標變動會拒絕。
- SQLite／PostgreSQL 全部 DDL＋資料在單一目標交易。SQLite 用 immediate 交易；PostgreSQL 持有工具專用 advisory lock，來源全部表 SHARE lock 後讀取並在提交前再檢查。這不是在線無鎖搬移；請用隔離副本／維護時段，外部工具不會自動遵守 advisory lock，也不能宣稱能禁止其他管理者在最後檢查之後建立額外物件。
- 每批至多 100 列／200 參數，最多 100,000 列；每欄至多 1 MiB，每輪快照資料至多 64 MiB。驗證 schema、列數、Int64／binary／NULL 的 SHA-256 多重集合（保留重複列）。不保證隱含 rowid、實體排序或儲存配置跨引擎相同。
- 取消等實際工作停止，SQLite 用原生 progress handler，PostgreSQL 使用 provider cancellation；工作台 gate 持續到對話框關閉，不能同時 SQL／edit/delete/connect。變更目標／版本／認證即失效原預覽。

## 重試與恢復

沒有逐表 checkpoint：全部驗證後才提交。失敗且確認回滾的結果是 `RolledBack`，須重新預檢；已驗證提交是 `Verified`，不再寫入。commit 開始後失敗、回覆遺失或回滾失敗是 `Uncertain`，UI 停止再次預檢／寫入，禁止盲目重試，必須由 DBA 核對；不宣稱放棄 Task 就已停止或回滾。已有內容的目標即使是前次結果也不能自動 resume。

## 尚未實作、執行前拒絕

MySQL/MariaDB、SQL Server 與 SQLite/PostgreSQL 之間的通用 verified conversion 尚未接入；MySQL 目標的 implicit-commit DDL 不能沿用此單一交易保證，需另一套可驗證的 staging／恢復設計。Windows Oracle／Snowflake 也未加入此 verified 路徑。MongoDB、Redis/Garnet 等非關聯式 model 沒有通用無損表格映射，不能當同義還原目標。選不支援的 provider，服務在開連線前明確拒絕。

有 PK/FK、索引、字串定序、時間、decimal、identity、procedure/trigger 的一般資料庫不能在此階段搬移；替代路徑是引擎官方 schema/data migration 工具與人工轉換／驗證，不是宣稱 bacpac 可無限制降版。新版 SQL Server .bak 仍不能直接還原到舊引擎，compatibility level 不改備份格式；既有原生 restore 預檢繼續保留。

## 回歸證據

實際隔離 SQLite：1,005 列、多批、Int64 極值、空／非空 bytes、NULL、重複列、反向內容一致、錯誤確認、缺目標不自動建立、來源同列數內容改變、unsupported features、部分寫入失敗、內容／結構驗證失敗、途中取消，以及真正 commit 後注入 ack lost（保留資料且禁止重試）。不是單靠手動設 mock Blockers。

Avalonia/Xvfb：正式入口持有主視窗 gate、預覽、目標變更失效、錯誤確認不寫入、實際確認寫入且禁止重複 resume。既有跨平台 CI fixture 另測 SQLite ↔ PostgreSQL 16，包括正式 catalog 逐項加 feature／移除 feature 後恢復可預檢。

真 SQL Server 跨版本、SQL auth+SSH 真機、PostgreSQL 14/15/17、SQLite 歷史版本及非關聯式轉換皆沒有完整矩陣證據；不得把 mock／同版本一般 provider round-trip 當全部資料庫版本已通過。
