# 功能路線圖（對照 Navicat Premium / Navicat 17）

本表以 2026-08-25 讀取的 [Navicat Premium 功能頁](https://www.navicat.com/cht/products/navicat-premium)與 [Navicat 17 Highlights](https://www.navicat.com/en/navicat-17-highlights)為範圍。使用者已指定「網址裡面提到的功能都要有」，因此舊版標成「不做」的 BI、協作、MongoDB、Redis、Snowflake 與 Linux ARM 全部改回長期排程，不再從範圍刪除。

狀態標記：✅ 核心功能已具備｜🆕 本輪完成｜🟡 已有部分能力｜📋 尚未實作

## Navicat 17 亮點對照

| Navicat 功能 | 狀態 | mySQLPunk 現況與完成條件 |
| --- | --- | --- |
| AI 助理、多聊天室、附加 schema、跨模型答案比較 | 🟡 | 已有 Punky 停靠聊天面板、schema 上下文、多家 API／本機 CLI 與模型切換；待補多聊天室與並排比較。 |
| 詢問 AI：可自訂／釘選動作 | 🟡 | 已能提問、插入回覆 SQL；待補動作管理、釘選與查詢編輯器快捷入口。 |
| 解釋／最佳化／格式化／跨資料庫轉換 SQL，差異並排確認 | 🟡 | 已有格式化、AI 對話及 View SQL 跨 provider 轉換；待補統一動作、差異檢視與確認後套用。 |
| AI 修正 SQL 錯誤 | 🟡 | AI 面板可處理錯誤文字；待補執行失敗後一鍵附上 SQL／provider 錯誤並回寫差異。 |
| 同一工作區多模型、Function／Procedure 物件 | 📋 | 先完成唯讀 ER 圖，再擴充多模型工作區與 routine 物件。 |
| 圖表樣式、圖層、鎖定、群組、自動排列、連接線重導 | 📋 | 納入模型工作區第二階段。 |
| 模型與資料庫雙向比較／同步 | 📋 | 先共用「結構差異引擎」，再支援 DB→模型與模型→DB。 |
| 關聯式／維度／Data Vault 2.0 模型 | 📋 | 納入模型工作區第三階段。 |
| 資料字典範本、個人化、PDF、自動化、郵件、模型字典 | 🟡 | 已能輸出五種 provider 的整庫 HTML 並由瀏覽器另存 PDF；待補範本、直接 PDF、排程、郵件與模型來源。 |
| 資料分析：型別、格式、分佈、統計與互動探索 | 🆕 | 資料表右鍵「資料分析」；五種既有 provider 共用，含抽樣／全表、NULL、相異值、極值、平均、Top 10 比例與值鑽取查詢。待補格式異常偵測與更多圖表。 |
| Query Explain：視覺／JSON／文字／統計計畫與高成本標示 | 🟡 | MySQL／MariaDB、PostgreSQL 的唯讀 JSON 計畫、樹狀節點、屬性、文字計畫與高成本標示已完成；待補 SQL Server／Oracle／SQLite 原生計畫。 |
| 釘選查詢結果（SQL、耗時、不可變快照） | ✅ | 結果快照分頁可比較並可中鍵／右鍵關閉。 |
| Table Profile：多組篩選／排序／欄顯示設定 | 🟡 | 已能記住單一資料表設定；待補具名多組設定與快速切換。 |
| 物件 URI 分享與直接定位 | 🟡 | 已註冊 `mysqlpunk://`；待補「複製物件 URI」、參數驗證與啟動後定位物件。 |
| 連線精靈、進階篩選／搜尋、URI 連線 | 🟡 | 已有連線精靈、引擎搜尋、名稱／群組即時搜尋；待補連線 URI 匯入。 |
| 集中管理多連線、批次操作、星號、顏色、群組 | 🟡 | 已有多設定檔、多層群組、拖曳與連線顏色；待補星號與批次屬性操作。 |
| BI 圖表互連 | 📋 | 現有 BI 只有物件分佈／列數排名資料表；需新增儀表板與同來源聯動篩選。 |
| BI 自訂運算式 | 📋 | 納入 BI 運算式引擎。 |
| BI 連接 MongoDB／Snowflake | 📋 | 等對應 provider 與 BI 基礎儀表板完成。 |
| MongoDB Aggregation Pipeline 視覺設計 | 📋 | MongoDB provider 後續階段：拖放 stage、逐步預覽與結果驗證。 |
| 專注模式 | ✅ | F11／檢視選單可隱藏工具列、導覽與資訊窗格。 |
| Snowflake | 📋 | 新 provider，需連線、metadata、查詢、資料檢視、模型與 BI 能力。 |
| Redis standalone／Cluster／Sentinel、Microsoft Garnet | 📋 | 新 provider，需連線、命令、資料編輯、監控、Pub/Sub。 |
| Linux ARM | 📋 | 現有 WinForms 僅 Windows；需先抽離 UI／provider core，再評估跨平台桌面 UI。 |

## Navicat Premium 功能頁對照

| Navicat 功能 | 狀態 | mySQLPunk 現況與完成條件 |
| --- | --- | --- |
| 主要視窗、樹狀導覽、物件清單、分頁 | ✅ | 已具備可停靠／浮動分頁、多連線樹與物件清單。 |
| 物件設計器 | 🟡 | 五種 provider 已能建表與主要 ALTER；進階 constraint／索引仍需更多實機矩陣。 |
| RDBMS 資料編輯器（網格） | ✅ | 分頁瀏覽、篩選、排序、欄顯示、寫回、無主鍵安全模式、多格式匯出。 |
| MongoDB 資料編輯器（網格／樹／JSON） | 📋 | 隨 MongoDB provider 實作。 |
| Redis 資料編輯器 | 📋 | 隨 Redis／Garnet provider 實作。 |
| 資料分析與互動圖表 | 🆕 | 已完成欄位摘要、Top 值比例與值鑽取的第一版。 |
| 自動完成程式碼 | ✅ | 已能解析目前 statement 的 FROM／JOIN／UPDATE／INTO 來源與 alias；支援欄位、`alias.column`、資料表、關鍵字與片段捷徑，並依 provider/database 快取資料表、View 與欄位 metadata。 |
| 程式碼片段 | ✅ | `Ctrl+Shift+P` 開啟片段管理器；支援 8 組內建片段、自訂片段 CRUD、全文搜尋、`$CURSOR$` 定位、保留縮排插入，以及 JSON 匯入／匯出工作區格式。 |
| 視覺化解釋 | 🟡 | MySQL／MariaDB、PostgreSQL 第一階段已完成；待補 SQL Server／Oracle／SQLite。 |
| 視覺查詢建構器 | 📋 | 待補拖拉資料表、JOIN、條件與 SQL 雙向更新。 |
| Procedure／Function 偵錯器（中斷點、逐步、變數、呼叫堆疊） | 📋 | 依 provider 能力分階段實作，優先 PostgreSQL／SQL Server。 |
| AI 助理／詢問 AI | 🟡 | 核心聊天與 schema 上下文已完成，進階動作見上表。 |
| 資料傳輸／遷移（跨 DBMS） | 🟡 | 已有 Table／View 跨 provider 複製；待補整庫精靈、mapping、續傳與驗證報告。 |
| 資料同步 | 📋 | 先做唯讀資料差異、方向選擇與 SQL 預覽，再開放同步執行。 |
| 結構同步 | 📋 | 先做兩庫結構差異報告，再產生／審核 DDL。 |
| 模型 | 📋 | 見 Navicat 17 模型路線。 |
| BI | 📋 | 見 Navicat 17 BI 路線。 |
| 匯入／匯出（Excel、Access、CSV、ODBC 等） | 🟡 | MySQL SQL 匯入／匯出完整，查詢結果有常用格式；待補五種 provider 精靈對等化、Access／ODBC。 |
| 資料字典 | 🟡 | HTML 核心已完成，範本／直接 PDF／排程／郵件待補。 |
| 資料產生器（規則、約束、參照完整性、大量資料） | 🟡 | 已能依欄位型別產生 INSERT；待補規則編輯、FK 順序、唯一性與大量批次。 |
| 備份／還原與原生工具介面 | 🟡 | 已有邏輯 SQL 備份、隔離還原、差異與完整性排程；待補 MongoDump、Oracle Data Pump、SQL Server native backup 介面。 |
| 自動執行：查詢、匯入／匯出、傳輸、通知郵件 | 📋 | Windows 工作排程器整合先行，作業定義需可攜並保存執行記錄。 |
| MongoDB 結構描述分析器 | 📋 | 隨 MongoDB provider 實作 schema 推斷、異常與極端值檢視。 |
| Redis Pub/Sub | 📋 | 隨 Redis／Garnet provider 實作。 |
| 協同合作：同步連線、查詢、pipeline、片段、模型、BI、群組 | 📋 | 先做本機可匯出／匯入的工作區格式與 Git 版控，再補可自架同步服務與權限。 |
| SSH tunnel、SSL/TLS | 📋 | 高優先安全缺口；先補五種既有連線 UI、憑證驗證與隧道生命週期。 |
| PAM／LDAP／Kerberos／MFA／SSO | 📋 | 依 provider 驗證能力分階段加入，不保存明文祕密。 |
| 深色模式／平台原生設計 | ✅ | Windows 原生 WinForms、淺／深色主題與 DPI 向量圖示已完成。 |
| 跨平台授權／Windows、macOS、Linux | 📋 | 商業授權本身不適用開源專案；功能等價目標是跨平台建置與同一工作區格式，需 UI 框架遷移。 |

## Provider 與服務覆蓋

| 範圍 | 狀態 | 說明 |
| --- | --- | --- |
| MySQL／MariaDB | ✅ | 共用 MySQL provider，已有實機版本矩陣。 |
| PostgreSQL、SQL Server、Oracle、SQLite | 🟡 | 核心 metadata／查詢／編輯／DDL／備份可用，進階功能持續對等化。 |
| MongoDB、Redis／Garnet、Snowflake | 📋 | 不再排除，分別建立 provider 與專用資料介面。 |
| AWS、Microsoft Azure、Google Cloud、Oracle Cloud、MongoDB Atlas、Redis Enterprise Cloud、Alibaba Cloud、Tencent Cloud、Huawei Cloud | 🟡 | RDBMS 可先用標準主機連線；待補各家 IAM／SSO／MFA、MongoDB／Redis provider 與雲端專用驗證。 |
| OceanBase、PingCAP／TiDB、Dameng、Fujitsu、Kingbase、HighGo | 📋 | 建立實機相容矩陣；能沿用 MySQL／PostgreSQL 協定者先驗證差異，其餘再建立專用 provider。 |

## 接續順序

1. 資料表設定檔（多組具名篩選／排序／欄顯示）。
2. 物件 URI 複製、啟動解析與定位。
3. SQL Server／Oracle／SQLite 原生執行計畫。
4. 唯讀 ER 圖與兩庫結構差異報告，建立模型／同步共用底層。
5. Windows 自動執行＋查詢／匯出／備份作業與記錄。
6. SSH tunnel＋SSL/TLS 選項 UI。
7. 依序擴充 MongoDB、Redis／Garnet、Snowflake，再接專用編輯器、BI、pipeline、schema analyzer 與 Pub/Sub。
