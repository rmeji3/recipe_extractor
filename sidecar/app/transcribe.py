"""Speech-to-text with faster-whisper.

Runs locally rather than through a hosted ASR API: recipe shorts are a minute or two, the
volume is high, and per-minute pricing on a backlog import adds up for a step that a
laptop-class CPU handles fine. No extra provider, no per-video cost, and the audio never
leaves this host.

Segment timestamps are kept. The build plan wants `source_ts` captured from day one — it
is free here and it is what later lets a user tap a step and jump to the moment it was
said.
"""

from __future__ import annotations

import functools
import pathlib
from dataclasses import dataclass, field

from .config import WHISPER_COMPUTE, WHISPER_DEVICE, WHISPER_MODEL


@dataclass
class Segment:
    start: float
    end: float
    text: str


@dataclass
class Transcript:
    text: str
    language: str
    language_probability: float
    duration: float
    segments: list[Segment] = field(default_factory=list)

    @property
    def is_speech(self) -> bool:
        """Whether there is enough spoken content to be worth structuring.

        A silent recipe video — method shown as on-screen text over music — transcribes to
        a handful of stray words. That is the signal to fall back to the vision path
        rather than hand noise to the extractor.
        """
        return len(self.text.split()) >= 20


@functools.lru_cache(maxsize=1)
def _model():
    """Loaded once per process; the first call pays the model download."""
    from faster_whisper import WhisperModel

    return WhisperModel(WHISPER_MODEL, device=WHISPER_DEVICE, compute_type=WHISPER_COMPUTE)


def transcribe(path: pathlib.Path) -> Transcript:
    segments, info = _model().transcribe(
        str(path),
        # Skips silence and background music instead of hallucinating lyrics over it,
        # which is the common failure mode on short-form video.
        vad_filter=True,
        beam_size=5,
        condition_on_previous_text=False,
    )

    collected = [
        Segment(start=round(s.start, 2), end=round(s.end, 2), text=s.text.strip())
        for s in segments
        if s.text.strip()
    ]

    return Transcript(
        text=" ".join(s.text for s in collected).strip(),
        language=info.language,
        language_probability=round(info.language_probability, 3),
        duration=round(info.duration, 2),
        segments=collected,
    )
