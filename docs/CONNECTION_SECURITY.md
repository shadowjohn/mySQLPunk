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

### Windows 完整版

下列資料只存進目前 Windows 使用者的 Credential Manager：

- 資料庫密碼
- SSH 密碼
- SSH 私鑰密語
- 用戶端憑證密碼

`setting.ini` 或連線設定檔只保存非祕密選項、檔案路徑與 credential target。自動執行作業 JSON 只引用設定檔與連線名稱。排程必須由建立設定的同一個 Windows 使用者執行，才能讀到這些憑證。

CA、用戶端憑證、私鑰與 Oracle Wallet 的檔案本身不會複製進 mySQLPunk；搬到另一台電腦時要另外安全部署，並在連線設定重新選擇路徑。

### Linux / macOS 跨平台預覽版

跨平台版的 `connections.json` 只保存主機、帳號、provider 與 `UseSecretStore` 等非祕密設定，`Password` 明確排除序列化。連線設定頁可選擇將資料庫密碼交給目前平台的系統密碼庫：

- Linux：使用 `secret-tool` 連接 freedesktop.org Secret Service。Ubuntu / Debian 可安裝 `libsecret-tools`，並需要已解鎖的 GNOME Keyring 或相容 Secret Service。
- macOS：使用系統內建 `security` 連接使用者 Keychain。

Linux 密碼內容直接從 stdin 傳給 `secret-tool`；macOS 密碼先轉為 UTF-8／Base64，再透過 `security` 互動模式的 stdin 寫入。兩者都不把祕密放在 process arguments，寫入後也會立即讀回比對。刪除連線、取消勾選保存或清空已載入的密碼時，程式會刪除對應 Keyring 項目。

若平台工具不存在、桌面 Secret Service 未啟動、Keyring 尚未解鎖或操作驗證失敗，程式不會建立明文或自行加密的檔案 fallback；密碼只保留到本次程式關閉，下一次連線時重新要求輸入。

#### 跨平台版 TLS 模式

跨平台版的連線設定頁以「TLS 模式」下拉取代舊的「優先使用 SSL/TLS」勾選，選項依 provider 原生語意列出：

| Provider | 可用模式 | 驅動程式預設 |
| --- | --- | --- |
| MySQL / MariaDB | Disabled、Preferred、Required、VerifyCA、VerifyFull | Preferred（可退回未加密） |
| PostgreSQL | Disable、Allow、Prefer、Require、VerifyCA、VerifyFull | Prefer（可退回未加密） |
| SQL Server | Optional、Mandatory、Strict | Mandatory（驗證憑證鏈與主機名稱） |
| SQLite | 不適用 | 固定 Disabled |

`connections.json` 以 `tlsMode` 字串保存模式；舊檔案的 `useSsl` 會在載入時依 provider 遷移：MySQL／PostgreSQL 的 `true` 對應 Preferred、`false` 對應 Disabled，SQL Server 的 `true` 對應 Mandatory、`false` 對應 Optional。同一筆設定同時包含 `useSsl` 與 `tlsMode`、`tlsMode` 不是明確名稱、模式與 provider 不相容，或檔案含 `password` 欄位時，載入一律 fail closed 並提示修正，不會猜測較弱的模式。切換資料庫類型時，畫面會把目前模式映射到新 provider 最接近且不較弱的選項，並在狀態列提示確認。

#### 跨平台版 TLS 憑證檔案

TLS 模式下方可指定憑證檔案，路徑必須是本機絕對路徑、不含控制字元，並會保存在 `connections.json`（只保存路徑，不保存憑證或私鑰內容）：

| 欄位 | MySQL / MariaDB | PostgreSQL | SQL Server |
| --- | --- | --- | --- |
| CA／伺服器憑證 | `SslCa`（PEM／DER），需 VerifyCA 或 VerifyFull | `RootCertificate`（PEM），需 VerifyCA 或 VerifyFull | `ServerCertificate`（PEM／DER／CER，與伺服器憑證精確比對），需 Mandatory 或 Strict |
| 客戶端憑證＋私鑰 | `SslCert` ＋ `SslKey`（PEM，成對），需 Required 以上 | `SslCertificate` ＋ `SslKey`（PEM，成對），需 Require 以上 | 不支援，欄位會隱藏 |

規則一律 fail closed，避免「有填憑證但驅動程式其實忽略」的假安全：CA 憑證搭配不驗證憑證的模式（Preferred、Required、Allow、Optional、Disabled）會拒絕儲存；客戶端憑證與私鑰缺一、指向同一檔案、或搭配可能退回未加密的模式也會拒絕；合併的 PFX／PKCS#12 與加密私鑰目前不支援。儲存、測試連線與實際連線前都會確認每個檔案存在且不是目錄，Unix 上的私鑰檔若可被群組或其他使用者讀取（例如 `0644`）會被拒絕，請改為 `chmod 600`。載入 `connections.json` 時只驗證格式與相容性，不要求檔案當下存在，因此放在外接媒體上的憑證不會讓整份設定檔無法載入。切換資料庫類型時，若新 provider 不支援客戶端憑證，欄位會清空並隱藏；SQLite 不使用任何憑證欄位。

#### 跨平台版連線 URI

連線設定頁最上方可貼上 `mysql://`／`mariadb://`、`postgres://`／`postgresql://`、`mssql://`／`sqlserver://` 或 `sqlite:///絕對路徑` URI，按「安全套用」後會填入 provider、主機、port、使用者、密碼、資料庫、逾時與 TLS 模式，不會自動連線或儲存。支援的查詢參數：

- 共同：`name`（連線名稱）、`timeout`（1–300 秒）。
- MySQL / MariaDB：`sslmode=disabled|preferred|required|verify-ca|verify-full`，或 `ssl=true|false`（分別對應 Required 與 Disabled）；兩者不可同時出現。
- PostgreSQL：`sslmode=disable|allow|prefer|require|verify-ca|verify-full`。
- SQL Server：`encrypt=true|false|yes|no|mandatory|optional|strict`。
- 憑證檔案（值為 percent-encoded 的本機絕對路徑，規則同上）：MySQL / MariaDB `sslca`、`sslcert`、`sslkey`；PostgreSQL `sslrootcert`、`sslcert`、`sslkey`；SQL Server `servercertificate`。

URI 輸入框以遮蔽字元顯示，成功套用後立即清空。未知、重複或空白參數、fragment、多層資料庫路徑、dot-segment、未編碼空白、非法百分比編碼、非 UTF-8 位元組、Unicode 控制／格式／noncharacter 字元、超過 8,192 字元的內容，以及與 provider 不相容的 TLS 值，一律拒絕並只顯示不含密碼的錯誤訊息。
