# Punky loading video transparency

2026-10-06. The user-provided `snapshot/punky_loading.mp4` is preserved unchanged
(SHA-256 `12eeb649c4282f6ed8ca352b13064fb92f9b00f40e7f484d32e9db5c885d4ff0`).
The clip contains 145 H.264 video frames at 544 × 544 / 24 fps, plus audio and a
cover image. The loading assets omit audio.

| Asset in `snapshot/` | Size | Timing | Intended use |
| --- | ---: | --- | --- |
| `punky_loading_clean_preview.webm` | 295,074 bytes | 256 × 256, 12 fps, 73 frames, 6.083 s | Preferred inline loading animation; VP9 alpha |
| `punky_loading_clean_preview.webp` | 446,864 bytes | 192 × 192, about 8 fps, 49 frames, 6.042 s | Small animated WebP fallback |
| `punky_loading_clean.webp` | 1,016,510 bytes | 256 × 256, about 12 fps, 73 frames, 6.042 s | Larger animated WebP alternative; do not inline in the prototype |
| `punky_loading_clean.webm` | 3,336,417 bytes | 544 × 544, 24 fps, 145 frames, 6.042 s | Full-size VP9 alpha |
| `punky_loading_clean.apng` | 50,185,325 bytes | 544 × 544, 145 frames, 6.042 s | Lossless RGBA master, local-only / Git-ignored |

The 12 fps WebM ends 41 ms later because its duration is quantized to complete
video frames. WebP/APNG preserve the original duration with per-frame delays.

## Processing

This is video matting, not image generation. The original pose, face, props and
animation frames are retained. The static PNG was used as a visual identity
reference only; no new character or video frame was generated.

1. Inspect six source frames. Exact black occupies about 26–35% of each sample;
   values with maximum RGB channel ≤ 12 occupy about 33–39%.
2. Detect near-black pixels (maximum channel ≤ 8) connected to the outer border.
   Do not globally key out dark colors: that damages the jacket and dark hair.
3. Follow nearby, almost-black background pockets across thin hair strands.
   This also clears the enclosed gap created when the paper is lifted. Each
   accepted pocket has at least 20 pixels, mean maximum channel < 4, and comes
   within 7 pixels of already identified background. Interior dark details
   without this connection are retained.
4. Estimate alpha in a narrow edge band, remove the premultiplied black fringe,
   and apply subpixel feathering. Optical-flow-aligned, low-weight temporal
   smoothing is restricted to agreeing edge pixels to avoid motion trails.
5. Downsample with Lanczos, then encode the small assets. Use the animation only
   while work is running; respect reduced-motion preferences in the UI.

## Evidence

- `source-analysis.json`, `source-frames.png`: measured source and six samples.
- `clean-six-frames-white-dark.png`: full-resolution matte, six poses on white
  and dark gray, reduced only for the contact sheet.
- `preview-webm-six-frames-white-dark.png`: six frames **decoded from the final
  small WebM with libvpx-vp9**, composited on both backgrounds.
- `preview-six-frames-white-dark.png`: six final WebP frames on both backgrounds.
- `clean-validation.json`, `verification-metrics.json`: dimensions, counts,
  alpha and reconstruction checks. All 145 APNG frames have transparent corners
  and opaque subject pixels. Both WebM assets pass decoded alpha checks.
- Between 1,985 and 9,300 near-black subject pixels per frame remain fully opaque;
  a global black color key would incorrectly remove these pixels.
- Recomposition over black differs from the decoded source by a mean absolute
  RGB error of 0.025 / 255 averaged over frames (worst frame: 0.068 / 255).

## Limits

The MP4 had already flattened transparency onto black, so the original alpha
cannot be recovered perfectly. Fine hair and some high-contrast paper/laptop
edges still contain source compression and line-detail irregularities at full
resolution. The 256 px version reduces these visible speckles. There is no claim
of perfect strand recovery or manually retouched frames.

The **original clip itself is not a seamless loop**: its first and last poses
differ. Source first/last RGB mean absolute difference is 28.70, versus 8.04 for
the median adjacent frame pair. No crossfade, time reversal, pose replacement,
or artificial loop repair was added. This original pose jump is distinct from
possible minor matte fluctuations along thin moving hair.

GIF was not produced because its single-bit transparency would reintroduce
rough edges. Actual browser playback and WinForms integration are separate UI
acceptance boundaries; these files were verified by decoding and compositing.

## Reproduce

Use the existing local Python 3.12 environment with NumPy, SciPy, OpenCV, Pillow
and FFmpeg (including libvpx-vp9); no model downloads are needed.

```powershell
python .\docs\design\report-export\animation\clean_loading_video.py
python .\docs\design\report-export\animation\validate_clean_video.py
```

To re-encode only the small WebP from the local APNG master:

```powershell
python .\docs\design\report-export\animation\make_small_webp.py
```

The analysis and processing scripts modify only these generated loading assets
and local QA evidence. They do not read database connections or application
credentials.
