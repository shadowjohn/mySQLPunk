# SpatiaLite runtime

這個目錄是 SQLite / SpatiaLite runtime 的輸出位置。為了讓 DLL 來源可追溯，請優先使用 `tools/spatialite/Build-SpatiaLiteRuntime.ps1` 從 Gaia-SINS 官方 `libspatialite-5.1.0.zip` 原始碼重新建置，並將產生的 `SPATIALITE_RUNTIME_MANIFEST.json` 一起提交。

若手動更換 DLL，請至少確認：

- `mod_spatialite.dll` 或可被 `SQLiteConnection.LoadExtension` 載入的 SpatiaLite DLL 存在
- 相依 DLL 與 `proj.db` 已放在同一個目錄
- `SQLite.Interop.dll` 位於輸出目錄的 `x64` / `x86` 子目錄
