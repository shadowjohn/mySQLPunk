# mySQLPunk 待辦事項與未來規劃 (TODO)

本文件整理自當前專案已知限制、跨平台移植進度與後續功能規劃。長期藍圖亦可參閱 [`docs/ROADMAP.md`](docs/ROADMAP.md)。

---

## 🐧 Linux / macOS 跨平台版功能遷移

跨平台預覽版目前以 Avalonia (.NET 8) 打造，核心資料庫功能已具備，以下功能正分階段由 Windows 完整版遷移：

- [ ] **更多 Provider 支援**
  - [ ] Oracle 連線與基本查詢
  - [ ] MongoDB 文件瀏覽與編輯
  - [ ] Redis / Microsoft Garnet 鍵值瀏覽與監控
  - [ ] Snowflake 直連與查詢
- [ ] **進階工作台工具**
  - [ ] 視覺化 ER Diagram（實體關聯圖）
  - [ ] 資料庫結構差異比較 (Schema Diff)
  - [ ] 資料表設計器 (Table Designer) 完整版
  - [ ] Punky AI 助理跨平台整合
  - [ ] 自動補欄位註解與字典功能
- [ ] **打包與簽章**
  - [ ] macOS 申請 Apple Developer ID 簽名與 Notarization (公證)
  - [ ] Linux 更多發行版套件包裝 (deb, rpm, Flatpak)

---

## 🗄️ 各資料庫 Provider 進階功能補齊

### MySQL / MariaDB
- [ ] 支援更多特殊 Collation 的無損樂觀並行比對
- [ ] 進階 Partition 管理與優化

### SQL Server
- [ ] 降版相容搬移：擴大支援非 dbo schema、包含部分索引與約束的搬移
- [ ] 跨版本實機測試涵蓋率擴充 (SQL Server 2016–2025)
- [ ] 物件名稱含 `.` 時的 schema.table 精確解析修復

### MongoDB
- [ ] 視覺化 Aggregation Pipeline 查詢建立器
- [ ] MongoDB Atlas / SRV 環境專用認證與實機驗收
- [ ] 支援 SSH Tunnel 轉發連線

### Redis / Microsoft Garnet
- [ ] 支援 Redis Cluster 與 Sentinel 拓撲架構
- [ ] 連線斷線自動重連機制與 Pub/Sub 歷史訊息保存
- [ ] 效能趨勢圖表與記憶體即時分析

### Snowflake
- [ ] Key-pair JWT 身份驗證支援
- [ ] 查詢參數綁定與網格雙向寫回
- [ ] 暫存區 (Stage) 管理與 Bulk Data Load

---

## 🛠️ 核心效能與體驗優化

- [ ] **記憶體串流最佳化**
  - [ ] XLSX 超大資料集匯出改為基於 OpenXML 串流寫入，避免高記憶體佔用
  - [ ] 超大 SQL 備份還原支援分段串流解析與執行
- [ ] **網路與代理**
  - [ ] 加入 SOCKS5 代理支援（讓資料庫連線可透過 SOCKS5 穿透）
- [ ] **程式碼簽名**
  - [ ] Windows 安裝檔加入 Code Signing 憑證，消除初次下載的 SmartScreen 警告

---

> 💡 如果您想參與以上任一項目的開發，歡迎閱讀 [`CONTRIBUTING.md`](CONTRIBUTING.md) 並提交 PR！
