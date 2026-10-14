# 可驗證邏輯搬移：分階段矩陣

邏輯搬移讀取 schema／資料、轉換到目標 DDL、參數化寫入並驗證內容，不是原生 restore。既有一般 DataTransferService 的型別對映及列數 checkpoint、SchemaSyncScriptService 的手動修正 SQL，不具備這個工具的完整預檢與內容保證，沒有直接拿來當跨引擎還原器。

## 已落地方向

| 來源 → 目標 | 程式允許的版本 | 可執行範圍 | 驗證區分 |
| --- | --- | --- | --- |
| SQL Server → SQL Server | 2016/2017/2019/2022/2025 任意雙向及同版本，共 25 組 | Windows 既有專用工具；dbo heap、既有型別白名單、同定序、無 advanced features | 25 組預檢及交易 mock；另有正式來源 2019 ↔ 2022 隔離回歸，1007 列／7 欄＋空表，不是 25 組真引擎驗證 |
| SQLite → SQLite | 3.37–3.53 | STRICT、INTEGER/BLOB/TEXT、NULL/NOT NULL、非自增 PK、普通索引；詳細子集見下文 | 本機實際 SQLite 3.53.3；歷史 minor 版本未逐一跑 |
| SQLite → PostgreSQL | SQLite 3.37–3.53 → PostgreSQL 14–17 | INTEGER → bigint、BLOB → bytea、TEXT BINARY → text C；非自增 PK、普通索引 | 隔離 CI PostgreSQL 16 fixture 加入文字／鍵／索引雙向 round-trip；只以這次新 head 結果為證據 |
| PostgreSQL → SQLite | PostgreSQL 14–17 → SQLite 3.37–3.53 | 上述型別反向映射；PK → WITHOUT ROWID、普通索引 | PG17 實際 roundtrip 與既有 PG16 fixture；PG15 未實機跑 |
| PostgreSQL → PostgreSQL | 14–17 雙向及同版本 | 同一 bigint/bytea/text C、PK／索引子集與全部預檢 | 真實 17.10 ↔ 14.24、17.10 ↔ 16.15；不是全部版本配對驗證 |
| MySQL／MariaDB → PostgreSQL | MySQL 8.4.x／MariaDB 11.4.x → PG14–17 | signed BIGINT／LONGTEXT NO PAD binary／LONGBLOB，整數 PK／普通索引；PG 目標單交易 | 正式路徑實際 8.4.11 → PG16.15、11.4.13 → PG16.15，各 1007 列＋空表；其他 PG 版本配對未跑 |
| PostgreSQL → MySQL／MariaDB | PG14–17 → MySQL 8.4.x／MariaDB 11.4.x | 上述反向映射，專屬 staging＋校驗＋單一 multi-table RENAME＋發布後校驗 | 正式路徑實際 PG16.15 → 8.4.11／11.4.13；不是全版本、全型別或一般 DDL atomic 保證 |

## 第三階段：MySQL／MariaDB ↔ PostgreSQL

這是正式 Core 與 Avalonia 可選取的邏輯搬移，不是只加入引擎白名單。MySQL 的 DDL 隱含提交，不沿用 SQLite／PG 的「schema＋rows 單一交易」說法：

1. 重新預檢來源 fingerprint 與空目標，確認版本、既有 catalog 可見性、InnoDB Dynamic、`lower_case_table_names=0` 與 utf8mb4 資料庫定序。只接受 MySQL `utf8mb4_0900_bin`／MariaDB `utf8mb4_nopad_bin`，拒絕 PAD SPACE 或其他定序。不修改帳號權限、伺服器設定或既有物件。
2. MySQL 來源使用 RepeatableRead＋完整有界 `LOCK IN SHARE MODE` 讀取，鎖讀過的資料及 metadata 到搬移結束，最終重讀來源 schema／內容。PG 來源維持既有 SHARE locks＋最後重驗。確認後同列數變動也使 fingerprint 失效。最多 100 表／100,000 列／64 MiB，單欄 1 MiB；先 OCTET_LENGTH／CASE 限額，才讀取完整欄位。
3. MySQL 目標本輪生成 `_mp_<隨機 GUID>_<序號>` 專屬 staging 表，CREATE DDL 確實已隱含提交。DML 在另一交易中分批寫入，最多 100 列，並依目標 `max_allowed_packet` 與 UTF8／binary escaping 保守 byte 預算縮批；單列不合預算先拒絕。每次 CREATE／INSERT 檢查 warnings，不能把截斷／轉型視為成功。
4. staging catalog／結構／逐欄內容 SHA256 全部吻合、DML 提交後再檢查 staging 與來源，才送出**一個** multi-table RENAME。只限上述版本的 InnoDB 普通表、無 FK／trigger 等相依物件，且不覆蓋名稱。碰撞會失敗，不先刪除目標；發布後再次校驗才 `Verified`。
5. staging 之前拒絕為 `Refused`。CREATE 曾送出後失敗／取消為 `StagingRemains`，列出本輪 staging→原名與階段；DML 可回滾，但表不會消失，列數只是處理進度。RENAME 曾送出後任何失敗／ack lost 為 `Uncertain`，不推測已發布或已撤銷。兩者禁止 UI 續寫／盲重試，不自動 DROP；Task 與 parent gate 保持到實際操作停止。RENAME 送出後取消不撤銷已提交操作，需等發布／驗證結果。

沒有逐表 checkpoint、自動 cleanup 或 durable 客戶端 resume journal。程序／引擎 crash 不在本輪實際驗證範圍；重啟後任何 staging 或發布物件都讓「必須空目標」預檢拒絕，需 DBA 核對實際 catalog 與本輪報告，不把名稱前綴當成自動刪除授權。不得把正常 RENAME 碰撞探測／注入 ack lost 擴大為實測斷電 crash recovery。

精確子集：有／無整數單欄或複合 PK（手動給值、最多 16 欄）、具名普通升冪 bigint 索引；BIGINT signed ↔ bigint、LONGTEXT ↔ text C、LONGBLOB ↔ bytea、NULL／NOT NULL。表／欄／索引名只接受小寫 ASCII 63 字元，索引在來源與目標名稱空間不得碰撞。MySQL `PRIMARY` 映射 PG 預設 `表名前58字元_pkey`；PG 自訂 PK 名稱無法保留為 MySQL PRIMARY，預檢拒絕。恰巧符合預設慣例的手動 PG 名稱無法由 catalog 區分；如依賴該 constraint 名稱，應用 PG 目標，不承諾跨引擎 ON CONFLICT 或字串函式行為相同。

文字／二進位**可以作一般欄位，但不能作此 MySQL 配對的索引鍵**：MySQL LONGTEXT／LONGBLOB 鍵需要 prefix，縮成 VARCHAR／VARBINARY 會新增長度約束；本輪不偷偷截鍵或改型別。unsigned／其他整數寬度、DECIMAL／時間／浮點、CHAR／VARCHAR／TEXT／BLOB 的其他長度、ENUM／SET、zero date、AUTO_INCREMENT、default、generated、FK／CHECK／UNIQUE、前綴／expression／descending／invisible／特殊索引、partition、view、sequence、trigger／routine／event、自訂表屬性或 comments 均以 catalog＋SHOW CREATE TABLE 完整 grammar 預檢拒絕，沒有靜默跳過。需要這些語意仍須後續專用轉換契約。

目前僅能證明直接 global／schema-level 權限的完整 catalog 可見性：SELECT、SHOW VIEW、TRIGGER、EVENT、CREATE ROUTINE、ALTER ROUTINE；角色／表級授權或缺權限會阻擋。不執行 SHOW GRANTS（MariaDB 可能含認證資訊）、不讀秘密或要求新增權限。

可重跑：`python3 tests/Run-MySqlLogicalMigration.py [新證據目錄]`，先快取官方 mysql:8.4、mariadb:11.4、postgres:16-alpine。直接引用正式 Core；唯一 ownership label／loopback 隨機 ports／tmpfs，finally 僅清自有容器。`migration.log`／`evidence.json` 保存實際版本、image digest、來源 hash／清理狀態，不保存資料值／密碼。CI Linux 另跑此 fixture，四平台編譯；這與既有 provider 56/56 分開。

官方語意依據：MySQL 8.4 [implicit commit](https://dev.mysql.com/doc/refman/8.4/en/implicit-commit.html)／[RENAME TABLE](https://dev.mysql.com/doc/refman/8.4/en/rename-table.html)，MariaDB [implicit commit](https://mariadb.com/docs/server/reference/sql-statements/transactions/sql-statements-that-cause-an-implicit-commit)／[RENAME TABLE](https://mariadb.com/docs/server/reference/sql-statements/data-definition/rename-table)。限於受控版本與物件子集，不宣稱所有 DDL 可回滾。

## 可重跑的 PostgreSQL 跨版本證據

`python3 tests/Run-PostgreSqlVersionMigration.py 14 [新證據目錄]` 與 `python3 tests/Run-PostgreSqlVersionMigration.py 16 [新證據目錄]` 直接 ProjectReference 正式 Core，不使用凍結副本。需先快取官方 `postgres:14-alpine`／`postgres:16-alpine` 與 `postgres:17-alpine`；腳本只 image inspect，不拉取映像或調整網路／權限。每次建立唯一 ownership label、loopback 隨機 port 與 tmpfs 容器，最後核對 label 才清理自己的 fixture；密碼隨機產生且不輸出。

每輪為 SQLite seed → PG17 → 舊版 PG → 空 PG17 → SQLite，records 1007 列＋empty_table 空表，複合 PK、具名普通索引、UTF8／emoji、NULL／空 binary／空 text、Int64 極值。每段使用 production 預檢、交易、schema 與 SHA256 多重集合驗證，最後另測重複 PK 被拒絕及索引存在。這不是 procedures、FK、identity、任意型別、SSH 或桌面 UI 的跨版本證明；1005 列 transaction fault 仍只屬既有 SQLite 回歸，provider 全套 56/56 仍是另一組測試。

2026-10-14 獨立證據先以 fa734e7 跑 17 ↔ 14／16，再以 06 工作樹跑 17 ↔ 14。06 最終 95c0be2（合入 4368883）三個 production 檔案 SHA256 均與獨立工作樹一致：Models/LogicalMigration.cs `a912ddcd778bac06c4980f3f3403d9b0d4563b81414c170894b79619bf79912e`、Services/LogicalMigrationService.cs `721ec60b1be6c2ada40114e59c323513c44aa1124e52ffa9411cb6cd23095fd4`、Services/LogicalMigrationKeys.cs `69fc1e7ea82ab7f27c2ce22d4d14613294f981f73bdb0b4f99e04893ef793cef`。新 harness 再以相同正式來源跑兩組成功；並非重跑舊 SHA CI。

當次實際官方映像 RepoDigest（後續 tag 可能改變，重跑以 evidence.json 的實際版本及 digest 為準）：

| 實際版本 | 官方 RepoDigest |
| --- | --- |
| 14.24 | postgres@sha256:4ea9e5ed06591da7ea23eb65465e8d3187fe79f4d5ec3ae976d29a33b013e77a |
| 16.15 | postgres@sha256:721873c34ceb9f8d8fc265984940dc982404c105f19ad51be9fdc5970a6080ea |
| 17.10 | postgres@sha256:742f40ea20b9ff2ff31db5458d127452988a2164df9e17441e191f3b72252193 |

`migration.log`、`evidence.json` 保留實際版本、映像 ID／digest、正式來源 hash 與清理结果；不存密碼或資料值。原獨立日誌仍在 audit-evidence/postgresql-14-17-real-versions.log、postgresql-16-17-real-versions.log、postgresql-14-17-primary-name-fix.log，沒有覆寫。

## 第二階段：實用文字與鍵索引子集

實作適用明確手動給 ID 的業務表：例如 `customers(id INTEGER NOT NULL, label TEXT COLLATE BINARY NOT NULL, note TEXT, payload BLOB, PRIMARY KEY(id)) STRICT, WITHOUT ROWID`，可帶 `customer_label(label)`、`customer_note(note,id)` 普通索引；複合主鍵如 `lines(customer,code)` 同樣保留順序、唯一性及 NOT NULL。所有索引 DDL 都列在確認預覽，建立後正式 catalog 再讀回驗證，缺索引／改欄位順序也會整批回滾。

- SQLite 文字只接受 BINARY（隱含或明確），PostgreSQL 必須是內建 `pg_catalog."C"`；兩端 UTF-8，逐欄嚴格解碼，拒絕 NUL、無效 UTF-8，不偷偷替換字元。保留空字串、NULL、Unicode、大小寫與 byte-order 比較語意；不承諾兩引擎字串函式或 LIKE 行為相同。
- SQLite PK 表要求完整可辨識 grammar、明確 NOT NULL 與 WITHOUT ROWID，避免 INTEGER PRIMARY KEY 的 rowid alias 自動給 ID 行為被偷偷改成 PostgreSQL 無 sequence 的 bigint。單欄整數／文字／二進位與複合 PK 均可。PostgreSQL → PostgreSQL 保留 catalog 的主鍵 constraint／背後索引名稱，名稱納入確認 fingerprint 與目標結構驗證。SQLite → PostgreSQL 明確產生 `表名前 58 字元_pkey`，並預檢它與所有表／普通索引／其他主鍵索引的 public 名稱空間碰撞，不讓引擎自動另取名稱。PostgreSQL → SQLite 僅允許上述預設命名慣例；其他名稱無法保留 `ON CONFLICT ON CONSTRAINT` 依賴，預檢拒絕。catalog 無法區分手動指定但恰好符合預設慣例的名稱，跨 SQLite 路徑明確不保證此預設名稱／內部索引名稱與實體配置；需要名稱依賴的資料庫應使用 PostgreSQL 目標。
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
- PostgreSQL 目標先讀 `SHOW block_size` 並納入確認 fingerprint；只接受標準 8192-byte page。PK 與每個普通索引各自逐列限制保守 tuple 預算 2048 bytes，32 欄以內：保留 40 bytes 給 tuple header／TID／NULL bitmap／對齊，每欄 bigint 8 bytes，text／bytea 加 4-byte varlena header 後向上對齊到 8 bytes，文字使用 UTF-8 byte 數，複合鍵加總。這是刻意低於標準 btree 頁面上限的應用白名單，不是宣稱精確最大可用大小；不依賴壓縮。超限在預覽阻擋且列出表／鍵名稱；未索引文字仍可達原本 1 MiB 欄位限額。非標準 page／未知能力 fail closed。
- 取消等實際工作停止，SQLite 用原生 progress handler，PostgreSQL 使用 provider cancellation；工作台 gate 持續到對話框關閉，不能同時 SQL／edit/delete/connect。變更目標／版本／認證即失效原預覽。

## 重試與恢復

沒有逐表 checkpoint：全部驗證後才提交。失敗且確認回滾的結果是 `RolledBack`，須重新預檢；已驗證提交是 `Verified`，不再寫入。commit 開始後失敗、回覆遺失或回滾失敗是 `Uncertain`，UI 停止再次預檢／寫入，禁止盲目重試，必須由 DBA 核對；不宣稱放棄 Task 就已停止或回滾。已有內容的目標即使是前次結果也不能自動 resume。

## 其他方向：目前阻擋與必要技術工作

| 方向 | 現況／執行前拒絕的技術理由 | 下一階段必要能力（不是本輪已通過） |
| --- | --- | --- |
| MySQL/MariaDB → SQLite/SQLServer | unsigned、zero date、ENUM/SET、charset、PAD SPACE、AUTO_INCREMENT 等仍無等價映射；與 PG 的受控子集已落地 | 另訂目的端 schema／資料 codec 與語意驗證，不把 count-only transfer 當忠實轉換 |
| SQLite/SQLServer → MySQL/MariaDB，以及 MySQL↔MariaDB／跨版本 | 不開放這些配對；與 PG 的 staging 路徑不等於其他來源語意已可保留 | 依目的端型別／定序契約與引擎版本驗證擴充；不直接沿用既有對映或自動刪 staging |
| SQLServer → SQLite/PG/MySQL，以及反向 | UTF-16/UTF-8、linguistic/BIN2 定序的比較／尾空白規則、rowversion、decimal/time scale、identity/default/sequence 不能把現有型別名稱替換當成保留語意 | 可重用 SQLServer catalog 的阻擋規則，但需跨 provider canonical schema／資料 codec、來源一致快照與逐欄語意／版本證據；目前只 Windows SQLServer→SQLServer 專用子集可執行 |
| Oracle/Snowflake 與其他引擎 | NUMBER／時間、空字串 NULL、identifier/collation 與交易 DDL 不等價 | 專用 catalog／codec／交易恢復設計；本輪不開連線 |
| MongoDB、Redis/Garnet 與表格引擎 | document/key-value model、TTL／原子操作／巢狀內容沒有通用無損表格映射 | 明確專用模型轉換契約，不當成同義 restore |

上述其他方向服務在開連線前拒絕。雙向跨版本／換引擎的整體需求仍未全部完成；目前已擴充 SQLite/PG 與 MySQL/MariaDB↔PG 的可驗證子集，不是全引擎完成或發版宣告。release 仍 hold。

有 FK、其他字串定序、時間、decimal、identity、procedure/trigger 的一般資料庫仍須使用引擎官方 schema/data migration 工具與人工轉換／驗證，不是宣稱 bacpac 可無限制降版。新版 SQL Server .bak 仍不能直接還原到舊引擎，compatibility level 不改備份格式；既有原生 restore 預檢繼續保留。

## 回歸證據

實際隔離 SQLite：1,005 列、多批、Int64 極值、空／非空 bytes、NULL、重複列、反向內容一致、錯誤確認、缺目標不自動建立、來源同列數內容改變、unsupported features、部分寫入失敗、內容／結構驗證失敗、途中取消，以及真正 commit 後注入 ack lost（保留資料且禁止重試）。不是單靠手動設 mock Blockers。

本輪新增正式 SQLite：32 MiB BLOB 在 materialize 前拒絕且 managed allocation 小於 8 MiB、1 MiB 邊界搬移、有效 Unicode／空字串／NULL／NUL／無效 UTF-8／文字超限、單欄與複合 PK 真的拒絕重複及缺 ID、索引缺失／順序變動與索引建立後取消整批回滾。PostgreSQL 16 CI fixture 用同一業務表雙向搬移與 C 定序／複合 PK 實際檢查，另對 expression/unique/null ordering/collation 真 catalog 加入與移除後預檢，以及 32 MiB bytea 有界拒絕／1 MiB 邊界搬移。

Avalonia/Xvfb：正式入口持有主視窗 gate、預覽、目標變更失效、錯誤確認不寫入、實際確認寫入且禁止重複 resume。既有跨平台 CI fixture 另測 SQLite ↔ PostgreSQL 16，包括正式 catalog 逐項加 feature／移除 feature 後恢復可預檢。

真 SQL Server 2019↔2022 與 PG14↔17／16↔17 的限定 fixture 已有證據；SQL auth+SSH 真機、PG15、MySQL/MariaDB 的其他版本配對、SQLite 歷史版本及非關聯式轉換皆沒有完整矩陣證據。不得把 mock／同版本一般 provider round-trip 當全部資料庫版本已通過。
