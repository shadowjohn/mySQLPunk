# SQL Server 正式來源雙向回歸

在已快取 `mcr.microsoft.com/mssql/server:2019-latest` 與 `2022-latest` 的 Docker x64 主機執行：

```sh
python3 tests/Run-SqlServerVersionMigration.py /tmp/mysqlpunk-sql-evidence-unique
```

輸出目錄必須尚不存在。腳本先編譯直接連結的正式 service/catalog/session/batch/owner，再以隨機名稱、localhost 動態 port、ownership label、tmpfs 建立自己的兩個容器；不使用既有 profile。密碼只存在子程序環境且輸出遮蔽；finally 核對 label 才清除自己的容器。失敗仍保留日誌、來源 SHA256、image digest、實際版本與清除紀錄。不下載 image、不讀使用者資料或憑證。

每方向 1007 列、7 欄及空表，包含 bigint 極值、繁中 emoji、decimal(20,6)、datetime2(7)、time(7)、varbinary bytes/empty/NULL、bit。NULL time 使用 SqlDbType.Time，非 NULL TimeSpan 交由 SqlClient 推導，不能設會轉成 DateTime 的 DbType.Time；泛用 provider 仍使用 DbType.Time。二進位 NULL 依 DataColumn 型別設定 Binary。每批最多 2098 個資料參數，保留兩個 RPC 額度，7 欄最多 299 列。

兩個引擎另各測七欄 1000 列：末批 PK 重複時全部回滾、修正後重試不重複，NULL time/binary 同時涵蓋。這裡的 PK 只用來注入批次失敗，不代表已支援 PK 的跨版本搬移。

這不是所有 SQL Server 版本矩陣，也不涵蓋 PK/FK/index/identity 搬移、Windows UI 或 SSH 真機。跨平台 smoke 另測 Microsoft.Data.SqlClient 與泛用 SQLite parameter；真引擎 harness 使用 Windows 程式同族的 System.Data.SqlClient 4.9.1。
