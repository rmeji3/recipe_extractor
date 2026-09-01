"""Turn a transcript (and any caption text) into a structured recipe.

Same schema and the same rules as scripts/extract_recipes.py, with one addition: a
transcript carries timestamps, so every step can record when it was said.
"""

from __future__ import annotations

import functools
from typing import Optional

import anthropic
from pydantic import BaseModel, Field

from .config import RECIPE_MODEL
from .frames import Frame
from .transcribe import Transcript


class Ingredient(BaseModel):
    group: Optional[str] = Field(
        default=None,
        description="The part of the dish this belongs to, e.g. 'Chicken', 'Sauce', "
                    "'Marinade'. Null when the recipe has no sections.")
    quantity: Optional[float] = Field(default=None, description="Numeric amount, null if not stated.")
    unit: Optional[str] = Field(default=None, description="tbsp, g, cup, etc. Null if not stated.")
    item: str = Field(description="The ingredient itself, e.g. 'soy sauce'.")
    prep_note: Optional[str] = Field(default=None, description="e.g. 'low sodium', 'finely diced'.")
    confidence: float = Field(default=0.0, description="0.0 to 1.0 that this is right.")
    source_ts: Optional[float] = Field(
        default=None, description="Seconds into the video where this was said, if known.")


class Step(BaseModel):
    text: str = Field(description="One instruction, in neutral voice.")
    ts_start: Optional[float] = Field(default=None, description="Seconds where this step begins.")
    ts_end: Optional[float] = Field(default=None, description="Seconds where this step ends.")


class Recipe(BaseModel):
    is_recipe: bool = Field(description="False if this is not a recipe at all.")
    title: str
    servings: Optional[int] = None
    prep_minutes: Optional[int] = None
    cook_minutes: Optional[int] = None
    ingredients: list[Ingredient] = []
    steps: list[Step] = []
    equipment: list[str] = []
    food_confidence: float = 0.0


SYSTEM = """\
You turn a short-form cooking video into a structured recipe. You are given a timestamped \
transcript of the narration, and sometimes the caption the creator posted with it.

Rules:
- Use ONLY what the transcript and caption state. Never invent a quantity, a time, or a \
step. A missing amount must be null — a plausible guess is worse than a gap, because the \
user will cook from this.
- Rewrite every step in plain, neutral instructional voice. Do not copy the creator's \
phrasing, jokes, sponsor reads, or calls to follow. State the action.
- Ingredients: split quantity, unit, and item. "two tablespoons of low sodium soy sauce" \
becomes quantity 2, unit "tbsp", item "soy sauce", prep_note "low sodium".
- **Keep the recipe's own sections.** Creators write "For the Chicken:", "For the Sauce:", \
"Marinade:" — set each ingredient's group from the heading it sits under, using a short \
name ("Chicken", "Sauce", "Marinade"). Flattening them into one list makes a recipe with \
a separate sauce much harder to cook from. Use null only when the source really is one \
undivided list; never invent sections that are not there.
- Transcripts are imperfect. When a word is clearly a mishearing of a cooking term, use \
the cooking term, but lower that ingredient's confidence. When you genuinely cannot tell, \
leave it out rather than guessing.
- Record timestamps. Each step takes ts_start and ts_end from the transcript lines it came \
from; an ingredient takes source_ts from where it was first mentioned. These let the app \
send a user straight to the moment, so approximate-but-present beats null.
- The caption is usually the more reliable source for exact amounts, because the creator \
typed it. Prefer it over the transcript when the two disagree, and say so via confidence.
- If this is not a recipe at all, set is_recipe false and leave the lists empty."""


VISION_SYSTEM = """\
You turn a short-form cooking video into a structured recipe. You are given still frames \
sampled from the video in order, each labelled with the second it came from, plus any \
narration transcript and the caption the creator posted.

The frames are there because the caption and narration were not enough on their own. Read \
what is written on screen and watch what is being done.

Rules:
- Use ONLY what you can read or see. Never invent a quantity, a time, or a step.
- **Amounts are almost never on screen in these videos.** Creators write hooks, section \
labels, and calorie cards, not measured ingredient lists. If you cannot read an amount, \
set it to null. Do not estimate one from how the food looks — a wrong quantity ruins \
dinner, and a null is honest.
- Take amounts from the caption when it has them; that is typed and reliable. Frames and \
narration are for identifying ingredients and recovering the method.
- **Keep the recipe's own sections.** Creators write "For the Chicken:", "For the Sauce:", \
"Marinade:" — set each ingredient's group from the heading it sits under, using a short \
name ("Chicken", "Sauce", "Marinade"). Flattening them into one list makes a recipe with \
a separate sauce much harder to cook from. Use null only when the source really is one \
undivided list; never invent sections that are not there.
- Rewrite every step in plain, neutral instructional voice. Never copy the creator's \
phrasing, jokes, or sponsor reads.
- Ignore on-screen text that is not part of the recipe: hooks like "pov: you learned how \
to...", follow prompts, episode numbers, watermarks, and comment overlays.
- Timestamps must be copied from the frame labels above, never estimated. Steps must run \
forward in time. If you cannot tell which frame a step came from, leave ts_start and \
ts_end null — a wrong timestamp sends the user to the wrong moment, which is worse than \
having none.
- Confidence matters here. An ingredient read from on-screen text scores high; one \
inferred from watching a pan scores low. Say so honestly in the number.
- If this is not a recipe at all — a tips video, a restaurant review, a haul — set \
is_recipe false and leave the lists empty. That is a correct answer, not a failure."""


@functools.lru_cache(maxsize=1)
def _client() -> anthropic.Anthropic:
    return anthropic.Anthropic()


def build_prompt(transcript: Transcript, caption: str | None) -> str:
    parts = []

    if caption:
        parts.append(f"Caption the creator posted:\n\n{caption}")

    lines = [f"[{s.start:.1f}-{s.end:.1f}] {s.text}" for s in transcript.segments]

    if lines:
        parts.append("Timestamped transcript:\n\n" + "\n".join(lines))
    elif transcript.text.strip():
        parts.append(f"Transcript:\n\n{transcript.text}")
    else:
        # Silent, or music only. Say so rather than presenting an empty transcript, which
        # reads as a failed transcription instead of a video that never spoke.
        parts.append("There is no narration — this video is silent or music-only.")

    return "\n\n---\n\n".join(parts)


def extract(transcript: Transcript, caption: str | None = None,
            model: str | None = None) -> Recipe:
    response = _client().messages.parse(
        model=model or RECIPE_MODEL,
        max_tokens=16000,
        system=SYSTEM,
        messages=[{"role": "user", "content": build_prompt(transcript, caption)}],
        output_format=Recipe,
    )
    return response.parsed_output


def extract_with_frames(
    frames: list[Frame],
    transcript: Transcript | None = None,
    caption: str | None = None,
    model: str | None = None,
) -> Recipe:
    """
    The vision path. Sends the sampled frames alongside whatever text exists.

    Each frame is preceded by a label giving its timestamp, so the model can attribute
    steps to moments rather than guessing an order.
    """
    content: list[dict] = []

    if caption:
        content.append({"type": "text", "text": f"Caption the creator posted:\n\n{caption}"})

    if transcript and transcript.text.strip():
        lines = "\n".join(f"[{s.start:.1f}-{s.end:.1f}] {s.text}" for s in transcript.segments)
        content.append({"type": "text", "text": f"Narration transcript:\n\n{lines}"})
    else:
        content.append({"type": "text",
                        "text": "There is no narration — this video is silent or music-only."})

    content.append({"type": "text", "text": f"{len(frames)} frames sampled from the video:"})

    for frame in frames:
        content.append({"type": "text", "text": f"Frame at {frame.timestamp:.1f}s:"})
        content.append(frame.as_content_block())

    response = _client().messages.parse(
        model=model or RECIPE_MODEL,
        max_tokens=16000,
        system=VISION_SYSTEM,
        messages=[{"role": "user", "content": content}],
        output_format=Recipe,
    )
    return _sanitise_timestamps(response.parsed_output, frames)


def _sanitise_timestamps(recipe: Recipe, frames: list[Frame]) -> Recipe:
    """
    Drop frame-derived timestamps that cannot be right.

    Measured: on the vision path the model reliably reconstructs the *method* but not
    *when* each step happened — it returns small, non-monotonic numbers that do not match
    the frame labels it was given. A wrong timestamp is worse than none, because tapping a
    step would jump the user to the wrong moment. Narration timestamps come from Whisper
    and are trustworthy; these do not, so they are only kept when they survive two checks:
    every value lands within the sampled range, and the steps run forward in time.
    """
    if not frames or not recipe.steps:
        return recipe

    latest = max(frame.timestamp for frame in frames)
    starts = [step.ts_start for step in recipe.steps]

    in_range = all(t is None or 0 <= t <= latest + 1 for t in starts)
    known = [t for t in starts if t is not None]
    monotonic = all(a <= b for a, b in zip(known, known[1:]))

    if in_range and monotonic:
        return recipe

    for step in recipe.steps:
        step.ts_start = None
        step.ts_end = None
    for ingredient in recipe.ingredients:
        ingredient.source_ts = None

    return recipe
