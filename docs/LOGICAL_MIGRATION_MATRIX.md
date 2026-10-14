# 可驗證邏輯搬移：分階段矩陣

邏輯搬移讀取 schema／資料、轉換到目標 DDL、參數化寫入並驗證內容，不是原生 restore。既有一般 DataTransferService 的型別對映及列數 checkpoint、SchemaSyncScriptService 的手動修正 SQL，不具備這個工具的完整預檢與內容保證，沒有直接拿來當跨引擎還原器。

## 已落地方向

| 來源 → 目標 | 程式允許的版本 | 可執行範圍 | 驗證區分 |
| --- | --- | --- | --- |
| SQL Server → SQL Server | 2016/2017/2019/2022/2025 任意雙向及同版本，共 25 組 | Windows 既有專用工具；dbo heap、既有型別白名單、同定序、無 advanced features | 25 組預檢及交易 mock；不是 25 組真引擎驗證 |
| SQLite → SQLite | 3.37–3.53 | STRICT、INTEGER/BLOB/TEXT、NULL/NOT NULL、非自增 PK、普通索引；詳細子集見下文 | 本機實際 SQLite 3.53.3；歷史 minor 版本未逐一跑 |
| SQLite → PostgreSQL | SQLite 3.37–3.53 → PostgreSQL 14–17 | INTEGER → bigint、BLOB → bytea、TEXT BINARY → text C；非自增 PK、普通索引 | 隔離 CI PostgreSQL 16 fixture 加入文字／鍵／索引雙向 round-trip；只以這次新 head 結果為證據 |
| PostgreSQL → SQLite | PostgreSQL 14–17 → SQLite 3.37–3.53 | 上述型別反向映射；PK → WITHOUT ROWID、普通索引 | 同上；14/15/17 未實機跑 |
| PostgreSQL → PostgreSQL | 14–17 雙向及同版本 | 同一 bigint/bytea/text C、PK／索引子集與全部預檢 | 邏輯共用路徑；沒有完整跨版本真機矩陣 |

## 第二階段：實用文字與鍵索引子集

實作適用明確手動給 ID 的業務表：例如 `customers(id INTEGER NOT NULL, label TEXT COLLATE BINARY NOT NULL, note TEXT, payload BLOB, PRIMARY KEY(id)) STRICT, WITHOUT ROWID`，可帶 `customer_label(label)`、`customer_note(note,id)` 普通索引；複合主鍵如 `lines(customer,code)` 同樣保留順序、唯一性及 NOT NULL。所有索引 DDL 都列在確認預覽，建立後正式 catalog 再讀回驗證，缺索引／改欄位順序也會整批回滾。

- SQLite 文字只接受 BINARY（隱含或明確），PostgreSQL 必須是內建 `pg_catalog."C"`；兩端 UTF-8，逐欄嚴格解碼，拒絕 NUL、無效 UTF-8，不偷偷替換字元。保留空字串、NULL、Unicode、大小寫與 byte-order 比較語意；不承諾兩引擎字串函式或 LIKE 行為相同。
- SQLite PK 表要求完整可辨識 grammar、明確 NOT NULL 與 WITHOUT ROWID，避免 INTEGER PRIMARY KEY 的 rowid alias 自動給 ID 行為被偷偷改成 PostgreSQL 無 sequence 的 bigint。單欄整數／文字／二進位與複合 PK 均可；不保證引擎自動產生的 PK constraint／內部索引名稱或實體配置相同。
- 普通非 unique btree 索引保留名稱、欄位與順序，只允許升冪、預設內建 opclass、無 expression／partial／INCLUDE／自訂 storage。SQLite 升冪 NULL 在前，目標 PostgreSQL 明確 `ASC NULLS FIRST`；反向 nullable 欄位索引若為 PostgreSQL 預設 NULLS LAST 則拒絕，NOT NULL 索引沒有這項差異。DESC、unique（PK 除外）、自訂定序／opclass、clustered／replica identity 索引都阻擋。
- SQLite／PostgreSQL schema＋索引＋資料仍為單一交易，不引入逐表 partial success。每次重新預檢／最終驗證涵蓋 PK、索引、內容；已提交或 ack lost 不可盲重試。
- 有界讀取先在 SQL 投影取 byte length、超限 CASE 回傳 NULL，再檢查預算，才 materialize 欄位。BLOB 不會先 GetValue 完整大陣列；SQLite expression 用已限額的 byte[] 讀取（避開 provider GetBytes 的 metadata 路徑），PostgreSQL 分段讀取。NULL 與空欄位至少計入 8 bytes 預算。這是應用端快照限額，不是對引擎自身 page cache／SQL 執行記憶體的硬上限。

SQLite／PostgreSQL 工具入口在 Avalonia 工作台「邏輯搬移」。來源使用目前連線的記憶體設定；目標選已儲存的連線及另建空資料庫，明確選版本，必要密碼只由使用者輸入，不額外讀秘密庫。獨立 provider owner 保有 SSH tunnel，所有開啟／失敗／結束都有 scope 釋放；不從 redacted ConnectionString 重建。

## 寫入前安全檢查

- SQLite 檔案必須已存在；來源唯讀開啟，目標 ReadWrite（不自動建立），使用 private cache、停用 pooling。缺檔、非 UTF-8、非 STRICT、virtual table、生成欄位、default、check、FK、不支援的鍵／索引、trigger、view、內部 sequence 等一律拒絕。
- SQLite CREATE TABLE／INDEX 只接受可證明完整的安全 grammar；不是從欄位猜 DDL 後丟掉未辨識 clause。PostgreSQL 查正式 catalog，只有上述 PK／普通索引白名單放行：額外 schema、extension、view、sequence、其他 constraint/index、default、identity、trigger（含 database event trigger）、function/procedure、rule、policy、user type、partition、inheritance、RLS、unlogged、large object、publication/subscription、自訂定序／轉換／運算子／文字搜尋物件等都阻擋。查詢失敗也不放行；不自動搬移帳號、權限或安全設定。
- 名稱限制 ASCII identifier 63 字元；轉到 SQLite 另阻擋只差大小寫的表／欄位／全域索引名稱。其他 encoding/collation、時間與浮點／decimal precision、identity/sequence 仍拒絕，不靜默轉成 TEXT 或捨棄鍵／預設值。
- 預覽顯示實際版本、方向、目標、表、列數、轉換 SQL 與阻擋原因，不顯示資料值。來源內容也納入確認 fingerprint；輸入完整目標名稱才可執行，確認後來源內容（包括相同列數下的變更）／結構／實際引擎識別／目標變動會拒絕。
- SQLite／PostgreSQL 全部 DDL＋資料在單一目標交易。SQLite 用 immediate 交易；PostgreSQL 持有工具專用 advisory lock，來源全部表 SHARE lock 後讀取並在提交前再檢查。這不是在線無鎖搬移；請用隔離副本／維護時段，外部工具不會自動遵守 advisory lock，也不能宣稱能禁止其他管理者在最後檢查之後建立額外物件。
- 每批至多 100 列／200 參數，最多 100,000 列；每欄至多 1 MiB，每輪快照資料至多 64 MiB。驗證 schema、PK／索引、列數、Int64／text／binary／NULL 的 SHA-256 多重集合（保留重複列）。不保證隱含 rowid、實體排序或儲存配置跨引擎相同。
- 取消等實際工作停止，SQLite 用原生 progress handler，PostgreSQL 使用 provider cancellation；工作台 gate 持續到對話框關閉，不能同時 SQL／edit/delete/connect。變更目標／版本／認證即失效原預覽。

## 重試與恢復

沒有逐表 checkpoint：全部驗證後才提交。失敗且確認回滾的結果是 `RolledBack`，須重新預檢；已驗證提交是 `Verified`，不再寫入。commit 開始後失敗、回覆遺失或回滾失敗是 `Uncertain`，UI 停止再次預檢／寫入，禁止盲目重試，必須由 DBA 核對；不宣稱放棄 Task 就已停止或回滾。已有內容的目標即使是前次結果也不能自動 resume。

## 其他方向：目前阻擋與必要技術工作

| 方向 | 現況／執行前拒絕的技術理由 | 下一階段必要能力（不是本輪已通過） |
| --- | --- | --- |
| MySQL/MariaDB → SQLite/PG/SQLServer | 整數 unsigned 上界、zero date、ENUM/SET、charset、PAD SPACE 定序、AUTO_INCREMENT 與 storage engine 一致快照不能用現有 count-only transfer 當忠實轉換 | 來源 catalog／版本／InnoDB 快照 adapter，逐欄區間及 charset/定序證明；不可先沿用一般 type map 再補驗證 |
| SQLite/PG/SQLServer → MySQL/MariaDB，以及 MySQL↔MariaDB／跨版本 | MySQL DDL implicit commit，DDL＋資料單一交易會是假保證；目標即使原本空白，取消仍可能已留下表 | 另建 staging 與 durable uncertain/checkpoint 協議、預檢引擎版本差異、人工核對恢復；不能自動刪 staging 或覆蓋目標假裝 rollback |
| SQLServer → SQLite/PG/MySQL，以及反向 | UTF-16/UTF-8、linguistic/BIN2 定序的比較／尾空白規則、rowversion、decimal/time scale、identity/default/sequence 不能把現有型別名稱替換當成保留語意 | 可重用 SQLServer catalog 的阻擋規則，但需跨 provider canonical schema／資料 codec、來源一致快照與逐欄語意／版本證據；目前只 Windows SQLServer→SQLServer 專用子集可執行 |
| Oracle/Snowflake 與其他引擎 | NUMBER／時間、空字串 NULL、identifier/collation 與交易 DDL 不等價 | 專用 catalog／codec／交易恢復設計；本輪不開連線 |
| MongoDB、Redis/Garnet 與表格引擎 | document/key-value model、TTL／原子操作／巢狀內容沒有通用無損表格映射 | 明確專用模型轉換契約，不當成同義 restore |

上述方向服務在開連線前拒絕，沒有聲稱某個 MySQL/MariaDB 或 SQLServer 版本已跨引擎通過。雙向跨版本／換引擎的整體需求仍未全部完成；此批是實際擴充 SQLite/PG 的可驗證第二階段，而不是全引擎完成或發版的宣告。

有 FK、其他字串定序、時間、decimal、identity、procedure/trigger 的一般資料庫仍須使用引擎官方 schema/data migration 工具與人工轉換／驗證，不是宣稱 bacpac 可無限制降版。新版 SQL Server .bak 仍不能直接還原到舊引擎，compatibility level 不改備份格式；既有原生 restore 預檢繼續保留。

## 回歸證據

實際隔離 SQLite：1,005 列、多批、Int64 極值、空／非空 bytes、NULL、重複列、反向內容一致、錯誤確認、缺目標不自動建立、來源同列數內容改變、unsupported features、部分寫入失敗、內容／結構驗證失敗、途中取消，以及真正 commit 後注入 ack lost（保留資料且禁止重試）。不是單靠手動設 mock Blockers。

本輪新增正式 SQLite：32 MiB BLOB 在 materialize 前拒絕且 managed allocation 小於 8 MiB、1 MiB 邊界搬移、有效 Unicode／空字串／NULL／NUL／無效 UTF-8／文字超限、單欄與複合 PK 真的拒絕重複及缺 ID、索引缺失／順序變動與索引建立後取消整批回滾。PostgreSQL 16 CI fixture 用同一業務表雙向搬移與 C 定序／複合 PK 實際檢查，另對 expression/unique/null ordering/collation 真 catalog 加入與移除後預檢，以及 32 MiB bytea 有界拒絕／1 MiB 邊界搬移。

Avalonia/Xvfb：正式入口持有主視窗 gate、預覽、目標變更失效、錯誤確認不寫入、實際確認寫入且禁止重複 resume。既有跨平台 CI fixture 另測 SQLite ↔ PostgreSQL 16，包括正式 catalog 逐項加 feature／移除 feature 後恢復可預檢。

真 SQL Server 跨版本、SQL auth+SSH 真機、PostgreSQL 14/15/17、SQLite 歷史版本及非關聯式轉換皆沒有完整矩陣證據；不得把 mock／同版本一般 provider round-trip 當全部資料庫版本已通過。
