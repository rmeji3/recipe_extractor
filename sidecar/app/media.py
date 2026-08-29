"""Fetching and preparing media, transiently.

Nothing here persists. Every path runs inside a temporary directory that is removed on
the way out, including on failure — the legal posture in the build plan depends on media
never living on this server, so the cleanup is not best-effort.
"""

from __future__ import annotations

import contextlib
import json
import pathlib
import re
import shutil
import subprocess
import tempfile
from dataclasses import dataclass

from .config import MAX_DURATION_SECONDS

TIKTOK_ID = re.compile(r"/video/(\d+)")
INSTAGRAM_SHORTCODE = re.compile(r"instagram\.com/(?:p|reel|tv)/([^/?#]+)")


class MediaError(RuntimeError):
    """Fetch or conversion failed in a way the caller should see as a 4xx/502."""


@dataclass(frozen=True)
class Media:
    """A fetched item. `video` is present only when frames were requested."""

    audio: pathlib.Path
    duration_seconds: float
    source_id: str | None
    video: pathlib.Path | None = None


@dataclass(frozen=True)
class Audio:
    """16 kHz mono WAV — what Whisper wants, and the smallest thing that carries speech."""

    path: pathlib.Path
    duration_seconds: float
    source_id: str | None


def platform_id(url: str) -> str | None:
    """The cross-user cache key: TikTok numeric id or Instagram shortcode."""
    for pattern in (TIKTOK_ID, INSTAGRAM_SHORTCODE):
        match = pattern.search(url)
        if match:
            return match.group(1)
    return None


@contextlib.contextmanager
def workspace():
    """A temp directory that is always removed."""
    directory = pathlib.Path(tempfile.mkdtemp(prefix="recipe-sidecar-"))
    try:
        yield directory
    finally:
        shutil.rmtree(directory, ignore_errors=True)


def _run(command: list[str], timeout: int) -> subprocess.CompletedProcess:
    try:
        return subprocess.run(command, capture_output=True, text=True, timeout=timeout, check=False)
    except FileNotFoundError as exc:
        raise MediaError(f"{command[0]} is not installed on this host") from exc
    except subprocess.TimeoutExpired as exc:
        raise MediaError(f"{command[0]} timed out after {timeout}s") from exc


def probe_duration(path: pathlib.Path) -> float:
    result = _run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration",
         "-of", "json", str(path)],
        timeout=60,
    )
    if result.returncode != 0:
        raise MediaError(f"ffprobe failed: {result.stderr.strip()[:200]}")
    try:
        return float(json.loads(result.stdout)["format"]["duration"])
    except (KeyError, ValueError, json.JSONDecodeError) as exc:
        raise MediaError("could not read media duration") from exc


def download_video(url: str, directory: pathlib.Path) -> pathlib.Path:
    """
    Fetch the video itself. Only for the vision path — the transcript needs audio alone,
    and pulling a whole mp4 to throw the picture away wastes bandwidth on every request.
    """
    template = str(directory / "video.%(ext)s")
    result = _run(
        ["yt-dlp", "--no-playlist", "--no-warnings",
         "-f", "mp4/best", "--output", template, url],
        timeout=420,
    )

    if result.returncode != 0:
        message = (result.stderr or result.stdout).strip().splitlines()
        raise MediaError(f"could not fetch media: {message[-1][:300] if message else 'unknown error'}")

    for candidate in sorted(directory.glob("video.*")):
        return candidate

    raise MediaError("fetch produced no video file")


def extract_audio(source: pathlib.Path, directory: pathlib.Path) -> pathlib.Path:
    """Pull the 16 kHz mono track Whisper wants out of an already-downloaded file."""
    return transcode_upload(source, directory)


def download_audio(url: str, directory: pathlib.Path) -> pathlib.Path:
    """Fetch audio only. Never the full video — it is not needed for a transcript."""
    template = str(directory / "source.%(ext)s")
    result = _run(
        [
            "yt-dlp",
            "--no-playlist",
            "--no-warnings",
            "--extract-audio",
            "--audio-format", "wav",
            # Whisper wants 16 kHz mono; asking yt-dlp for it avoids a second pass.
            "--postprocessor-args", "ffmpeg:-ac 1 -ar 16000",
            "--output", template,
            url,
        ],
        timeout=300,
    )

    if result.returncode != 0:
        message = (result.stderr or result.stdout).strip().splitlines()
        detail = message[-1][:300] if message else "unknown error"
        raise MediaError(f"could not fetch media: {detail}")

    for candidate in sorted(directory.glob("source.*")):
        if candidate.suffix.lower() == ".wav":
            return candidate

    raise MediaError("fetch produced no audio track")


def transcode_upload(source: pathlib.Path, directory: pathlib.Path) -> pathlib.Path:
    """Strip an uploaded video down to the audio Whisper needs."""
    output = directory / "upload.wav"
    result = _run(
        ["ffmpeg", "-nostdin", "-y", "-i", str(source),
         "-vn", "-ac", "1", "-ar", "16000", str(output)],
        timeout=300,
    )
    if result.returncode != 0 or not output.exists():
        raise MediaError(f"ffmpeg could not read that file: {result.stderr.strip()[-200:]}")
    return output


def prepare(directory: pathlib.Path, *, url: str | None = None,
            upload: pathlib.Path | None = None, want_video: bool = False) -> Media:
    """
    Produce a Media for either input shape, enforcing the duration ceiling.

    `want_video` keeps the picture as well, for the vision path. It costs a full download
    instead of an audio-only one, so ask for it only when it will be used.
    """
    video: pathlib.Path | None = None

    if url:
        source_id = platform_id(url)
        if want_video:
            video = download_video(url, directory)
            audio = extract_audio(video, directory)
        else:
            audio = download_audio(url, directory)
    elif upload:
        source_id = None
        audio = transcode_upload(upload, directory)
        video = upload if want_video else None
    else:
        raise MediaError("provide either a url or a file")

    duration = probe_duration(audio)
    if duration > MAX_DURATION_SECONDS:
        raise MediaError(
            f"media is {duration / 60:.1f} minutes; the limit is "
            f"{MAX_DURATION_SECONDS / 60:.0f}"
        )

    return Media(audio=audio, duration_seconds=duration, source_id=source_id, video=video)
