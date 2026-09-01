"""Extraction sidecar.

Transcribes a cooking video and optionally structures it into a recipe. Exists as a
separate Python service because yt-dlp and Whisper cannot run on device, and because the
ASP.NET API should not hold a subprocess open for the length of a download.

Media is transient. Every request works inside a temp directory that is deleted before the
response is returned, including on failure. Nothing about a video persists here.

    uvicorn app.main:app --reload --port 8000
"""

from __future__ import annotations

import logging
import pathlib
import shutil
import time
from typing import Optional

from fastapi import FastAPI, File, Form, HTTPException, UploadFile
from pydantic import BaseModel, Field

from .classify import Verdict, classify
from .config import MAX_UPLOAD_BYTES, WHISPER_MODEL
from .modify import modify
from .frames import select as select_frames
from .media import MediaError, prepare, workspace
from .recipe import Recipe, extract, extract_with_frames
from .transcribe import Transcript, transcribe

logger = logging.getLogger("sidecar")

app = FastAPI(
    title="Recipe extraction sidecar",
    description="Transcribes cooking videos and structures them into recipes. "
                "Media is fetched transiently and deleted before the response returns.",
    version="0.1.0",
)


# ------------------------------------------------------------------- contracts

class TranscribeRequest(BaseModel):
    url: str = Field(description="A TikTok or Instagram video URL.")
    caption: Optional[str] = Field(
        default=None,
        description="Caption text already known for this post. Improves quantity accuracy, "
                    "because the creator typed it and the transcript is heard.")
    structure: bool = Field(
        default=True,
        description="Also run the recipe extractor. False returns the transcript only.")
    vision: bool = Field(
        default=True,
        description="Escalate to reading video frames when the narration is not enough. "
                    "Costs a full video download and an image-bearing model call, so set "
                    "false to stay on the cheap audio-only path.")


class SegmentOut(BaseModel):
    start: float
    end: float
    text: str


class TranscriptOut(BaseModel):
    text: str
    language: str
    language_probability: float
    duration: float
    is_speech: bool = Field(
        description="False when there is too little narration to structure — the video "
                    "most likely shows its method as on-screen text, and belongs on the "
                    "vision path instead.")
    segments: list[SegmentOut]


class ExtractionOut(BaseModel):
    source_id: Optional[str] = Field(
        description="Platform video id or shortcode — the cross-user cache key.")
    seconds_elapsed: float
    transcript: TranscriptOut
    recipe: Optional[Recipe] = None
    note: Optional[str] = None
    path: str = Field(
        default="caption",
        description="Which route produced the recipe: 'caption', 'narration', 'vision', or 'none'.")
    frames_used: int = Field(
        default=0, description="Frames sent to the model. Zero on the audio-only path.")
    caption: Optional[str] = Field(
        default=None,
        description="The post's own description, read from the fetch. On Instagram this is "
                    "the only way to get a caption for a shared link, and it is usually the "
                    "only source that carries exact amounts. Worth storing.")
    creator_handle: Optional[str] = Field(default=None)


def _to_out(transcript: Transcript) -> TranscriptOut:
    return TranscriptOut(
        text=transcript.text,
        language=transcript.language,
        language_probability=transcript.language_probability,
        duration=transcript.duration,
        is_speech=transcript.is_speech,
        segments=[SegmentOut(start=s.start, end=s.end, text=s.text) for s in transcript.segments],
    )


def _thin(recipe: Optional[Recipe]) -> bool:
    """
    Whether a structured result is too sparse to hand a cook.

    Deliberately not the caption-path completeness rule. Here the question is only whether
    escalating to frames could plausibly add anything: a recipe with no steps, or barely
    any ingredients, has room to improve. Missing amounts are not a trigger — frames do
    not carry amounts, so escalating for them would spend money to learn nothing.
    """
    if recipe is None or not recipe.is_recipe:
        return True
    return len(recipe.steps) < 2 or len(recipe.ingredients) < 3


def _process(
    media,
    directory: pathlib.Path,
    caption: Optional[str],
    structure: bool,
    vision: bool,
    started: float,
) -> ExtractionOut:
    transcript = transcribe(media.audio)

    # The caller's caption wins when it has one — it came from an export or from stage 1,
    # and is already normalised. Otherwise use what the fetch turned up, which for a
    # shared Instagram link is the only caption that exists.
    caption = caption or media.description

    recipe: Optional[Recipe] = None
    note: Optional[str] = None
    path = "none"
    frames_used = 0

    if structure and (transcript.is_speech or caption):
        # The caption alone is often the whole recipe — creators type ingredient lists with
        # exact amounts, and photo/slideshow posts in particular carry almost nothing else.
        # Gating this on narration skipped the cheapest and most accurate source there is,
        # and sent a post whose caption already held every measurement to the vision model.
        recipe = extract(transcript, caption)
        path = "narration" if transcript.is_speech else "caption"

    # Escalate when narration produced nothing, or produced too little to cook from.
    if structure and vision and _thin(recipe):
        if media.video is None:
            note = ("Narration was not enough and no video was available for the vision "
                    "path. Re-request with vision enabled.")
        else:
            frames = select_frames(media.video, directory)

            if not frames:
                note = "No usable frames could be sampled from this video."
            else:
                frames_used = len(frames)
                vision_recipe = extract_with_frames(frames, transcript, caption)

                # Only take the vision result if it actually improved on the audio pass.
                if not _thin(vision_recipe) or recipe is None:
                    recipe = vision_recipe
                    path = "vision"

                if _thin(recipe):
                    note = ("Neither the narration nor the frames carried a full recipe. "
                            "This may not be a recipe video at all.")

    if recipe is not None and not recipe.is_recipe:
        note = note or "This does not appear to be a recipe."

    return ExtractionOut(
        source_id=media.source_id,
        seconds_elapsed=round(time.time() - started, 2),
        transcript=_to_out(transcript),
        recipe=recipe,
        note=note,
        path=path,
        frames_used=frames_used,
        caption=media.description,
        creator_handle=media.uploader,
    )


class ClassifyItem(BaseModel):
    caption: Optional[str] = None
    creator_handle: Optional[str] = None


class ClassifyRequest(BaseModel):
    items: list[ClassifyItem] = Field(
        description="Captions to judge, in order. One model call covers the whole batch, "
                    "so send a hundred rather than one.")


class ClassifyResponse(BaseModel):
    verdicts: list[Verdict]
    seconds_elapsed: float


# ------------------------------------------------------------------- endpoints


class ModifyResponse(BaseModel):
    changes: list[dict]
    summary: str


@app.post("/modify", response_model=ModifyResponse)
def modify_recipe(payload: dict) -> ModifyResponse:
    """
    Picks substitutions from the caller's allowed list.

    The caller supplies the candidates and validates the result against them, so this
    endpoint cannot introduce an ingredient nobody vetted.
    """
    selection = modify(payload)

    return ModifyResponse(
        changes=[{"from": c.from_ingredient, "to": c.to} for c in selection.changes],
        summary=selection.summary,
    )


@app.post("/classify", response_model=ClassifyResponse)
def classify_batch(request: ClassifyRequest) -> ClassifyResponse:
    """Judges a batch of captions as food or not, in a single model call."""
    started = time.time()

    if not request.items:
        return ClassifyResponse(verdicts=[], seconds_elapsed=0)

    verdicts = classify([item.model_dump() for item in request.items])

    return ClassifyResponse(verdicts=verdicts, seconds_elapsed=round(time.time() - started, 2))

@app.get("/health")
def health() -> dict:
    """Reports whether the external binaries this service depends on are present."""
    return {
        "status": "ok",
        "whisper_model": WHISPER_MODEL,
        "yt_dlp": shutil.which("yt-dlp") is not None,
        "ffmpeg": shutil.which("ffmpeg") is not None,
    }


@app.post("/transcribe", response_model=ExtractionOut)
def transcribe_url(request: TranscribeRequest) -> ExtractionOut:
    """Fetches a video's audio, transcribes it, and optionally structures a recipe."""
    started = time.time()
    with workspace() as directory:
        try:
            media = prepare(directory, url=request.url, want_video=request.vision)
        except MediaError as exc:
            # The URL is the caller's input, but a fetch failure is usually the platform,
            # so this is a 422 rather than a 400: the request was well-formed.
            raise HTTPException(status_code=422, detail=str(exc)) from exc

        return _process(media, directory, request.caption, request.structure,
                        request.vision, started)


@app.post("/transcribe/upload", response_model=ExtractionOut)
async def transcribe_upload(
    file: UploadFile = File(description="A video or audio file."),
    caption: Optional[str] = Form(default=None),
    structure: bool = Form(default=True),
    vision: bool = Form(default=True),
) -> ExtractionOut:
    """Same pipeline for a file the caller already has, with no fetching involved."""
    started = time.time()

    with workspace() as directory:
        destination = directory / "upload.bin"
        written = 0

        with destination.open("wb") as sink:
            # Streamed in chunks so a large upload never lands in memory, and the size
            # ceiling is enforced while reading rather than after.
            while chunk := await file.read(1024 * 1024):
                written += len(chunk)
                if written > MAX_UPLOAD_BYTES:
                    raise HTTPException(
                        status_code=413,
                        detail=f"file exceeds {MAX_UPLOAD_BYTES // (1024 * 1024)}MB")
                sink.write(chunk)

        if written == 0:
            raise HTTPException(status_code=400, detail="empty file")

        try:
            media = prepare(directory, upload=destination, want_video=vision)
        except MediaError as exc:
            raise HTTPException(status_code=422, detail=str(exc)) from exc

        return _process(media, directory, caption, structure, vision, started)
