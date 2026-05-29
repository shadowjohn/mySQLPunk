# SpatiaLite runtime

這個目錄是 SQLite / SpatiaLite runtime 的輸出位置。為了讓 DLL 來源可追溯，請優先使用 `tools/spatialite/Build-SpatiaLiteRuntime.ps1` 從 Gaia-SINS 官方 `libspatialite-5.1.0.zip` 原始碼重新建置，並將產生的 `SPATIALITE_RUNTIME_MANIFEST.json` 一起提交。

若手動更換 DLL，請至少確認：

- `mod_spatialite.dll` 或可被 `SQLiteConnection.LoadExtension` 載入的 SpatiaLite DLL 存在
- 相依 DLL 與 `proj.db` 已放在同一個目錄
- `SQLite.Interop.dll` 位於輸出目錄的 `x64` / `x86` 子目錄
- `SPATIALITE_RUNTIME_MANIFEST.json` 保留來源 URL、來源 SHA-256 與輸出檔案 hash

離線環境可把官方 `libspatialite-5.1.0.zip` 放在 `tools/spatialite/offline` 或 `tools/spatialite/cache`，再用 `Build-SpatiaLiteRuntime.ps1 -OfflinePackagePath ...` 或 `-PreferCachedSource` 重建 runtime。連線診斷會顯示目前 manifest、來源快取與離線包偵測結果。
