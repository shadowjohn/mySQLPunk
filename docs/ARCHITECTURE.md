# mySQLPunk 專案架構與原始碼導覽

本文件介紹 mySQLPunk 專案的核心架構、目錄組織以及重要檔案職責。

---

## 專案模組架構

```
mySQLPunk/
├── mySQLPunk/                     # Windows WinForms 完整版專案 (.NET Framework 4.7.2)
│   ├── entity/                    # 資料實體與設定檔處理
│   ├── lib/                       # 各資料庫 Provider 與業務核心服務 (Service Layer)
│   ├── template/                  # 連線設定對話框等模板畫面
│   └── Form1.cs, QueryForm.cs...  # WinForms 視窗 UI 控制器
│
├── mySQLPunk.Core/                # 跨平台共用核心函式庫 (.NET 8)
│   ├── Connection/                # 連線定義與安全儲存 (Secret Service / Keychain)
│   ├── Providers/                 # 跨平台 Managed Provider (MySQL, PG, MSSQL, SQLite)
│   └── Export/                    # 格式化匯出 (CSV, TSV, JSON) 與原子寫檔
│
├── mySQLPunk.Desktop/             # 跨平台桌面 UI 專案 (Avalonia UI)
│   ├── Views/                     # XAML 視窗與使用者控制項
│   └── ViewModels/                # MVVM 視圖模型
│
├── mySQLPunk.CrossPlatform.SmokeTests/ # 跨平台單元與煙霧測試
├── tests/                         # 實機整合測試腳本 (PowerShell / Shell)
├── scripts/                       # 打包與發版自動化腳本
└── snapshot/                      # 產品截圖與看板娘 Punky 視覺素材
```

---

## 重要程式檔案導覽

### 核心介面與 Provider
- `mySQLPunk/lib/IDatabase.cs`：核心資料庫 Provider 抽象介面。
- `mySQLPunk/lib/my_mysql.cs`：MySQL / MariaDB 實作。
- `mySQLPunk/lib/my_postgresql.cs`：PostgreSQL 實作。
- `mySQLPunk/lib/my_sqlite.cs`：SQLite / SpatiaLite 實作。
- `mySQLPunk/lib/my_mssql.cs`：SQL Server 實作。
- `mySQLPunk/lib/my_oracle.cs`：Oracle 實作。
- `mySQLPunk/lib/my_mongodb.cs`：MongoDB 實作與文件分頁。
- `mySQLPunk/lib/my_redis.cs`：Redis / Microsoft Garnet 實作。
- `mySQLPunk/lib/my_snowflake.cs`：Snowflake REST API 實作。

### 服務層 (Service Layer)
- `mySQLPunk/lib/ConnectionSecurityService.cs`：TLS 驗證、SSH Tunnel 金鑰確認與通道生命週期管理。
- `mySQLPunk/lib/ScheduledJobService.cs`：唯讀查詢、定期匯出與備份工作定義與執行紀錄。
- `mySQLPunk/lib/WindowsScheduledTaskService.cs`：Windows Task Scheduler 排程註冊管理。
- `mySQLPunk/lib/DatabaseCopyService.cs`：跨引擎資料表與檢視表結構遷移複製。
- `mySQLPunk/lib/ObjectUriService.cs`：`mysqlpunk://object` URI 格式解析與定位。
- `mySQLPunk/lib/ConnectionUriImportService.cs`：標準資料庫連線字串 (URI) 匯入解析。

### UI 與互動視窗
- `mySQLPunk/Form1.cs`：主視窗，左側連線樹狀清單、右鍵功能選單、物件導航。
- `mySQLPunk/QueryForm.cs`：SQL 編輯器、語法高亮、結果網格、資料表資料編輯器。
- `mySQLPunk/TableDesignerForm.cs`：資料表設計器（視覺化編輯欄位、索引與約束）。
- `mySQLPunk/AboutDialog.cs`：關於視窗，看板娘 Punky 崩琦動態展示。
- `mySQLPunk/ScheduledJobsForm.cs`：自動化排程作業管理視窗。
- `mySQLPunk/OptionsForm.cs`：偏好設定中心。

---

## 視覺與看板娘素材
- `snapshot/mySQLPunk_avatar.png`：Punky 崩琦官方設定基準圖。
- `snapshot/mySQLPunk_avatar_wink.mp4` / `mySQLPunk/image/mySQLPunk_avatar_wink.gif`：Punky 眨眼動畫。
- `snapshot/punky_report_loading.png`：Punky 整理資料庫報表工作姿態插圖。
- `avatar_prompt.md`：看板娘 Punky 角色設計基準與設定文件。
