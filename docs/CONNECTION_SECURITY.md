# 連線安全：SSL/TLS 與 SSH Tunnel

MySQL / MariaDB、PostgreSQL、SQL Server 與 Oracle 的新增／編輯連線視窗都有「SSL / SSH」按鈕。這些設定同時套用在主畫面連線、測試連線與 Windows 自動執行作業。SQLite / SpatiaLite 直接開啟本機檔案，不需要這一層網路設定。

## SSL/TLS 模式

| Provider | 可用模式 | 其他設定 |
| --- | --- | --- |
| MySQL / MariaDB | Preferred、Required、VerifyCA、VerifyFull、Disabled | CA、用戶端憑證、用戶端私鑰、憑證密碼 |
| PostgreSQL | Prefer、Require、VerifyCA、VerifyFull、Disable | CA、用戶端憑證、用戶端私鑰、憑證密碼、撤銷檢查 |
| SQL Server | Disabled、Required、VerifyFull | Required 會加密但信任伺服器憑證；VerifyFull 會驗證憑證鏈與主機名稱 |
| Oracle Basic | Disabled、Required、VerifyFull | Required 改用 TCPS；可指定 Wallet，VerifyFull 另啟用伺服器 DN 比對 |

正式環境優先選 VerifyFull。MySQL / PostgreSQL 使用 VerifyCA 或 VerifyFull 時，畫面會要求選擇 CA 憑證，避免看似啟用驗證卻缺少信任來源。

Oracle TNS 的傳輸與憑證設定由 `tnsnames.ora` 管理，mySQLPunk 不會改寫它。要在畫面設定 TCPS、Wallet 或 SSH Tunnel，請使用 Oracle Basic 連線。

## SSH Tunnel

1. 勾選「透過 SSH Tunnel 連線」。
2. 填入 SSH 主機、連接埠與使用者名稱。
3. 填 SSH 密碼、選擇私鑰，或兩者都填。若私鑰已加密，再填私鑰密語。
4. 填入經伺服器管理員確認的 SHA256 主機金鑰指紋。
5. 回到連線視窗按「測試連線」。

OpenSSH 顯示的指紋格式類似 `SHA256:abc...`。可以請管理員直接提供，或在可信任的管理管道先取得公鑰後核對：

```powershell
ssh-keyscan -p 22 ssh.example.com | ssh-keygen -lf -
```

不要只因為第一次連線成功就接受不明指紋。mySQLPunk 會在 SSH 驗證階段做完全比對，不相符就中止連線。Tunnel 只監聽本機 `127.0.0.1` 的動態連接埠，不會對區域網路開放，資料庫連線關閉時也會一併停止。

SSH Tunnel 會把資料庫連線端點改成 `127.0.0.1`，因此 TLS 的 VerifyFull 無法再以原始資料庫主機名稱比對憑證。畫面會阻擋這個組合；MySQL / PostgreSQL 請改用 VerifyCA，SQL Server / Oracle 請改用 Required，遠端主機身分則由 SSH 主機金鑰指紋驗證。

## 祕密保存位置

下列資料只存進目前 Windows 使用者的 Credential Manager：

- 資料庫密碼
- SSH 密碼
- SSH 私鑰密語
- 用戶端憑證密碼

`setting.ini` 或連線設定檔只保存非祕密選項、檔案路徑與 credential target。自動執行作業 JSON 只引用設定檔與連線名稱。排程必須由建立設定的同一個 Windows 使用者執行，才能讀到這些憑證。

CA、用戶端憑證、私鑰與 Oracle Wallet 的檔案本身不會複製進 mySQLPunk；搬到另一台電腦時要另外安全部署，並在連線設定重新選擇路徑。
