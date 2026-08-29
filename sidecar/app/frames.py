"""Choosing which video frames are worth sending to the model.

Frame selection, not the model call, is where the cost of the vision path is decided. A
naive sample is both expensive and bad: a caption that sits on screen for forty seconds
yields a dozen identical frames, and scene-change detection spends its budget on empty
cutting-board transitions between shots.

Two filters, cheapest first:

1. **Near-duplicate removal** by difference hash. This is what collapses a persistent
   overlay from twelve frames to one.
2. **Blank-frame removal** by detail score. Transition shots of an empty board carry
   neither text nor ingredients and are pure waste.

An OCR gate would be better still — only pay for frames that actually carry text — but it
needs a local OCR engine, and dedupe plus a detail floor already removes most of the waste.
"""

from __future__ import annotations

import base64
import pathlib
import re
import subprocess
from dataclasses import dataclass

from PIL import Image, ImageFilter

# Long edge the frames are resized to before encoding. Roughly 1.4k tokens per image at
# this size; smaller starts losing the small type recipe overlays use.
MAX_EDGE = 768
JPEG_QUALITY = 80

SHOWINFO_TIME = re.compile(r"pts_time:([\d.]+)")


@dataclass(frozen=True)
class Frame:
    """One selected still, with the moment it came from."""

    path: pathlib.Path
    timestamp: float

    def as_content_block(self) -> dict:
        data = base64.standard_b64encode(self.path.read_bytes()).decode("ascii")
        return {
            "type": "image",
            "source": {"type": "base64", "media_type": "image/jpeg", "data": data},
        }


def _difference_hash(image: Image.Image, size: int = 8) -> int:
    """Classic dHash: compare each pixel with its right-hand neighbour."""
    small = image.convert("L").resize((size + 1, size), Image.Resampling.LANCZOS)
    pixels = list(small.getdata())
    bits = 0
    for row in range(size):
        for col in range(size):
            left = pixels[row * (size + 1) + col]
            right = pixels[row * (size + 1) + col + 1]
            bits = (bits << 1) | int(left > right)
    return bits


def _detail_score(image: Image.Image) -> float:
    """
    Rough busy-ness measure. An empty chopping board scores near zero; a frame with text
    or food scores far higher. Cheap stand-in for "is anything here worth reading".
    """
    grey = image.convert("L").resize((160, 284), Image.Resampling.LANCZOS)
    edges = grey.filter(ImageFilter.FIND_EDGES)
    histogram = edges.histogram()
    total = sum(histogram) or 1
    # Share of pixels with a meaningful edge response.
    return sum(histogram[40:]) / total


def _candidate_timestamps(video: pathlib.Path, threshold: float) -> list[float]:
    """Ask ffmpeg where the picture changes, and at what times."""
    result = subprocess.run(
        ["ffmpeg", "-nostdin", "-loglevel", "info", "-i", str(video),
         "-vf", f"select='gt(scene,{threshold})',showinfo",
         "-fps_mode", "vfr", "-f", "null", "-"],
        capture_output=True, text=True, timeout=180, check=False,
    )
    return [float(m) for m in SHOWINFO_TIME.findall(result.stderr)]


def select(
    video: pathlib.Path,
    directory: pathlib.Path,
    max_frames: int = 10,
    scene_threshold: float = 0.12,
    hamming_distance: int = 8,
    min_detail: float = 0.02,
) -> list[Frame]:
    """Extract, filter, and encode the frames worth sending."""
    timestamps = _candidate_timestamps(video, scene_threshold)

    if not timestamps:
        # No detected cuts — a single continuous shot. Spread a few evenly instead.
        duration = _duration(video)
        timestamps = [duration * i / 6 for i in range(1, 6)]

    kept: list[Frame] = []
    hashes: list[int] = []

    for index, timestamp in enumerate(timestamps):
        if len(kept) >= max_frames:
            break

        raw = directory / f"raw{index:03d}.png"

        # -ss before -i seeks by keyframe, which is fast and accurate enough here.
        subprocess.run(
            ["ffmpeg", "-nostdin", "-loglevel", "error", "-ss", f"{timestamp:.2f}",
             "-i", str(video), "-frames:v", "1", str(raw)],
            capture_output=True, timeout=60, check=False,
        )

        if not raw.exists():
            continue

        with Image.open(raw) as image:
            image.load()

            if _detail_score(image) < min_detail:
                raw.unlink(missing_ok=True)
                continue

            digest = _difference_hash(image)

            if any(bin(digest ^ seen).count("1") <= hamming_distance for seen in hashes):
                # Same overlay, same shot. Already paid for.
                raw.unlink(missing_ok=True)
                continue

            hashes.append(digest)

            image.thumbnail((MAX_EDGE, MAX_EDGE), Image.Resampling.LANCZOS)
            encoded = directory / f"frame{len(kept):03d}.jpg"
            image.convert("RGB").save(encoded, "JPEG", quality=JPEG_QUALITY)

        raw.unlink(missing_ok=True)
        kept.append(Frame(path=encoded, timestamp=round(timestamp, 2)))

    return kept


def _duration(video: pathlib.Path) -> float:
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration",
         "-of", "default=nw=1:nk=1", str(video)],
        capture_output=True, text=True, timeout=60, check=False,
    )
    try:
        return float(result.stdout.strip())
    except ValueError:
        return 0.0
