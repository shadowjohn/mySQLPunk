# Punky 崩琦｜角色圖像基準

建立日期：2026-10-06。這份是依專案現有原圖整理的新基準，不是追回的原始生成 prompt，也不是 AI SQL 助理的 system prompt。

## 唯一外觀參考

- 原圖：`snapshot/mySQLPunk_avatar.png`
- 既有動畫：`mySQLPunk/image/mySQLPunk_avatar_wink.gif`
- 身分：mySQLPunk 的資料庫管理助手，Punky 崩琦。
- 每次生成都應附上原圖；文字不足以穩定重現同一張臉。新姿勢以原圖為準，不要逐代引用新圖造成角色漂移。

## 固定特徵

- 精緻日系 2D 動漫插畫，清楚線稿、柔和賽璐璐上色。
- 深灰黑、帶深藍／青綠反光的頭髮；束起的高馬尾、側邊長髮與瀏海。
- 青綠色眼睛、青綠色幾何三角髮夾；保持原圖臉型與五官。
- 黑色拉鍊連帽外套、青綠色內裡，小型金屬配件、銀色鏈飾與耳飾。
- 親切、專注，帶一點俏皮的程式設計師氣質。
- 記憶體模組是辨識道具，依場景可放在手邊；不必每張圖都咬在嘴裡。
- 不自行更換髮色、眼睛、髮型、服裝主色，也不添加陌生角色。

## 通用英文基礎 prompt

```text
Create an illustration of Punky, the mySQLPunk database assistant. The attached
snapshot/mySQLPunk_avatar.png is the authoritative character identity reference.
Preserve the same recognizable face, dark charcoal/navy hair with teal highlights,
high tied ponytail, long side bangs, teal-green eyes, geometric teal triangular
hair clips, black zip hoodie with teal lining, silver chain accents and small
ear piercings. Match the original polished 2D anime linework and soft cel shading.
She is friendly, focused and slightly mischievous. Keep her costume and identity
consistent. Use a compact clear composition legible in a desktop application.
No unrelated character, costume redesign, watermark, readable text or extra limbs.
```

## 報表匯出 loading 情境

```text
Use case: identity-preserve.
Asset type: transparent illustration for the mySQLPunk database-report export
loading screen, shown at about 240px.

Use the attached original avatar as the identity and anime-style reference.
Create ONE clean waist-up illustration of the SAME Punky character, seated behind
a small dark laptop, cheerfully concentrating on organizing database schema report
pages. Preserve all fixed character features above. Her laptop has a small generic
teal database cylinder symbol. A small green RAM module may lie beside it as an
identity callback; do not put RAM in her mouth in this working pose. Have one hand
turn a report page and the other rest on the laptop keyboard. Two neatly stacked
report sheets with abstract lines/tables, no readable text.

Centered compact composition with the entire ponytail, hands, laptop and paper
comfortably inside frame. Genuine transparent alpha background. No original
circular badge, halo, scenery, caption or watermark. The illustration must remain
legible when reduced for a desktop progress dialog.
```

## 動畫延伸規格與目前素材

- 以同一張圖做輕微眨眼、看向文件、翻頁或打字，約 3–4 秒無縫循環。
- 頭髮、髮夾、手指、筆電和紙張的位置保持連續；不要換臉或大幅移動鏡頭。
- 背景保持透明，避免邊緣白框；先驗證深／淺色背景。
- 尊重減少動態效果偏好，提供靜態圖。進度資訊使用介面文字，不燒進圖像。
- 本輪先產出 loading 靜態插畫；使用者隨後提供 `snapshot/punky_loading.mp4` 動畫（約 6 秒、544×544、24 fps）。該 MP4 為黑底 H.264，沒有透明通道，原檔保留。
- 改善去背的衍生素材使用 `snapshot/punky_loading_clean*` 命名；保留半透明邊緣，不把 GIF 當作柔邊透明母檔。實際輸出與限制見 `docs/design/report-export/animation/`。
- 原型只在模擬匯出期間播放透明動畫；完成、取消或減少動態效果時使用靜態圖。既有眨眼 GIF 仍可沿用。

## 資產位置與生成紀錄

- 本次使用內建 ImageGen，以原圖作為參考，沒有使用 API key 或外部登入資料。
- 新圖保存於 `snapshot/punky_report_loading.png`；原圖與既有 GIF 保留。
- 網頁預覽用縮小版本為 `snapshot/punky_report_loading_preview.webp`，只調整尺寸與編碼。
- 精確提交給生成工具的 prompt 另存 `docs/design/report-export/loading-image-prompt.txt`。
- 本輪尚未加入 WinForms 資源或發布套件；外觀待使用者檢視。
