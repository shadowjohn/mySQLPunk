# SpatiaLite Runtime 原始碼建置流程

這個資料夾提供可重現的 SpatiaLite runtime 建置腳本，目標是處理 issue #8：不要只依賴來路不明的 DLL，而是能從官方 libspatialite 5.1.0 原始碼重新編譯後引入 `mySQLPunk/binary/sqlite3_ext`。

## 需求

- Windows
- MSYS2 mingw64
- PowerShell 5 或更新版本

## 建置

```powershell
.\tools\spatialite\Build-SpatiaLiteRuntime.ps1
```

腳本會：

- 從 Gaia-SINS 官方來源下載 `libspatialite-5.1.0.zip`
- 透過 MSYS2 安裝 mingw64 相依套件
- 執行 `configure`、`make`、`make install`
- 複製 `mod_spatialite.dll` 或 `libspatialite.dll`、相依 DLL、`sqlite3.exe`、`proj.db`
- 產生 `SPATIALITE_RUNTIME_MANIFEST.json`，紀錄來源 URL、source SHA256 與輸出檔案 SHA256

若要固定來源檔 hash，可加上：

```powershell
.\tools\spatialite\Build-SpatiaLiteRuntime.ps1 -ExpectedSha256 "<官方來源 zip 的 SHA256>"
```

## 引入專案

建置完成後，確認 `mySQLPunk/binary/sqlite3_ext` 內的 DLL 與 manifest 一起提交。`mySQLPunk.csproj` 已將 `binary/sqlite3_ext/**/*` 設為輸出內容，建置時會複製到執行目錄。
