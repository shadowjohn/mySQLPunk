# SQL Server 降版相容搬移（有限範圍預覽版）

這是 Windows 資料庫右鍵的獨立邏輯搬移工具，不是完整資料庫降版器。一般資料傳輸仍只驗證列數，不能當成降版驗證。

## 引擎矩陣與驗證狀態

| 來源引擎 | 可選舊版目標 | 狀態 |
| --- | --- | --- |
| 2025 (17) | 2022 (16)、2019 (15)、2017 (14)、2016 (13) | 預檢與交易流程只有 mock 驗證 |
| 2022 (16) | 2019 (15)、2017 (14)、2016 (13) | 同一受限實作；沒有真引擎矩陣證據 |
| 2019 (15) | 2017 (14)、2016 (13) | 同上 |
| 2017 (14) | 2016 (13) | 同上 |
| 2016 (13) 或更早／未知 | 無 | 拒絕；不猜測最低版本 |

UI 不預選引擎，必須明確選取目標，並與 SERVERPROPERTY 實際版本一致。這張表代表程式允許預檢的範圍，不代表各版本已通過真 SQL Server 驗證，也不宣稱生產支援。Linux/macOS 目前只有共用核心 mock 測試，尚無此搬移 UI。

## 操作與邊界

1. 先由使用者另建空目標資料庫，再從目前已展開的 SQL Server 連線選取來源與目標；工具不建立資料庫、不自動清空或覆蓋。
2. 唯讀預檢顯示伺服器／資料庫識別、引擎 major、定序、表名與列數、CREATE TABLE 語句及阻擋原因，不顯示資料值。
3. 來源與目標不得為同一資料庫；定序必須完全相同。只使用連線既有的 CONTROL 與 VIEW DEFINITION 權限，缺少即停止，不變更權限。
4. 輸入完整目標名稱並按確認後才開啟交易。交易內重做預檢；識別、名稱、版本、結構、列數或空目標改變就拒絕。
5. 每批最多 500 列；INSERT 再依 2,100 參數及 1,000 VALUES 列限制切批。所有建立與寫入使用同一個目標交易。
6. 來源以 SERIALIZABLE 讀取，目標以 SERIALIZABLE 加工具專用 applock；可能阻擋其他寫入。請使用隔離副本或安排維護時段，這不是線上無停機搬移。
7. 逐列以型別、NULL、值的 SHA-256 多重集合核對內容與列數，並重新檢查來源及目標結構。全部通過才提交。
8. 每次最多 100,000 列，單一欄位編碼最多 1 MiB；不是大型資料庫 bulk migration。失敗／取消回滾，不自動重試。提交回覆遺失、回滾或清理失敗時，明確回報結果不確定，必須由 DBA 核對再決定下一步。

獨立 SQL 連線使用目前選取連線的記憶體設定，透過既有匯入連線工廠開啟，不從已開啟連線的 ConnectionString 重建。SQL authentication 與 SSH 沿用正常連線流程；provider owner 同時管理連線與 SSH tunnel，搬移結束或失敗後釋放。工具不另外讀取秘密庫、不開啟 PersistSecurityInfo，也不輸出密碼。

## 支援與 fail closed

只搬移 dbo、PRIMARY filegroup、未壓縮的一般 heap 的欄位與資料。保留名稱、型別長度／精度／scale、NULL。明確阻擋：

- identity、computed、default／check／PK／FK／unique constraints、所有索引（包含停用索引）、triggers、procedures、functions、views、sequences。
- 自訂 schema／型別、CLR、分割、非 PRIMARY 儲存、extended properties、change tracking、CDC、replication、RLS、temporal、memory optimized、FileTable、graph、ledger。
- sparse、column set、rowguid、FILESTREAM、hidden、masked、generated、encrypted、ANSI_PADDING OFF 欄位，以及不同於資料庫定序的欄位。
- max 長度、xml、float／real、datetimeoffset、rowversion、sql_variant、spatial、vector 等不在白名單的型別；不能確認的 catalog 欄位或查詢錯誤也停止。

型別白名單：bigint／int／smallint／tinyint、bit、date／datetime／smalldatetime、datetime2(0–7)、time(0–7)、money／smallmoney、uniqueidentifier、decimal／numeric（precision 1–38，scale 不大於 precision）、char／varchar／binary／varbinary（1–8000）、nchar／nvarchar（1–4000）。decimal 超出 .NET decimal 可表示範圍時讀取失敗並回滾，不截斷。

不複製資料庫／伺服器設定、登入、使用者、角色、授權、作業或應用程式相依。非白名單 feature 不會被略過後假報成功。錯誤報告包含階段、表名與例外類型，不包含資料值、連線字串或驅動程式原始錯誤內容。

## 備份與替代路徑

Microsoft 的 RESTORE Compatibility support 明定新版備份不能還原到舊版引擎；降低 compatibility level 並不重寫備份格式。原生還原現在先讀 HEADERONLY 與目標 ProductVersion，未知 metadata、複數 backup sets、非完整備份或來源較新一律在寫入前拒絕。

不在本工具範圍的資料庫，請由 DBA 在隔離環境產生針對精確目標版本的邏輯結構腳本，審核型別、定序、identity、constraints／indexes／triggers／routines 與相依順序，再搬資料並做內容、結構和應用程式測試。bacpac 有自身的模型與目標相容限制，不能視為無限制降版方案。

官方依據：
- https://learn.microsoft.com/en-us/sql/t-sql/statements/restore-statements-transact-sql?view=sql-server-ver17#compatibility-support
- https://learn.microsoft.com/en-us/sql/sql-server/maximum-capacity-specifications-for-sql-server?view=sql-server-ver17
- https://learn.microsoft.com/en-us/sql/t-sql/queries/table-value-constructor-transact-sql?view=sql-server-ver17

## 驗證

離線 SQLite 測試覆蓋 INSERT 邊界、中途失敗整批回滾與重試不重複；mock 覆蓋版本、確認、空目標、定序、feature 阻擋、內容／結構不符、取消、Begin／寫入／提交／回滾失敗。Windows UI 編譯與基本 smoke 交由 Windows CI；尚未以真 SQL Server 2016–2025 引擎或人工 Windows 點選驗證。
