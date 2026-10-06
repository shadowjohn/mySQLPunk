"""Clean a black-backed video without recoloring/redrawing its subject.

Requires local Python with numpy, scipy, OpenCV and Pillow (WebP support).
Only reads the original video. Run from any directory; outputs live beside
the source and QA evidence lives beside this script.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw
from scipy import ndimage as ndi


HERE = Path(__file__).resolve().parent
REPO = HERE.parents[3]


def border_background(rgb: np.ndarray) -> tuple[np.ndarray, dict]:
    level = rgb.max(axis=2)
    dark = level <= 8
    seed = np.zeros_like(dark)
    seed[0], seed[-1] = dark[0], dark[-1]
    seed[:, 0], seed[:, -1] = dark[:, 0], dark[:, -1]
    background = ndi.binary_propagation(seed, mask=dark)
    external_count = int(background.sum())

    # Pure-black pockets enclosed by thin outer hair loops are also background.
    # Their proximity to the exterior is essential: never remove all dark
    # components globally, which would puncture hair, eyes, jacket and laptop.
    labels, _ = ndi.label(dark)
    recovered = 0
    pockets = []
    for label, bounds in enumerate(ndi.find_objects(labels), 1):
        component = labels[bounds] == label
        area = int(component.sum())
        if area < 20 or background[bounds][component].any():
            continue
        if float(level[bounds][component].mean()) < 4:
            pockets.append((bounds, component, area))
    # A lifted sheet can enclose a larger black pocket behind several thin
    # hair strands. Follow only nearby near-black components from the exterior;
    # do not cap the depth of a legitimately reached background component.
    for _ in range(8):
        distance = ndi.distance_transform_edt(~background)
        additions = 0
        for bounds, component, area in pockets:
            if (not background[bounds][component].any()
                    and float(distance[bounds][component].min()) <= 7):
                background[bounds] |= component
                recovered += area
                additions += 1
        if not additions:
            break

    foreground = ~background
    labels, _ = ndi.label(foreground)
    sizes = np.bincount(labels.ravel())
    foreground &= sizes[labels] >= 10  # discard tiny isolated codec speckles
    return foreground, {"exterior_background_pixels": external_count,
                        "outer_hair_pocket_pixels": recovered}


def matte(rgb: np.ndarray) -> tuple[np.ndarray, dict]:
    foreground, details = border_background(rgb)
    core = ndi.binary_erosion(foreground, iterations=2, border_value=1)
    distance, nearest = ndi.distance_transform_edt(~core, return_indices=True)
    color = rgb.astype(np.float32)
    reference = color[nearest[0], nearest[1]]
    coverage = ((color * reference).sum(axis=2)
                / np.maximum((reference * reference).sum(axis=2), 20.0))
    alpha = foreground.astype(np.float32)
    band = distance <= 4
    alpha[band] = np.clip(coverage[band], 0, 1)
    alpha[core] = 1
    alpha[(~foreground) & (rgb.max(axis=2) <= 2)] = 0

    # Subpixel smoothing only; the opaque core never becomes transparent.
    soft = ndi.gaussian_filter(alpha, 0.50)
    boundary = (soft > 0) & (soft < .995) & ~core
    alpha[boundary] = .4 * alpha[boundary] + .6 * soft[boundary]
    alpha[alpha < .025] = 0
    return alpha, details


def stabilize(alpha: np.ndarray, rgb: np.ndarray,
              previous_alpha: np.ndarray, previous_rgb: np.ndarray) -> np.ndarray:
    # Backward flow aligns the prior matte with the current frame. Blend only
    # agreeing edge pixels: no unaligned averaging/trails on moving hands/hair.
    gray = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY)
    previous_gray = cv2.cvtColor(previous_rgb, cv2.COLOR_RGB2GRAY)
    flow = cv2.calcOpticalFlowFarneback(gray, previous_gray, None,
                                      .5, 3, 15, 3, 5, 1.1, 0)
    height, width = alpha.shape
    xx, yy = np.meshgrid(np.arange(width, dtype=np.float32),
                         np.arange(height, dtype=np.float32))
    maps = (xx + flow[..., 0], yy + flow[..., 1])
    old_alpha = cv2.remap(previous_alpha, *maps, cv2.INTER_LINEAR,
                          borderMode=cv2.BORDER_CONSTANT)
    old_rgb = cv2.remap(previous_rgb, *maps, cv2.INTER_LINEAR,
                        borderMode=cv2.BORDER_CONSTANT)
    residual = np.abs(rgb.astype(np.float32) - old_rgb).mean(axis=2)
    agree = ((alpha > .025) & (alpha < .975)
             & (residual < 5) & (np.abs(alpha - old_alpha) < .18))
    alpha[agree] = .85 * alpha[agree] + .15 * old_alpha[agree]
    return alpha


def rgba_from_black(rgb: np.ndarray, alpha: np.ndarray) -> np.ndarray:
    # The source was composited on black. Dividing by coverage removes the
    # premultiplied black fringe rather than lightening the whole character.
    colors = np.clip(rgb.astype(np.float32) / np.maximum(alpha[..., None], .04), 0, 255)
    colors[alpha == 0] = 0
    return np.dstack((np.rint(colors).astype(np.uint8),
                     np.rint(alpha * 255).astype(np.uint8)))


def composite(rgba: np.ndarray, background: tuple[int, int, int]) -> Image.Image:
    foreground = Image.fromarray(rgba, "RGBA")
    canvas = Image.new("RGBA", foreground.size, background + (255,))
    return Image.alpha_composite(canvas, foreground).convert("RGB")


def qa_contact_sheet(frames: list[np.ndarray], destination: Path, labels=None) -> None:
    indices = np.linspace(0, len(frames) - 1, 6, dtype=int)
    sheet = Image.new("RGB", (6 * 290, 2 * 320), "#e5e7eb")
    draw = ImageDraw.Draw(sheet)
    for row, background in enumerate([(255, 255, 255), (43, 49, 59)]):
        for column, index in enumerate(indices):
            preview = composite(frames[int(index)], background).resize((280, 280), Image.Resampling.LANCZOS)
            x, y = column * 290 + 5, row * 320 + 28
            sheet.paste(preview, (x, y))
            frame_label = labels[int(index)] if labels is not None else int(index)
            draw.text((x, y - 20), f"Frame {frame_label:03d} / {'WHITE' if row == 0 else 'DARK GRAY'}", fill="#17202b")
    sheet.save(destination)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, default=REPO / "snapshot" / "punky_loading.mp4")
    parser.add_argument("--output", type=Path, default=REPO / "snapshot" / "punky_loading_clean")
    arguments = parser.parse_args()
    original_hash = hashlib.sha256(arguments.source.read_bytes()).hexdigest()
    capture = cv2.VideoCapture(str(arguments.source))
    fps = float(capture.get(cv2.CAP_PROP_FPS))
    source_frames: list[np.ndarray] = []
    while True:
        success, bgr = capture.read()
        if not success:
            break
        source_frames.append(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))
    capture.release()
    if not source_frames:
        raise RuntimeError(f"No video frames decoded: {arguments.source}")

    cleaned, statistics = [], []
    previous_alpha = None
    for index, rgb in enumerate(source_frames):
        alpha, stats = matte(rgb)
        if previous_alpha is not None:
            alpha = stabilize(alpha, rgb, previous_alpha, source_frames[index - 1])
        previous_alpha = alpha.copy()
        cleaned.append(rgba_from_black(rgb, alpha))
        stats.update(frame=index, transparent_fraction=float((alpha == 0).mean()),
                     opaque_fraction=float((alpha == 1).mean()),
                     soft_fraction=float(((alpha > 0) & (alpha < 1)).mean()))
        statistics.append(stats)
    count = len(cleaned)
    durations = [round((i + 1) * 1000 / fps) - round(i * 1000 / fps) for i in range(count)]
    apng = arguments.output.with_suffix(".apng")
    full = [Image.fromarray(frame, "RGBA") for frame in cleaned]
    full[0].save(apng, format="PNG", save_all=True, append_images=full[1:],
                 duration=durations, loop=0, disposal=0, blend=0, compress_level=6)
    print(f"APNG: {apng.stat().st_size} bytes", flush=True)

    webm = arguments.output.with_suffix(".webm")
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(apng),
                    "-an", "-c:v", "libvpx-vp9", "-crf", "22", "-b:v", "0",
                    "-pix_fmt", "yuva420p", "-auto-alt-ref", "0", "-row-mt", "1",
                    "-r", str(fps), "-y", str(webm)], check=True)
    print(f"WebM alpha: {webm.stat().st_size} bytes", flush=True)
    preview_webm = arguments.output.with_name(arguments.output.name + "_preview").with_suffix(".webm")
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(apng),
                    "-an", "-vf", "fps=12,scale=256:256:flags=lanczos", "-c:v", "libvpx-vp9",
                    "-crf", "34", "-b:v", "0", "-pix_fmt", "yuva420p", "-auto-alt-ref", "0",
                    "-row-mt", "1", "-y", str(preview_webm)], check=True)

    # Keep source timing while halving the frame rate for the inline preview.
    selected = list(range(0, count, 2))
    small_durations = [sum(durations[i:min(i + 2, count)]) for i in selected]
    preview = [full[i].resize((256, 256), Image.Resampling.LANCZOS) for i in selected]
    webp = arguments.output.with_suffix(".webp")
    tried = []
    for quality in (32,):
        preview[0].save(webp, format="WEBP", save_all=True, append_images=preview[1:],
                        duration=small_durations, loop=0, quality=quality, method=4,
                        lossless=False, alpha_quality=100, minimize_size=False)
        tried.append({"quality": quality, "bytes": webp.stat().st_size})
        print(f"WebP quality {quality}: {webp.stat().st_size} bytes", flush=True)
    subprocess.run([sys.executable, str(HERE / "make_small_webp.py"), "--apng", str(apng),
                    "--output", str(arguments.output.with_name(arguments.output.name + "_preview").with_suffix(".webp"))], check=True)

    qa_contact_sheet(cleaned, HERE / "clean-six-frames-white-dark.png")
    # Full-resolution edges are inspected separately from the downscaled sheet.
    composite(cleaned[0], (255, 255, 255)).save(HERE / "clean-frame-000-white.png")
    composite(cleaned[115], (255, 255, 255)).save(HERE / "clean-frame-115-white.png")
    Image.fromarray(cleaned[0], "RGBA").save(HERE / "clean-frame-000-rgba.png")

    decoded_webp = Image.open(webp)
    decoded_apng = Image.open(apng)
    report = {
        "source": str(arguments.source), "source_sha256": original_hash,
        "original_unchanged": hashlib.sha256(arguments.source.read_bytes()).hexdigest() == original_hash,
        "source_fps": fps, "source_frames": count, "duration_ms": sum(durations),
        "audio": "not included in transparent loading assets",
        "webp": {"path": str(webp), "width": 256, "height": 256, "frames": decoded_webp.n_frames,
                 "bytes": webp.stat().st_size, "quality_trials": tried, "mode": decoded_webp.mode},
        "apng": {"path": str(apng), "width": full[0].width, "height": full[0].height,
                 "frames": decoded_apng.n_frames, "bytes": apng.stat().st_size, "mode": decoded_apng.mode},
        "webm": {"path": str(webm), "width": full[0].width, "height": full[0].height,
                 "bytes": webm.stat().st_size, "fps": fps, "alpha": "VP9 alpha_mode=1"},
        "preview_webm": {"path": str(preview_webm), "width": 256, "height": 256,
                         "bytes": preview_webm.stat().st_size, "fps": 12, "alpha": "VP9 alpha_mode=1"},
        "preview_webp": json.loads((HERE / "preview-webp-metadata.json").read_text(encoding="utf-8")),
        "frames": statistics,
    }
    (HERE / "clean-validation.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "frames"}, indent=2))


if __name__ == "__main__":
    main()
