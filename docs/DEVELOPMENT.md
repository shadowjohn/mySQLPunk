# mySQLPunk 開發、建置與測試指南

本文件提供 mySQLPunk 在本機的建置步驟、實機 Docker 測試矩陣以及發布打包指南。

---

## 目錄

- [Linux / macOS 跨平台版建置](#linux--macos-跨平台版建置)
- [Windows 完整版建置](#windows-完整版建置)
- [自動打包與發佈](#自動打包與發佈)
- [測試套件與實機驗收矩陣](#測試套件與實機驗收矩陣)

---

## Linux / macOS 跨平台版建置

跨平台版採用 .NET 8 與 Avalonia UI。

### 必要環境
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Linux：若需保存密碼至系統 Keychain，需安裝提供 `secret-tool` 的套件：
  ```bash
  sudo apt-get install libsecret-tools  # Ubuntu / Debian
  ```
- macOS：直接使用系統內建 Keychain。

### 建置與本機執行
```bash
# 還原相依套件與編譯
dotnet restore mySQLPunk.CrossPlatform.sln
dotnet build mySQLPunk.CrossPlatform.sln -c Release --no-restore

# 執行跨平台 Smoke Tests
dotnet run --project mySQLPunk.CrossPlatform.SmokeTests/mySQLPunk.CrossPlatform.SmokeTests.csproj -c Release --no-build

# 啟動跨平台桌面應用程式
dotnet run --project mySQLPunk.Desktop/mySQLPunk.Desktop.csproj -c Release
```

### 跨平台打包 (Self-contained)
```bash
# Linux
./scripts/package-cross-platform.sh --version 1.0.0.23 --runtime linux-x64
./scripts/package-cross-platform.sh --version 1.0.0.23 --runtime linux-arm64

# macOS（需在 macOS 系統執行以進行 codesign 驗證）
./scripts/package-cross-platform.sh --version 1.0.0.23 --runtime osx-x64
./scripts/package-cross-platform.sh --version 1.0.0.23 --runtime osx-arm64
```

---

## Windows 完整版建置

Windows 完整版採用 .NET Framework 4.7.2 與 WinForms。

### 必要環境
- Windows 10 / 11
- Visual Studio 2017+ 或 Visual Studio Build Tools 2019+
- .NET Framework 4.7.2 Developer Pack
- Inno Setup 6（若需本機打包安裝檔 EXE）

### 命令列快速建置與執行
在專案根目錄下：

```powershell
.\build.bat          # 編譯 Debug
.\build.bat release  # 編譯 Release
.\run.bat            # 編譯 Debug 並啟動
.\run.bat release    # 編譯 Release 並啟動
```

或使用 MSBuild 手動建置：
```powershell
nuget restore .\mySQLPunk.sln
msbuild .\mySQLPunk.sln /p:Configuration=Release /p:Platform="Any CPU"
```

### Windows 安裝檔打包
```powershell
.\scripts\package-release.ps1 -Version 1.0.0.23
```
該腳本會建置 Release，並以 Inno Setup 6 輸出 `dist/mySQLPunk-<version>-win-x64-setup.exe`。

---

## 自動打包與發佈

專案使用 GitHub Actions 完成全自動化發佈：

1. 確認 `mySQLPunk/Properties/AssemblyInfo.cs` 版本號已更新。
2. 建立並推送 Git Tag：
   ```powershell
   git tag v1.0.0.23
   git push origin v1.0.0.23
   ```
3. `.github/workflows/release.yml` 會觸發多平台 runner 同時打包 Linux x64/arm64、macOS x64/arm64 與 Windows Setup EXE，校驗 SHA-256 後自動發佈至 GitHub Release。
4. 詳細自動化規則請見 [`docs/RELEASE_AUTOMATION.md`](RELEASE_AUTOMATION.md)。

---

## 測試套件與實機驗收矩陣

### 1. Windows 端快速 Smoke Tests
```powershell
.\tests\Run-SmokeTests.ps1
```
覆蓋 View SQL 轉換、WKB/WKT 幾何轉換、SQLite 專用語法、Table Designer DDL 生成、URI 匯入、MongoDB / Redis / Snowflake client、憑證與排程服務等純邏輯與通訊測試。

### 2. 跨平台 Docker 實機整合測試
```bash
./tests/Run-CrossPlatformLiveTests.sh
```
啟動 MySQL 8、MariaDB 11.4、PostgreSQL 16、SQL Server 2022、TiDB 8.5 容器，驗證 TLS 憑證精確比對、SSH Tunnel 穿透與資料庫查詢。

### 3. MySQL / MariaDB 使用者權限實機測試
```powershell
.\tests\Run-MySqlUserIntegrationTests.ps1
```
自動測試 MySQL 5.6、5.7、8.0、MariaDB 10.6、10.11、11.4 之使用者建立、修改、權限賦予與 `SHOW GRANTS`。

### 4. MySQL / MariaDB 匯出、匯入與重命名測試
```powershell
.\tests\Run-MySqlExportRenameIntegrationTests.ps1
```
驗證串流 Dump、特殊字元、utf8mb4、預存程序、Triggers 匯出與官方 CLI 二次匯入驗證。

### 5. 多資料庫重命名 (Rename) 測試
```powershell
.\tests\Run-DatabaseRenameProviderIntegrationTests.ps1
```
驗證 SQLite 檔案移動、PostgreSQL `ALTER DATABASE ... RENAME` 與 SQL Server `ALTER DATABASE ... MODIFY NAME`。
