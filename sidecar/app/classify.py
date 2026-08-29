"""Batched food/not-food classification.

One model call decides a hundred captions. Classifying them one at a time is the expensive
version of the same answer and there is no reason to do it: a whole library is two or three
calls and a few cents.

The prompt is deliberately strict. A cookbook full of clips that are not recipes destroys
trust in the entire import, while anything wrongly rejected stays visible to the user in a
skipped list — so a miss is recoverable and a false positive is not.
"""

from __future__ import annotations

import functools

import anthropic
from pydantic import BaseModel, Field

from .config import RECIPE_MODEL

SYSTEM = """\
You sort saved short-form video captions into cooking content and everything else.

A caption is food if the video teaches, demonstrates, or documents preparing something \
edible: a recipe, a technique, a meal-prep routine, a bake. Restaurant reviews, food \
challenges, mukbang, and "what I eat in a day" without preparation are NOT food for this \
purpose — the goal is a cookbook, not a food feed.

Be strict. A cookbook full of clips that are not recipes destroys trust in the whole \
import, and anything you reject stays visible to the user in a skipped list, so a miss is \
recoverable and a false positive is not. When a caption is only hashtags with no dish and \
no method, that is not enough — mark it not food with low confidence.

Return exactly one entry per input line, using the index given."""


class Verdict(BaseModel):
    index: int = Field(description="The line number given in the input list.")
    is_food: bool = Field(description="True only if this is a cooking or recipe video.")
    confidence: float = Field(description="0.0 to 1.0.")


class Verdicts(BaseModel):
    items: list[Verdict]


@functools.lru_cache(maxsize=1)
def _client() -> anthropic.Anthropic:
    return anthropic.Anthropic()


def classify(items: list[dict], model: str | None = None) -> list[Verdict]:
    """
    Judge a batch. Each item is {"caption": str, "creator_handle": str | None}.

    Returns one verdict per input index. Anything the model omits is filled in as not-food
    at zero confidence rather than dropped, so the caller always gets a complete answer and
    never silently loses a post.
    """
    if not items:
        return []

    listing = "\n".join(
        f"{i}. [@{item.get('creator_handle') or 'unknown'}] "
        f"{' '.join((item.get('caption') or '').split())[:300]}"
        for i, item in enumerate(items)
    )

    response = _client().messages.parse(
        model=model or RECIPE_MODEL,
        max_tokens=16000,
        system=SYSTEM,
        messages=[{"role": "user", "content": f"Classify these {len(items)} captions:\n\n{listing}"}],
        output_format=Verdicts,
    )

    by_index = {v.index: v for v in response.parsed_output.items}

    return [
        by_index.get(i, Verdict(index=i, is_food=False, confidence=0.0))
        for i in range(len(items))
    ]
