"""Read-only validation for clean_loading_video.py outputs."""
import json
import subprocess
from pathlib import Path

import cv2
import numpy as np
from PIL import Image

from clean_loading_video import HERE, REPO, composite, qa_contact_sheet


def read_video(path):
    cap = cv2.VideoCapture(str(path))
    frames = []
    while True:
        ok, bgr = cap.read()
        if not ok:
            break
        frames.append(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))
    cap.release()
    return frames


def read_animation(path):
    image = Image.open(path)
    frames, durations = [], []
    for index in range(image.n_frames):
        image.seek(index)
        frames.append(np.array(image.convert("RGBA")))
        # WebP durations become available after decoding the frame above.
        durations.append(float(image.info.get("duration", 0)))
    return frames, durations


def main():
    source = read_video(REPO / "snapshot" / "punky_loading.mp4")
    cleaned, full_durations = read_animation(REPO / "snapshot" / "punky_loading_clean.apng")
    small, small_durations = read_animation(REPO / "snapshot" / "punky_loading_clean_preview.webp")
    assert len(source) == len(cleaned) == 145
    assert len(small) == 49
    assert abs(sum(full_durations) - 6042) < 1
    assert abs(sum(small_durations) - 6042) < 1
    reconstruction = []
    dark_preserved = []
    for original, rgba in zip(source, cleaned):
        alpha = rgba[:, :, 3]
        assert alpha[0, 0] == alpha[0, -1] == alpha[-1, 0] == alpha[-1, -1] == 0
        assert alpha.min() == 0 and alpha.max() == 255
        reconstruction.append(float(np.abs(np.array(composite(rgba, (0, 0, 0))).astype(float) - original).mean()))
        dark_preserved.append(int(((original.max(axis=2) <= 8) & (alpha == 255)).sum()))

    # The native FFmpeg VP9 decoder may omit VP9's auxiliary alpha stream.
    # Explicitly use libvpx-vp9, then inspect six fully decoded RGBA frames.
    selection = "+".join(f"eq(n,{i})" for i in [0, 28, 57, 86, 115, 144])
    data = subprocess.run([
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-c:v", "libvpx-vp9",
        "-i", str(REPO / "snapshot" / "punky_loading_clean.webm"),
        "-vf", f"select='{selection}'", "-vsync", "0", "-pix_fmt", "rgba",
        "-f", "rawvideo", "pipe:1"], capture_output=True, check=True).stdout
    decoded_webm = np.frombuffer(data, np.uint8).reshape(-1, 544, 544, 4)
    assert len(decoded_webm) == 6
    assert all(frame[..., 3].min() == 0 and frame[..., 3].max() == 255 for frame in decoded_webm)
    qa_contact_sheet(list(decoded_webm), HERE / "webm-six-frames-white-dark.png", [0, 28, 57, 86, 115, 144])
    qa_contact_sheet(small, HERE / "preview-six-frames-white-dark.png")
    composite(small[38], (255, 255, 255)).save(HERE / "preview-lifted-paper-white.png")
    preview_selection = "+".join(f"eq(n,{i})" for i in [0, 14, 28, 43, 57, 72])
    preview_data = subprocess.run([
        "ffmpeg", "-hide_banner", "-loglevel", "error", "-c:v", "libvpx-vp9",
        "-i", str(REPO / "snapshot" / "punky_loading_clean_preview.webm"),
        "-vf", f"select='{preview_selection}'", "-vsync", "0", "-pix_fmt", "rgba",
        "-f", "rawvideo", "pipe:1"], capture_output=True, check=True).stdout
    decoded_preview = np.frombuffer(preview_data, np.uint8).reshape(-1, 256, 256, 4)
    assert len(decoded_preview) == 6
    assert all(frame[..., 3].min() == 0 and frame[..., 3].max() == 255 for frame in decoded_preview)
    qa_contact_sheet(list(decoded_preview), HERE / "preview-webm-six-frames-white-dark.png", [0, 14, 28, 43, 57, 72])

    source_steps = [float(np.abs(a.astype(float) - b).mean()) for a, b in zip(source[:-1], source[1:])]
    original_loop_seam = float(np.abs(source[-1].astype(float) - source[0]).mean())
    alpha_steps = [float(np.abs(a[..., 3].astype(float) - b[..., 3]).mean()) for a, b in zip(cleaned[:-1], cleaned[1:])]
    alpha_loop_seam = float(np.abs(cleaned[-1][..., 3].astype(float) - cleaned[0][..., 3]).mean())
    metrics = {
        "passed": True,
        "full_frame_count": len(cleaned), "preview_frame_count": len(small),
        "apng_duration_ms": sum(full_durations), "webp_duration_ms": sum(small_durations),
        "alpha": "all 145 APNG frames have fully transparent corners and fully opaque subject pixels; six VP9 frames decode real alpha with libvpx-vp9",
        "source_loop_seam_rgb_mae": original_loop_seam,
        "source_adjacent_rgb_mae_median": float(np.median(source_steps)),
        "source_adjacent_rgb_mae_p95": float(np.percentile(source_steps, 95)),
        "cleaned_alpha_loop_seam_mae": alpha_loop_seam,
        "cleaned_alpha_adjacent_mae_median": float(np.median(alpha_steps)),
        "recomposite_black_rgb_mae_mean": float(np.mean(reconstruction)),
        "recomposite_black_rgb_mae_worst": max(reconstruction),
        "opaque_dark_pixels_preserved_per_frame_min": min(dark_preserved),
        "opaque_dark_pixels_preserved_per_frame_max": max(dark_preserved),
        "loop_note": "The original poses at frame 0 and 144 differ. No frame reordering, crossfade or fabricated seamless loop was introduced.",
    }
    (HERE / "verification-metrics.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
    print(json.dumps(metrics, indent=2))


if __name__ == "__main__":
    main()
