"""Choosing substitutions from a fixed list.

This is deliberately not "ask the model how to make a recipe vegetarian". It is handed the
only swaps that are allowed — drawn from a curated table, already filtered by the goal and
the user's profile — and asked to pick among them. Anything it returns that is not on the
list is discarded by the caller before a user sees it.

That constraint is the whole feature. A wrong substitution ruins dinner, which is a far
worse failure than a wrong search result, and a language model asked to invent one will
produce something plausible at full confidence.
"""

from __future__ import annotations

import functools

import anthropic
from pydantic import BaseModel, Field

from .config import RECIPE_MODEL

SYSTEM = """\
You adapt a recipe by choosing from substitutions that have already been vetted.

You are given the recipe's ingredients and, for each one that can be changed, a list of \
allowed replacements. Each option says what it does to the dish and whether this cook \
already uses it.

Rules:
- **Choose only from the options given.** You may not suggest an ingredient that is not \
listed, however obvious it seems. Anything else is discarded, so inventing one wastes the \
change rather than making it.
- **Change only what the goal requires.** A request to make something vegetarian is not a \
licence to also swap the rice and the oil. Every unnecessary change is one more way the \
dish differs from the one they wanted to cook.
- **Prefer options marked `already_cooks_with`.** That cook has bought it, used it, and \
liked the result — it is a better suggestion than something they have never tried.
- Some ingredients need no change. Leaving an ingredient alone is a valid answer, and \
leaving *everything* alone is valid when the recipe already meets the goal.
- Write a short summary of what the dish becomes: how it will differ to eat, and anything \
they should watch for. Two or three sentences, plain and specific. Do not restate the list \
of changes — they can see it.
- Never claim a nutritional number you were not given. "Lower in fat" is fair; "saves 200 \
calories" is not."""


class Change(BaseModel):
    from_ingredient: str = Field(
        alias="from", description="The ingredient being replaced, exactly as given.")
    to: str = Field(description="The replacement, copied exactly from that ingredient's options.")

    model_config = {"populate_by_name": True}


class Selection(BaseModel):
    changes: list[Change] = Field(default_factory=list)
    summary: str = Field(description="Two or three sentences on what the dish becomes.")


@functools.lru_cache(maxsize=1)
def _client() -> anthropic.Anthropic:
    return anthropic.Anthropic()


def _render(payload: dict) -> str:
    lines = [f"Recipe: {payload.get('title')}", f"Goal: {payload.get('goal')}"]

    if payload.get("user_notes"):
        lines.append(f"About this cook: {payload['user_notes']}")

    lines.append("\nIngredients that can be changed:\n")

    for candidate in payload.get("candidates", []):
        amount = " ".join(
            str(part) for part in (candidate.get("quantity"), candidate.get("unit")) if part
        )
        lines.append(f"- {candidate['ingredient']}{f' ({amount})' if amount else ''}")

        for option in candidate.get("options", []):
            mark = "  [already cooks with this]" if option.get("already_cooks_with") else ""
            note = f" {option['note']}" if option.get("note") else ""
            lines.append(f"    * {option['replacement']} — {option['effect']}{note}{mark}")

    return "\n".join(lines)


def modify(payload: dict, model: str | None = None) -> Selection:
    if not payload.get("candidates"):
        return Selection(changes=[], summary="")

    response = _client().messages.parse(
        model=model or RECIPE_MODEL,
        max_tokens=16000,
        system=SYSTEM,
        messages=[{"role": "user", "content": _render(payload)}],
        output_format=Selection,
    )

    return response.parsed_output
