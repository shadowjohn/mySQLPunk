# MSSQL 匯出：可選 SNAPSHOT 與自動還原資料庫設定

日期：2026-10-13
狀態：設計草案，待使用者審核；產品程式尚未修改。
基底：codex/mssql-export-preflight-compat，34024b7。

## 使用者需求與取代範圍

使用者要求匯出前可決定是否使用 SNAPSHOT，預設「否」；程式先檢查 ON/OFF 能力與權限，權限不足時停用選項，走一般匯出；選「是」時由程式在作業中自動處理開關。

此決定取代 design.md、production-preflight.md 中「所有含資料匯出必須預先 ON、不自動變更設定」的規則。原本 SNAPSHOT 模式的同一時間點保證保留；一般模式明確不具有該保證。既有串流、Geometry/LOB 分段、原檔保護、取消、DDL fingerprint、Punky、進度與鏡像架構繼續沿用。這份設計只授權未來產品行為；本次設計工作沒有連線或更改正式庫。

## 方案比較

| 方案 | 效果 | 取捨 |
| --- | --- | --- |
| 僅增加一般讀取，SNAPSHOT 仍由 DBA 事先開啟 | 一般帳號可匯出 | 不滿足自動開關需求 |
| 可選一般／SNAPSHOT，按原狀管理資料庫設定（採用） | 符合預設否、能力檢查及自動開關 | 需要有限等待、併行協調與復原紀錄 |
| 每次按選項強制全庫 ON／OFF | 行為表面簡單 | 會關掉原本由 DBA 或其他程式使用的 ON，不採用 |

## 1. 精靈與權限規則

輸出設定頁加入「使用 SNAPSHOT 一致性讀取」是／否，預設否。顯示目前資料庫設定與可用性原因；每次新開精靈預設仍為否，不延用上次的肯定選項。

能力檢查只有讀取：目前資料庫的 snapshot_isolation_state、可更新狀態，以及 HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'ALTER')。不能為了試探能力先執行 ON/OFF。能力資料獨立於物件結構 fingerprint。

依使用者的權限政策，選項代表包含自動開關管理的完整功能；沒有有效 ALTER 權限時固定為否並停用，即使資料庫目前已 ON 也是如此。這是產品政策，不代表 SQL Server 在已 ON 時使用 SNAPSHOT 本身需要 ALTER。

| 情境 | UI 與執行 |
| --- | --- |
| 僅結構 | 固定否、停用；不開資料交易、不改設定 |
| ON 或 OFF，已確認有效 ALTER 權限且可變更設定 | 選項可用，預設否 |
| 權限為 0／NULL，能力資訊不足，或資料庫不可變更設定 | 固定否、停用，說明原因；仍可一般匯出 |
| ON/OFF 轉換中或狀態未知 | 固定否、停用，可重新檢查；一般讀取不因這個設定單獨被擋 |
| 權限檢查發生取消、斷線或真正連線失敗 | 保留原有錯誤／取消處理；不得偽裝成可正常匯出 |

固定否只解除 SNAPSHOT／ALTER 的額外要求；資料表 SELECT、VIEW DEFINITION、可讀相依 metadata 等必要條件仍保留。

開始前重新檢查。若選是後權限或狀態變動，回確認頁要求重新檢查／選擇；執行中不可偷偷降級。能力檢查停用選項時，畫面清楚顯示目前改為一般讀取，再由使用者按開始。

## 2. 選否：一般串流讀取

沿用同一個 owned session，以明確的 IsolationLevel.ReadCommitted 資料交易逐表讀取；SchemaOnly 不開資料交易。保留 SequentialAccess、bounded buffer、取消、開始／結束結構 fingerprint 與來源資源釋放順序。

不執行 ALLOW_SNAPSHOT_ISOLATION ON/OFF，不改 READ_COMMITTED_SNAPSHOT，不使用 NOLOCK。既有 RCSI 設定照常影響 READ COMMITTED；RCSI OFF 時一般讀取可能與寫入互相等待。單一 READ COMMITTED 交易不提供跨表同一時間點保證。

確認頁、輸出 SQL header（包含 DataOnly）及結果均標示：

> 一般讀取（READ COMMITTED）：匯出期間可能讀到不同時間的資料；來源持續寫入時，關聯資料可能不一致，還原可能因外鍵限制失敗。檔案驗證不等同還原成功。

維持可信任外鍵的 WITH CHECK，不為了讓混合時間資料還原成功而自動改 NOCHECK／刪除約束／修改資料。

## 3. 選是：啟用、讀取、還原原狀

1. 檢查必要匯出條件與本次要求後，先在來源資料庫取得固定名稱的 Session-owned application lock。只有 SNAPSHOT 作業取得此鎖；同庫另一個合作中的 mySQLPunk 快照匯出執行時，立即回報忙碌，讓使用者稍後重試。不同庫互不阻擋。
2. 取得鎖後重新讀取真實狀態、資料庫識別與權限；UI 查到的狀態不能當作本次原狀。
3. 原本 ON：建立本次 SNAPSHOT 交易；結束時保留 ON，不執行 OFF。
4. 原本 OFF：先落地本機復原紀錄，再執行 ON。啟用等待最多 60 秒並可取消；只有回讀確認 state=1 才開始 SNAPSHOT 交易。轉換中／未知狀態不當作成功。
5. 所有表仍由同一筆 SNAPSHOT 交易讀取；完成來源資料與結構複核後，關閉 reader、結束交易。
6. 本次從 OFF 啟用的工作才嘗試恢復 OFF。成功、取消、匯出失敗共用此清理邊界；清理使用獨立 30 秒期限，不能沿用已被取消的作業 token。
7. 回讀確認 OFF 才記為還原成功並移除該工作復原紀錄。已經是 OFF 時可確認完成；轉換中／未知／讀不到狀態均不能宣稱恢復。
8. 釋放 application lock 及 DB／SSH 資源後，再執行本機檔案驗證、發布與鏡像。設定管理只包住來源階段。

設定管理是明確的工作階段，顯示「啟用 SNAPSHOT」「恢復資料庫設定」與等待時間；不能把長 ALTER 隱藏在無法回報的 Dispose 裡。不得使用 KILL、ROLLBACK IMMEDIATE、SINGLE_USER 或強制終止其他連線。

## 4. 失敗、取消、程序中斷

設定切換不是匯出交易的一部分，程式當掉或網路中斷時不能承諾一定關回。啟用前的本機紀錄放在 `%LOCALAPPDATA%\mySQLPunk\ExportRecovery\snapshot\<job-id>.json`，只含 job ID、來源伺服器／資料庫識別、原狀、操作階段及時間，不含帳密、連線字串或資料列。紀錄寫入失敗時不執行 ON。

若啟用指令結果不明，必須嘗試回讀與有限清理；不能因 bool 尚未被設成成功便漏掉可能已生效的變更。斷線後不自動換身分／重連去執行 OFF；保留待人工確認的紀錄。

匯出與設定復原結果分開顯示：

- 資料完整且檔案已驗證，但無法確認恢復 OFF：可完成發布，結果須醒目顯示「檔案匯出完成；資料庫設定尚未確認恢復」，附目前已知狀態與復原紀錄位置。
- 匯出失敗／取消：原目標檔仍受保護；設定清理失敗另外列出，不能覆蓋原始失敗或取消原因。
- 程序中斷留下紀錄：下次開啟相關精靈可看到未處理紀錄及人工檢查方式；不得只靠同名資料庫自動執行 OFF。來源識別無法確認時也不得代修。

## 5. 併行與共用設定的界線

application lock 只協調遵守相同協定的 mySQLPunk 版本。其他工具、舊版程式及 DBA 不受此鎖限制。即使本次原始狀態是 OFF，開啟期間其他程式仍可能開始使用 SNAPSHOT；關回 OFF 可能等待它們，並影響後續新 SNAPSHOT 交易。

因此選是旁顯示：「需要 ALTER 權限；必要時會暫時變更整個資料庫的 SNAPSHOT 設定，結束後嘗試恢復原狀。其他程式同時使用時可能互相影響。」恢復有期限、留下確切狀態，不保證任意外部併行下零影響。原本 ON 一律保持 ON。

## 6. 程式範圍與驗收

局部擴充 MssqlExportModels、MetadataReader、Source、SqlExportService、SchemaScriptWriter 與原生 Wizard，新增小型設定生命週期／復原紀錄元件。公開 provider contract、通用 dump、其他資料庫、原生 .bak、排程不變。不新增套件。

必要驗證：

1. 預設否／無 ALTER／NULL 能力／SchemaOnly 的 UI 與 request；能力 retry、取消與過期結果保護。
2. 一般模式在 ON、OFF、轉換狀態皆不執行 ALTER；資料／定義權限不足仍被擋。靜態資料實際匯出還原通過。
3. 持續寫入的受控 Parent／Child fixture：一般模式觀察跨時點資料；合法來源更新造成匯出缺父列時，實際還原應因 FK 失敗並保留正確警語。SNAPSHOT 模式仍維持同時點資料。
4. OFF→ON→OFF 的成功、取消、讀取例外；初始 ON 保持 ON。回讀實際狀態，不以 command 返回或 finally 執行當作證據。
5. 啟用等待逾時、設定已生效但回應不明、權限撤銷、斷線、復原紀錄寫入失敗與程序中斷殘留。
6. 同庫快照作業互斥、不同庫獨立；外部 SNAPSHOT 交易令 OFF 等待時，有限清理與警語正確，不強制中斷他人。
7. 一般模式的取消／DDL drift／原檔保護；設定清理問題不掩蓋檔案與鏡像的真實結果。
8. 舊的一致性／Core／Geometry 等測試明確指定 SNAPSHOT，避免預設更改後原來的驗收含義悄悄改變。
9. 全部 DB 測試僅使用既有授權的 (localdb)\mySQLPunkExportTest_20261012 及 token-owned fixture；原生 UI、Debug／Release、適當 smoke／獨立 review 與正式機驗收分別記錄。

## 官方依據

- [ALTER DATABASE 權限](https://learn.microsoft.com/en-us/sql/t-sql/statements/alter-database-transact-sql?view=sql-server-ver15#permissions)：需要資料庫 ALTER。
- [ALLOW_SNAPSHOT_ISOLATION 開關](https://learn.microsoft.com/en-us/sql/t-sql/statements/alter-database-transact-sql-set-options?view=sql-server-ver15)：資料庫層級設定、過渡狀態、等待及取消行為。
- [隔離等級](https://learn.microsoft.com/en-us/sql/t-sql/statements/set-transaction-isolation-level-transact-sql?view=sql-server-ver15)：READ COMMITTED 與 SNAPSHOT 的一致性範圍。
- [Application lock](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql?view=sql-server-ver15)：Session ownership、資料庫範圍與有限等待。
