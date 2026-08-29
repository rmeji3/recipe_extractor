#!/usr/bin/env python3
"""Step 2 of the build plan: classification + caption extraction, as a CLI.

Reads the stage-1 cache produced by tiktok_stage1.py and runs the two model passes the
plan calls for, so their output can be read directly before any UI is built on top:

  classify   one batched call per ~100 captions -> food / not-food + confidence
  extract    per-caption structured recipe, with the completeness check that decides
             whether a video would need to escalate to the vision model

Nothing here writes to the database. It exists to answer one question: does caption-only
parsing produce usable recipes?

    python3 scripts/extract_recipes.py classify
    python3 scripts/extract_recipes.py extract --limit 20
    python3 scripts/extract_recipes.py report

Reads ANTHROPIC_API_KEY from a .env file in the repo root (see .env.example), from the
environment, or from an `ant auth login` profile.
"""

from __future__ import annotations

import argparse
import json
import os
import pathlib
import sys
import textwrap
from typing import Optional

try:
    import anthropic
    from pydantic import BaseModel, Field
except ImportError:
    print("missing deps. run:  pip install anthropic pydantic", file=sys.stderr)
    raise SystemExit(1)

def load_dotenv(path: pathlib.Path = pathlib.Path(".env")) -> None:
    """Read KEY=VALUE lines from .env into the environment.

    Deliberately dependency-free and non-clobbering: a variable already exported in the
    shell wins, so `ANTHROPIC_API_KEY=... python3 script.py` still overrides the file.
    """
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


load_dotenv()

DEFAULT_MODEL = os.environ.get("RECIPE_MODEL", "claude-opus-5")
STAGE1 = pathlib.Path("data/tiktok_stage1.jsonl")
CLASSIFIED = pathlib.Path("data/classified.jsonl")
EXTRACTED = pathlib.Path("data/extracted.jsonl")


# --------------------------------------------------------------------- schemas

class ClassifiedItem(BaseModel):
    index: int = Field(description="The line number given in the input list.")
    is_food: bool = Field(description="True only if this is a cooking or recipe video.")
    confidence: float = Field(description="0.0 to 1.0.")


class ClassifiedBatch(BaseModel):
    items: list[ClassifiedItem]


class Ingredient(BaseModel):
    quantity: Optional[float] = Field(description="Numeric amount, null if not stated.")
    unit: Optional[str] = Field(description="tbsp, g, cup, etc. Null if not stated.")
    item: str = Field(description="The ingredient itself, e.g. 'soy sauce'.")
    prep_note: Optional[str] = Field(description="e.g. 'low sodium', 'finely diced'.")
    confidence: float = Field(description="0.0 to 1.0 that this is right.")


class Step(BaseModel):
    text: str = Field(description="One instruction, rewritten in neutral voice.")


class ExtractedRecipe(BaseModel):
    is_recipe: bool = Field(description="False if the caption is not a recipe at all.")
    title: str
    servings: Optional[int] = None
    prep_minutes: Optional[int] = None
    cook_minutes: Optional[int] = None
    ingredients: list[Ingredient] = []
    steps: list[Step] = []
    equipment: list[str] = []
    food_confidence: float = 0.0


# --------------------------------------------------------------------- prompts

CLASSIFY_SYSTEM = """\
You sort saved short-form video captions into cooking content and everything else.

A caption is food if the video teaches, demonstrates, or documents preparing something \
edible: a recipe, a technique, a meal-prep routine, a bake. Restaurant reviews, food \
challenges, mukbang, and "what I eat in a day" without preparation are NOT food for this \
purpose — the goal is a cookbook, not a food feed.

Be strict. A cookbook full of clips that are not recipes destroys trust in the whole \
import, and anything you reject stays visible to the user in a skipped list, so a miss is \
recoverable and a false positive is not. When a caption is only hashtags with no dish and \
no method, that is not enough — mark it not food with low confidence.

Return one entry per input line, using the index given."""

EXTRACT_SYSTEM = """\
You turn a short-form video caption into a structured recipe.

Rules:
- Use ONLY what the caption states. Never invent a quantity, a time, or a step that is \
not there. A missing amount must be null — a plausible guess is worse than a gap, because \
the user will cook from this.
- Rewrite every step in plain, neutral instructional voice. Do not copy the creator's \
phrasing, jokes, or asides. State the action.
- Ingredients: split quantity, unit, and item. "2 tbsp low sodium soy sauce" becomes \
quantity 2, unit "tbsp", item "soy sauce", prep_note "low sodium".
- Set confidence per ingredient. Low confidence on anything you inferred rather than read.
- If the caption is not a recipe at all, set is_recipe false and leave the lists empty."""


# ----------------------------------------------------------------------- utils

def load_jsonl(path: pathlib.Path) -> list[dict]:
    if not path.exists():
        return []
    rows = []
    with path.open(encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if line:
                try:
                    rows.append(json.loads(line))
                except json.JSONDecodeError:
                    continue
    return rows


def resolved_captions() -> list[dict]:
    rows = [r for r in load_jsonl(STAGE1) if r.get("ok") and r.get("caption", "").strip()]
    if not rows:
        print(f"no resolved captions in {STAGE1}. run scripts/tiktok_stage1.py first.",
              file=sys.stderr)
        raise SystemExit(1)
    return rows


TIERS = ("complete", "ingredients_only", "escalate")


def completeness(recipe: dict | ExtractedRecipe) -> tuple[str, list[str]]:
    """Grade a caption extraction into one of three tiers.

    The plan's original rule escalated on any of: <3 ingredients, <half quantified, or
    <2 steps. Measured against real captions that sent most recipes to the vision model
    for the wrong reason — the common TikTok pattern is a full, fully-quantified
    ingredient list with the method shown on screen only, so "no steps" fired on recipes
    that were already 80% extracted.

    So a caption with a usable ingredient list but no method is its own tier. It is
    immediately searchable and works as a shopping list, and the video call can wait until
    someone actually cooks it — which, for most saved recipes, is never. Only captions
    with too little to stand on their own escalate up front.
    """
    if isinstance(recipe, ExtractedRecipe):
        ingredients = [i.model_dump() for i in recipe.ingredients]
        steps = [s.model_dump() for s in recipe.steps]
    else:
        ingredients = recipe.get("ingredients", [])
        steps = recipe.get("steps", [])

    gaps = []
    quantified = sum(1 for i in ingredients if i.get("quantity") is not None)

    if len(ingredients) < 3:
        gaps.append("fewer than 3 ingredients")
    elif quantified < len(ingredients) / 2:
        gaps.append("under half of ingredients have a quantity")
    if len(steps) < 2:
        gaps.append("fewer than 2 steps")

    if not gaps:
        return "complete", []

    # A solid, mostly-quantified ingredient list is worth keeping even with no method.
    if len(ingredients) >= 3 and quantified >= len(ingredients) / 2:
        return "ingredients_only", gaps

    return "escalate", gaps


def fmt(value, suffix: str = "") -> str:
    """Render an optional number without printing the word 'None'."""
    return f"{value}{suffix}" if value is not None else "—"


# -------------------------------------------------------------------- commands

def cmd_classify(args, client) -> int:
    rows = resolved_captions()
    done = {r["video_id"] for r in load_jsonl(CLASSIFIED)}
    pending = [r for r in rows if r["video_id"] not in done]
    if args.limit:
        pending = pending[: args.limit]

    print(f"{len(rows)} captions; {len(done)} classified; batching {len(pending)}")
    if not pending:
        return 0

    batches = [pending[i:i + args.batch_size] for i in range(0, len(pending), args.batch_size)]
    print(f"{len(batches)} call(s) of up to {args.batch_size} captions\n")

    with CLASSIFIED.open("a", encoding="utf-8") as sink:
        for number, batch in enumerate(batches, start=1):
            listing = "\n".join(
                f"{i}. [@{r.get('creator_handle')}] {' '.join(r['caption'].split())[:300]}"
                for i, r in enumerate(batch)
            )
            response = client.messages.parse(
                model=args.model,
                max_tokens=16000,
                system=CLASSIFY_SYSTEM,
                messages=[{"role": "user", "content": f"Classify these {len(batch)} captions:\n\n{listing}"}],
                output_format=ClassifiedBatch,
            )
            by_index = {item.index: item for item in response.parsed_output.items}

            hits = 0
            for i, row in enumerate(batch):
                verdict = by_index.get(i)
                out = {
                    "video_id": row["video_id"],
                    "creator_handle": row.get("creator_handle"),
                    "caption": row["caption"],
                    "is_food": bool(verdict and verdict.is_food),
                    "confidence": round(verdict.confidence, 2) if verdict else 0.0,
                }
                hits += out["is_food"]
                sink.write(json.dumps(out, ensure_ascii=False) + "\n")
            sink.flush()

            usage = response.usage
            print(f"  batch {number}/{len(batches)}: {hits}/{len(batch)} food  "
                  f"({usage.input_tokens} in / {usage.output_tokens} out)")

    return 0


def cmd_extract(args, client) -> int:
    classified = [r for r in load_jsonl(CLASSIFIED) if r.get("is_food")]
    if not classified:
        print("nothing classified as food yet. run `classify` first.", file=sys.stderr)
        return 1

    classified.sort(key=lambda r: r.get("confidence", 0), reverse=True)
    done = {r["video_id"] for r in load_jsonl(EXTRACTED)}
    pending = [r for r in classified if r["video_id"] not in done]
    if args.limit:
        pending = pending[: args.limit]

    print(f"{len(classified)} food captions; {len(done)} extracted; extracting {len(pending)}\n")

    with EXTRACTED.open("a", encoding="utf-8") as sink:
        for number, row in enumerate(pending, start=1):
            try:
                response = client.messages.parse(
                    model=args.model,
                    max_tokens=16000,
                    system=EXTRACT_SYSTEM,
                    messages=[{"role": "user", "content": f"Caption:\n\n{row['caption']}"}],
                    output_format=ExtractedRecipe,
                )
            except anthropic.APIStatusError as exc:
                # One bad caption must not end the run. Record it and move on — the cache
                # makes a re-run cheap, and the failure is visible in the report.
                print(f"  {number}/{len(pending)} {'API ERROR':>16}  "
                      f"{exc.status_code} {row['video_id']}")
                sink.write(json.dumps({
                    "video_id": row["video_id"],
                    "creator_handle": row.get("creator_handle"),
                    "error": f"{type(exc).__name__}: {exc.status_code}",
                    "recipe": {"title": "", "ingredients": [], "steps": []},
                    "tier": "error",
                    "gaps": [],
                }, ensure_ascii=False) + "\n")
                sink.flush()
                continue
            except anthropic.APIConnectionError as exc:
                print(f"  {number}/{len(pending)} {'CONNECTION':>16}  {exc}")
                continue

            recipe = response.parsed_output
            tier, gaps = completeness(recipe)

            out = {
                "video_id": row["video_id"],
                "creator_handle": row.get("creator_handle"),
                "recipe": recipe.model_dump(),
                "tier": tier,
                "gaps": gaps,
                # Kept for older readers of this file.
                "escalate": tier == "escalate",
                "escalate_reasons": gaps,
            }
            sink.write(json.dumps(out, ensure_ascii=False) + "\n")
            sink.flush()

            print(f"  {number}/{len(pending)} {tier:>16}  "
                  f"{len(recipe.ingredients):>2}ing {len(recipe.steps)}step  {recipe.title[:52]}")

    return 0


def cmd_report(args, _client) -> int:
    classified = load_jsonl(CLASSIFIED)
    extracted = load_jsonl(EXTRACTED)

    print(f"\n{'=' * 68}")
    if classified:
        food = [r for r in classified if r.get("is_food")]
        print(f"  classified   {len(classified)}")
        print(f"  food         {len(food)}  ({len(food) / len(classified) * 100:.0f}%)")
        high = [r for r in food if r.get("confidence", 0) >= 0.8]
        print(f"    conf >=0.8 {len(high)}  auto-import tier")

        from collections import Counter
        creators = Counter(r["creator_handle"] for r in food if r.get("creator_handle"))
        repeats = [(h, c) for h, c in creators.most_common() if c > 1]
        print(f"  food creators: {len(creators)} distinct, {len(repeats)} with >1 hit")
        if repeats:
            print(f"    {repeats[:6]}")

    if extracted:
        from collections import Counter

        # Re-graded on read, so rows written under an older rule are re-tiered for free.
        errors = [r for r in extracted if r.get("tier") == "error"]
        extracted = [r for r in extracted if r.get("tier") != "error"]
        for row in extracted:
            row["tier"], row["gaps"] = completeness(row["recipe"])
        if errors:
            print(f"\n  {len(errors)} caption(s) failed at the API and were skipped")

        tiers = Counter(r["tier"] for r in extracted)
        total = len(extracted)
        print(f"\n  extracted    {total}")
        labels = {
            "complete": "ingredients + method, ready to cook",
            "ingredients_only": "usable list, method is on-screen only",
            "escalate": "too thin — needs the vision model",
        }
        for tier in TIERS:
            count = tiers.get(tier, 0)
            print(f"    {tier:<17}{count:>4}  ({count / total * 100:>3.0f}%)  {labels[tier]}")

        quantified = sum(1 for r in extracted for i in r["recipe"]["ingredients"]
                         if i.get("quantity") is not None)
        ingredients = sum(len(r["recipe"]["ingredients"]) for r in extracted)
        if ingredients:
            print(f"\n  {quantified}/{ingredients} ingredients carry a quantity "
                  f"({quantified / ingredients * 100:.0f}%)")

        gaps = Counter(g for r in extracted for g in r["gaps"])
        for gap, count in gaps.most_common():
            print(f"    {count:>3}  {gap}")

        print("\n  --- sample extractions ---")
        for row in extracted[: args.limit or 3]:
            recipe = row["recipe"]
            print(f"\n  {recipe['title']}  (@{row.get('creator_handle')})  [{row['tier']}]")
            print(f"    serves {fmt(recipe.get('servings'))}  "
                  f"prep {fmt(recipe.get('prep_minutes'), 'm')}  "
                  f"cook {fmt(recipe.get('cook_minutes'), 'm')}")
            for ing in recipe["ingredients"][:6]:
                amount = f"{fmt(ing['quantity'])} {ing['unit'] or ''}".strip()
                print(f"      {amount:>12}  {ing['item']}  (conf {ing['confidence']})")
            for i, step in enumerate(recipe["steps"][:3], start=1):
                print(f"      {i}. {textwrap.shorten(step['text'], 82)}")
    print(f"{'=' * 68}\n")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("command", choices=["classify", "extract", "report"])
    parser.add_argument("--limit", type=int, help="cap how many items this run handles")
    parser.add_argument("--batch-size", type=int, default=100,
                        help="captions per classification call (default 100)")
    parser.add_argument("--model", default=DEFAULT_MODEL)
    args = parser.parse_args()

    client = None
    if args.command != "report":
        if not (os.environ.get("ANTHROPIC_API_KEY") or os.environ.get("ANTHROPIC_AUTH_TOKEN")):
            print("No ANTHROPIC_API_KEY found. Put it in .env in the repo root:\n"
                  "    ANTHROPIC_API_KEY=sk-ant-...\n"
                  "(copy .env.example if you have not already)", file=sys.stderr)
            return 1
        client = anthropic.Anthropic()

    return {"classify": cmd_classify, "extract": cmd_extract, "report": cmd_report}[args.command](args, client)


if __name__ == "__main__":
    raise SystemExit(main())
