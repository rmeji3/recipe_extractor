#!/usr/bin/env python3
"""Stage 1 for the TikTok path: fetch light metadata for saved videos via oEmbed.

The TikTok export carries nothing but a date and a link, so every downstream step —
classification, extraction, creator clustering — needs this pass first. oEmbed is
unauthenticated and intended for public embedding, which is a far better posture than
scraping, and it returns the full untruncated caption.

Resumable: results are appended to a JSONL cache keyed by video id, and a re-run skips
anything already cached. Kill it and start it again freely.

    python3 scripts/tiktok_stage1.py data/user_data_tiktok.json
    python3 scripts/tiktok_stage1.py data/user_data_tiktok.json --limit 50
    python3 scripts/tiktok_stage1.py --report-only
"""

from __future__ import annotations

import argparse
import json
import pathlib
import random
import re
import sys
import time
import urllib.error
import urllib.request
from collections import Counter

# The share URL in the export is rejected by oEmbed; the canonical form resolves from the
# numeric id alone, with no creator handle needed.
OEMBED = "https://www.tiktok.com/oembed?url=https://www.tiktok.com/video/{video_id}"
VIDEO_ID = re.compile(r"/video/(\d+)")

# Free first pass. Deliberately generous: it is a ranking hint feeding a ranked queue, not
# a filter. Known false positives on this vocabulary include "season" (gaming) and
# "cookie" (cartoons) — the ambiguous middle is what the batched model call is for.
FOOD_TERMS = re.compile(
    r"\b(recipe|recipes|cook|cooking|cooked|bake|baking|baked|grill|grilled|roast|roasted|"
    r"fry|fried|air ?fryer|marinate|marinade|simmer|saute|sauté|whisk|knead|"
    r"ingredient|ingredients|tbsp|tsp|teaspoon|tablespoon|cups?|grams?|oz|ml|"
    r"dinner|lunch|breakfast|brunch|dessert|snack|meal|meals|mealprep|meal prep|dish|"
    r"chicken|beef|pork|steak|salmon|shrimp|pasta|noodle|rice|bread|dough|cake|cookie|"
    r"brownie|sauce|soup|stew|salad|taco|pizza|burger|garlic|onion|butter|cheese|eggs?|"
    r"foodtok|food|homemade|kitchen|chef|protein|calories?)",
    re.I,
)

# Terms that, on inspection of real results, reliably mark a non-food video whose caption
# happens to trip a food term.
NEGATIVE_TERMS = re.compile(
    r"\b(warzone|cod|gaming|gameplay|fortnite|minecraft|nba|nfl|anime|"
    r"skincare|makeup|outfit|streetwear|crypto|stocks?)\b",
    re.I,
)


def load_favourites(export_path: pathlib.Path) -> list[dict]:
    """Favourites only. Likes are ambient scrolling, 8x the volume, and export-capped."""
    with export_path.open(encoding="utf-8") as handle:
        data = json.load(handle)

    section = data.get("Likes and Favorites", {})
    entries = section.get("Favorite Videos", {}).get("FavoriteVideoList", [])

    out = []
    for entry in entries:
        # Favourites use Date/Link, likes use date/link. Read case-insensitively.
        link = entry.get("Link") or entry.get("link") or ""
        saved = entry.get("Date") or entry.get("date")
        match = VIDEO_ID.search(link)
        if match:
            out.append({"video_id": match.group(1), "url": link, "saved_at": saved})
    return out


def load_cache(cache_path: pathlib.Path) -> dict[str, dict]:
    if not cache_path.exists():
        return {}
    cache = {}
    with cache_path.open(encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line:
                continue
            try:
                row = json.loads(line)
            except json.JSONDecodeError:
                continue
            cache[row["video_id"]] = row
    return cache


def fetch(video_id: str, timeout: int) -> dict:
    """One oEmbed call. Returns a row with ok=False rather than raising."""
    request = urllib.request.Request(
        OEMBED.format(video_id=video_id),
        headers={"User-Agent": "recipe-importer/0.1 (stage-1 metadata)"},
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:
            payload = json.load(response)
    except urllib.error.HTTPError as exc:
        # 400 here means gone: deleted, private, or region-locked. A normal outcome for an
        # old backlog, not an error to retry.
        return {"video_id": video_id, "ok": False, "error": f"HTTP {exc.code}"}
    except Exception as exc:  # noqa: BLE001 - network layer, any failure is just a miss
        return {"video_id": video_id, "ok": False, "error": type(exc).__name__}

    return {
        "video_id": video_id,
        "ok": True,
        "caption": payload.get("title", ""),
        "creator_handle": payload.get("author_unique_id"),
        "creator_name": payload.get("author_name"),
        "thumbnail_url": payload.get("thumbnail_url"),
    }


def food_score(caption: str) -> float:
    """Crude confidence in [0, 1]. Enough to order a queue, not to auto-import on alone."""
    if not caption:
        return 0.0
    hits = len(set(m.group(0).lower() for m in FOOD_TERMS.finditer(caption)))
    if hits == 0:
        return 0.0
    score = min(1.0, 0.35 + 0.15 * hits)
    if NEGATIVE_TERMS.search(caption):
        score *= 0.3
    return round(score, 2)


def report(rows: list[dict]) -> None:
    total = len(rows)
    resolved = [r for r in rows if r.get("ok")]
    failed = [r for r in rows if not r.get("ok")]

    print(f"\n{'=' * 62}")
    print(f"  fetched      {total}")
    if total:
        print(f"  resolved     {len(resolved)}  ({len(resolved) / total * 100:.0f}%)")
        print(f"  unavailable  {len(failed)}  ({len(failed) / total * 100:.0f}%)  {dict(Counter(r.get('error') for r in failed))}")
    if not resolved:
        return

    scored = sorted(
        ({**r, "food_confidence": food_score(r.get("caption", ""))} for r in resolved),
        key=lambda r: r["food_confidence"],
        reverse=True,
    )
    high = [r for r in scored if r["food_confidence"] >= 0.65]
    mid = [r for r in scored if 0.35 <= r["food_confidence"] < 0.65]
    low = [r for r in scored if r["food_confidence"] < 0.35]

    print(f"\n  ranked queue (of {len(resolved)} resolved)")
    print(f"    high   >=0.65   {len(high):>4}   auto-import tier")
    print(f"    mid    0.35-.65 {len(mid):>4}   review pile")
    print(f"    low    <0.35    {len(low):>4}   skipped, kept visible")

    lengths = [len(r.get("caption", "")) for r in resolved]
    lengths.sort()
    print(f"\n  caption chars: median={lengths[len(lengths) // 2]} max={lengths[-1]} empty={sum(1 for x in lengths if x == 0)}")

    creators = Counter(r["creator_handle"] for r in resolved if r.get("creator_handle"))
    repeats = [(h, c) for h, c in creators.most_common() if c > 1]
    print(f"  creators: {len(creators)} distinct, {len(repeats)} appear more than once")
    if repeats:
        print(f"    top: {repeats[:6]}")

    food_creators = Counter(
        r["creator_handle"] for r in scored
        if r["food_confidence"] >= 0.5 and r.get("creator_handle")
    )
    print(f"\n  creators with >=2 food hits (clustering candidates): "
          f"{[(h, c) for h, c in food_creators.most_common() if c > 1][:8]}")

    print("\n  --- top of the queue ---")
    for row in scored[:10]:
        caption = " ".join(row.get("caption", "").split())[:88]
        print(f"    {row['food_confidence']:.2f}  @{row.get('creator_handle')}: {caption}")
    print(f"{'=' * 62}\n")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("export", nargs="?", type=pathlib.Path,
                        default=pathlib.Path("data/user_data_tiktok.json"),
                        help="TikTok export JSON (default: data/user_data_tiktok.json)")
    parser.add_argument("--cache", type=pathlib.Path, default=pathlib.Path("data/tiktok_stage1.jsonl"),
                        help="JSONL result cache; re-runs skip what is already in it")
    parser.add_argument("--limit", type=int, help="only fetch this many new videos")
    parser.add_argument("--delay", type=float, default=0.35,
                        help="base seconds between calls (default 0.35); jitter is added")
    parser.add_argument("--timeout", type=int, default=20)
    parser.add_argument("--report-only", action="store_true", help="summarise the cache, fetch nothing")
    args = parser.parse_args()

    cache = load_cache(args.cache)

    if args.report_only:
        if not cache:
            print(f"cache {args.cache} is empty; run a fetch first", file=sys.stderr)
            return 1
        report(list(cache.values()))
        return 0

    if not args.export.exists():
        print(f"export not found: {args.export}", file=sys.stderr)
        return 1

    favourites = load_favourites(args.export)
    pending = [f for f in favourites if f["video_id"] not in cache]
    if args.limit:
        pending = pending[: args.limit]

    print(f"{len(favourites)} favourites; {len(cache)} cached; fetching {len(pending)}")
    if pending:
        print(f"~{len(pending) * (args.delay + 0.3) / 60:.1f} min at a {args.delay}s delay\n")

    args.cache.parent.mkdir(parents=True, exist_ok=True)
    started = time.time()

    try:
        with args.cache.open("a", encoding="utf-8") as sink:
            for index, favourite in enumerate(pending, start=1):
                row = fetch(favourite["video_id"], args.timeout)
                row["url"] = favourite["url"]
                row["saved_at"] = favourite["saved_at"]

                sink.write(json.dumps(row, ensure_ascii=False) + "\n")
                sink.flush()
                cache[row["video_id"]] = row

                if index % 25 == 0 or index == len(pending):
                    ok = sum(1 for r in cache.values() if r.get("ok"))
                    print(f"  {index}/{len(pending)}  resolved {ok}/{len(cache)}  "
                          f"{time.time() - started:.0f}s")

                # Jittered, one at a time. Getting blocked at post 40 is the failure mode
                # that kills the feature; this pass is not worth rushing.
                time.sleep(args.delay + random.uniform(0, args.delay))
    except KeyboardInterrupt:
        print("\ninterrupted; cache holds everything fetched so far")

    report(list(cache.values()))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
