"""Settings, read once at import."""

from __future__ import annotations

import os
import pathlib


def _load_dotenv(path: pathlib.Path) -> None:
    """Same dependency-free loader the scripts use; shell env still wins."""
    if not path.exists():
        return
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        key, value = key.strip(), value.strip().strip('"').strip("'")
        if key and value and key not in os.environ:
            os.environ[key] = value


_load_dotenv(pathlib.Path(__file__).resolve().parents[2] / ".env")

# Whisper model size. "small" is the sweet spot for recipe audio on Apple Silicon:
# "tiny"/"base" drop ingredient quantities, "medium" is ~3x slower for little gain.
WHISPER_MODEL = os.environ.get("WHISPER_MODEL", "small")
WHISPER_DEVICE = os.environ.get("WHISPER_DEVICE", "cpu")
WHISPER_COMPUTE = os.environ.get("WHISPER_COMPUTE", "int8")

RECIPE_MODEL = os.environ.get("RECIPE_MODEL", "claude-opus-5")

# Refuse anything longer than this. A recipe short is a minute or two; a 40-minute
# upload is either a mistake or an abuse of the endpoint.
MAX_DURATION_SECONDS = int(os.environ.get("MAX_DURATION_SECONDS", "900"))
MAX_UPLOAD_BYTES = int(os.environ.get("MAX_UPLOAD_BYTES", str(200 * 1024 * 1024)))
