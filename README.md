# Recipe Importer

Turn a saved food video into a recipe you can actually cook from — one at a time, or
a whole backlog at once.

**The core product is a single video in, a recipe out.** Share a link, get ingredients
and steps. Every competitor does this; the bet is on doing it materially better, and
that means extraction quality is the product, not a feature of it.

Around that:

- **Backlog import** is the differentiator. Nobody else turns a platform export into a
  cookbook. It runs in the background — the user shares their first link on day one and
  finds hundreds more recipes waiting later.
- **Corpus-grounded substitution** is the retention hook. It only works once a corpus
  exists, so it compounds with the import instead of standing alone.

The three reinforce each other, but they are not equally urgent. If single-video
extraction is mediocre, nothing downstream matters — the user never gets far enough to
care about the import.

## Status

Pre-code. The pre-flight gate is closed: both export formats have been inspected
and confirmed (see [Confirmed export schemas](#confirmed-export-schemas)). Nothing
below the plan has been built yet.

| Path | What it is |
|---|---|
| `server/` | ASP.NET Core (.NET 9) API. See [server/CLAUDE.md](server/CLAUDE.md) for code guidelines. |
| `data/` | Real personal exports used to confirm the schemas below. **Gitignored — never commit these.** |

Planned, not yet present: React Native app, Python FastAPI extraction sidecar,
Redis queue, Postgres.

## Stack

- **App:** React Native (iOS only for v1)
- **API:** ASP.NET Core
- **Extraction sidecar:** Python FastAPI (yt-dlp can't run on device)
- **Queue:** Redis
- **DB:** Postgres

---

# Build plan (rev 2)

*Rewritten after inspecting real Instagram and TikTok export files. Supersedes
rev 1. The architecture changed substantially as a result.*

## What changed from rev 1

| Assumption in rev 1 | What the real files showed |
| --- | --- |
| Exports are URL-only, fetch every post | **Instagram ships full captions.** Zero network calls needed |
| One record shape, two thin adapters | Platforms are structurally different. Two real parsers |
| Collections give a free food filter | **No usable collection membership on either platform** |
| Fetch queue is the core of v1 | Only TikTok needs it. Instagram needs none |
| Import everything the user saved | **Favourites only.** Likes are ambient noise |
| Filtering is a preprocessing step | It's a ranked queue that drives import order |

---

## Confirmed export schemas

### Instagram (`saved_posts.json`)

Top-level is a **bare array**. Each record:

```jsonc
{
  "timestamp": 1785369434,        // seconds, not ms
  "media": [],                    // empty in practice
  "fbid": "...",
  "label_values": [ /* heterogeneous, recursive */ ]
}
```

`label_values` entries are one of two shapes:

- Leaf: `{label, value, href?}`
- Group: `{title, dict: [...]}` — nests up to 3 deep

Labels observed, by depth:

| Depth | Label | Notes |
| --- | --- | --- |
| 0 | `URL` | post permalink |
| 0 | `Caption` | **full caption text** |
| 0 | `Title` | |
| 0 | group `Owner` | contains creator |
| 0 | group `Hashtags` | one record had 29 nested |
| 0 | group `Brand partner` | |
| 2 | `Username` | creator handle |
| 2 | `URL` | under `Owner` — creator's **link-in-bio**, often a personal site, not an instagram.com profile |
| 2 | `Name` | display name under `Owner`; also the hashtag text under `Hashtags` |

**Parser rules:**

- **Walk `label_values` recursively.** Never index positionally. Group entries have
  no `label` key at all, so a naive flat loop crashes or silently drops the `Owner`
  data.
- **Key leaves by group path, not by bare label.** Labels collide across groups:
  `Name` appears under `Hashtags`, `Owner`, and `Brand partner` (159 occurrences in
  the sample, mostly hashtags), and `URL` appears both at depth 0 (the post
  permalink) and under `Owner`. A flat `{label: value}` dict silently overwrites the
  post URL with the owner's. Track the group titles you descended through.
- Groups nest through an **unnamed intermediate group** (`title: ""`) before reaching
  leaves — `Hashtags → "" → Name`, `Owner → "" → {URL, Name, Username}`. Skip empty
  titles when building the path.
- **Dedupe captions before concatenating.** Sample file had 18 `Caption` leaves
  against 14 `URL`s, but the 4 extras were **byte-identical duplicates** within the
  same record, not distinct carousel slides. Blind concatenation doubles those
  captions and feeds the model the same text twice. Dedupe first, then concatenate
  whatever distinct captions remain — a carousel recipe can still split ingredients
  and steps across slides, so don't just take the first.
- URL mix was 8 `/p/` and 6 `/reel/`. Carousels are a real share of saves, not an
  edge case. Use the path segment as a ranking hint, never a hard filter.
- `media` is empty. No local files to work with.
- **The latin-1 → utf-8 mojibake repair is required, not optional.** 12 of 18
  captions in the sample are mojibake (`\u00f0\u009f\u0092\u008e` → 💎). Emoji and
  smart quotes are both affected, and smart quotes sit inside recipe words. Still
  guard it behind a try — it raises on already-clean text.
- The wrapper shape varies by account. One sample seen elsewhere used a
  `saved_saved_posts` object with `string_map_data`. **Detect, don't assume.**

### TikTok (`user_data_tiktok.json`)

Root is `{"Likes and Favorites": {...}}` with twelve subsections. Only two matter:

```jsonc
"Favorite Videos": { "FavoriteVideoList": [ {"Date": "...", "Link": "..."} ] }
"Like List":       { "ItemFavoriteList":  [ {"date": "...", "link": "..."} ] }
```

**Note the case inconsistency.** Favourites use `Date`/`Link`, likes use
`date`/`link`. Same file, same section. Normalise on read or you drop records
silently.

- **No captions. No creator. Nothing but a date and a link.**
- Links are `https://www.tiktokv.com/share/video/{id}/`. Extract the numeric ID as
  your cache key.
- `Favorite Collection` held one entry: a name and a date, with **no membership
  data**. The separate `Collection` key was an empty object. Collection filtering is
  impossible on TikTok.
- Both sections also carry an `App` sibling key (value `1`) next to the list. Read
  the list by name; don't assume the section has exactly one key.
- Other `Likes and Favorites` subsections in the sample: sounds (31), effects (4),
  comments (3), and six empty lists. None are useful. Ignore them.

### Import favourites only

Sample account: **786 favourites, 6,000 likes, overlap of 6.**

Those are different behaviours. Favourites are deliberate saves; likes are ambient
scrolling. Importing likes means fetching 6,000 videos, spending real money, and
delivering a cookbook full of comedy clips.

Also: the like list was **exactly 6,000**, spanning 11 months, while favourites went
back to 2019. A round number at a hard boundary is a cap. Never assume the like list
is complete.

Offer likes as an explicit opt-in with a warning, or not at all.

---

## The asymmetry (central architectural fact)

```
INSTAGRAM                          TIKTOK
parse zip                          parse zip
  └─ caption already present         └─ stage 1: fetch light metadata
       └─ extract                         └─ classify (batched)
                                               └─ stage 2: extract food only
0 network calls                    ~1 cheap call per post + N extractions
seconds                            minutes
```

Do not try to unify these behind one pipeline shape. Share the extraction and
storage layers; keep ingest separate.

### oEmbed: confirmed working (measured 2026-08-29)

TikTok's public oEmbed endpoint returns the **full caption** and the creator, so
stage 1 is nearly free and TikTok lands close to Instagram. No yt-dlp needed for
metadata.

- **The share URL in the export is rejected.** `tiktokv.com/share/video/{id}/`
  returns 400. Rewrite it to `https://www.tiktok.com/video/{id}` — the handle is not
  required, the numeric id alone resolves.
- Returns `title` (the caption, **not truncated** — longest seen 2,216 chars),
  `author_unique_id`, `author_name`, `thumbnail_url`, `embed_product_id`.
- ~0.3s per call. 150 calls at a 0.35s delay drew no rate limiting; 786 favourites
  is roughly 8 minutes.

**But 34% of the backlog is gone.** On a 120-video sample, **only 66% resolved** —
the other 41 returned HTTP 400, i.e. deleted, private, or region-locked. That is
what a backlog reaching back to 2019 looks like. Budget for it: the ranked queue
must treat an unresolvable id as a normal outcome, not an error, and the "found N
recipes" number should be computed against what resolved.

### What the sample says about corpus size

On the same 120-video sample, a keyword pass flagged **23% of resolved videos** as
food. Extrapolated: **786 favourites → roughly 117 food candidates**, not the ~214
the pitch assumes.

The keyword pass alone is also visibly imprecise — it matched a *Warzone* clip on
"Season" and a Cookie Monster cartoon on "cookie". This is the argument for the
plan's later stages, not against them: keywords are the free first pass, and the
ambiguous middle has to go to the batched model call. Precision, not recall, is
what the auto-import tier needs.

Repeat creators were thin in the sample (top handle appeared twice), so creator
clustering may help less here than assumed. Re-check it on the full corpus before
building it.

---

## Classification: 786 saves, not all recipes

The filtering problem, in order of what to try:

**1. Keyword pass (free).** Cooking verbs, units, ingredient nouns, hashtags like
`#recipe` / `#foodtok`. Resolves a chunk confidently in both directions with no
model call. Only the ambiguous middle goes further.

**2. Cluster by creator before classifying individually.** People who save recipes
save from the same few food accounts repeatedly. A handle appearing 20 times with 3
confident food hits means the other 17 almost certainly are too, and one confident
non-food hit on a comedy account skips everything else from them. This will beat
prompt tuning for accuracy. Creator comes free from the Instagram export and from
TikTok stage 1.

**3. Batch the model call.** Concatenate ~100 titles per call and return
food/not-food per line. Two or three calls for an entire library, a few cents total.
One call per post is the expensive version and there is no reason to do it.

**4. Cache classification by platform video ID, shared across users.** Viral food
videos get saved by many people. Decide once, free forever. Same cache as extraction
results.

### Ranked queue, not a binary filter

Sort by food confidence and process descending. The user sees real recipes appearing
within a minute while the uncertain tail grinds in the background. You don't need to
be right about everything, only about the first twenty.

| Tier | Behaviour |
| --- | --- |
| High confidence | Extract and import automatically |
| Uncertain | "Review these" pile with thumbnails, **bulk** approve |
| Low confidence | Don't import, but keep visible under "skipped" |

**Tune for precision.** A missed recipe is recoverable — the user finds it in the
skipped list or adds it by link. A cookbook full of memes destroys trust in the
entire import. The skipped list is the safety valve that lets you be aggressive.

**Surface the number.** "214 recipes found in 786 saves" is the wow moment. It reads
as the app being smart about their mess, not as failing to process 572 things.

---

## V1

**Definition of done:** hand it to five people who aren't you. They share a link and
get a recipe good enough to cook from. They import their backlog, and they can find a
specific recipe a week later.

The first half of that sentence is the harder test and the one to build against.

### Scope

- **Share sheet for single posts — the primary flow, not an afterthought.** Must
  handle every link shape the share sheet emits, including short links that carry no
  video id
- **Cross-user cache by platform video id.** On the share path this is the speed story
  as much as the cost one: the second person to share a viral recipe should wait
  roughly zero seconds
- Zip import for both platforms (favourites only on TikTok), **running in the
  background** — never something the user waits on
- Two-stage classification with ranked queue
- Extraction cascade
- Two-stage classification with ranked queue
- Extraction cascade
- Recipe cards: title, servings, times, ingredients, steps, source link, creator
- Search across the corpus
- Manual edit of any field
- Basic cooking mode: step list, screen awake, timers parsed from steps

### Out of scope

| Cut | Why |
| --- | --- |
| Hosting video | Direct distribution, DMCA target, storage cost |
| Video playback in cooking mode | v3 |
| Generic "ask AI about this recipe" | Competitors have it, zero moat |
| Search over other people's corpora | Cache extractions across users, never expose one user's library to another |
| Meal planning | Every incumbent has it, add only on request |
| Likes import | Ambient noise, 8x the cost for worse results |
| Android | Ship one platform properly |

### Unzip on device

The app opens the zip via document picker, walks it, extracts the saved-content
JSON, and POSTs a normalised array. ~60KB instead of a 200MB upload.

- No upload wait on cellular, no multipart handling, no temp storage
- The user's archive never touches your infrastructure — a much cleaner answer when
  App Store review asks what you do with it
- **Cost:** parser bugs need an app update. Mitigate by keeping file-matching
  patterns in remote config, and offer server-side upload as a fallback when local
  parsing finds nothing recognisable, so you can inspect the tree yourself
- Resolve the shortcode / video ID at parse time and send it alongside the URL

**If you ever accept zips server-side, treat them as hostile.** Validate entry paths
against traversal, cap decompressed size and entry count before reading, stream
entries and only read expected names. Never blind-extract.

### Extraction cascade

```
1. Have caption? (Instagram: yes. TikTok: from stage 1)
2. Parse caption with a cheap LLM call -> structured recipe
3. Completeness check. Escalate if ANY of:
     - fewer than 3 ingredients
     - fewer than half of ingredients have a quantity
     - fewer than 2 steps
4. On escalation: read the frames. See "Vision" below — the original argument for
   Gemini Flash was that it handles audio and frames in one call with no separate ASR
   step, but ASR turned out to be free and local, so that argument is gone.
5. Delete the media. Nothing persists on your infrastructure.
6. Write structured recipe + keyframes to Postgres.
```

A 60-second reel is roughly 15-20k input tokens on Flash, fractions of a cent.
Verify current pricing before building cost assumptions on it.

### Vision: what on-screen text actually contains

*Measured by pulling frames from real escalating videos and reading them, 2026-08-29.*

The assumption behind the vision path was that silent recipe videos put their method on
screen as text. That is **half true, and the half that is false is the important one.**

What on-screen text actually turned out to be, on sampled frames:

| Seen | Example |
| --- | --- |
| Hook captions that persist the whole video | *"pov: you learned how to meal prep high protein frozen burritos"* |
| Section labels | *"episode 17"*, *"spice mix"* |
| Title and macro cards | *"QUESO CHICKEN ROLLS — 335 Calories, 35g Protein, 9g Fat"* |
| **Ingredient lists with quantities** | **not observed** |

**Quantities only ever come from captions.** Three sources, three different things:

| Source | Gives you | Missing |
| --- | --- | --- |
| Caption | ingredients **with amounts**, sometimes steps | method, when the creator did not type it |
| Narration (Whisper) | method, in order, with timestamps | amounts — narrators say "season the chicken", not "1.5 tsp" |
| Frames | ingredient *identity*, step sequence, equipment | amounts |

So vision recovers **what and in what order, never how much**. That is a real gain — an
identified ingredient list plus an ordered method is a cookable recipe for anyone who can
eyeball quantities — but it does not close the gap on its own, and no amount of model
capability changes it, because the information is not in the video.

**Some escalating videos are not recipes at all.** One that transcribed to zero words
turned out to be freezer-organisation tips; the caption-path extraction correctly returned
an empty recipe. Vision cannot rescue what was never there. Measure how much of the
`NeedsVision` bucket is actually recoverable before sizing the spend.

**Frame selection is the real engineering problem, not the model call.** Scene-change
detection (`select='gt(scene,0.12)'`) spent frames on empty cutting-board transitions and
returned near-duplicates of the same persistent overlay. Sampling naively is how the cost
gets away from you. Gate it: run local OCR over candidate frames first and only send ones
carrying meaningful text, or dedupe by perceptual hash so a caption that sits on screen
for forty seconds costs one frame instead of twelve.

**Use Claude vision here, not Gemini Flash.** The plan chose Flash because it handles audio
and frames in one call with no separate ASR step — but ASR is now solved locally by Whisper
at zero marginal cost, so that advantage is gone. What is left is a second provider, a
second key, a second SDK, and a second failure mode, in exchange for nothing this pipeline
needs. Frames are images, and images go to the model already wired up. Roughly 1.4k tokens
per resized frame; a dozen frames is around ten cents on Opus, less on a smaller model, and
the cross-user cache means each video is paid for once ever.

### Schema

```jsonc
{
  "title": "string",
  "servings": 4,
  "prep_minutes": 15,
  "cook_minutes": 30,
  "ingredients": [
    {
      "quantity": 2.0,
      "unit": "tbsp",
      "item": "soy sauce",
      "prep_note": "low sodium",
      "confidence": 0.94,       // surface low values in the UI
      "source_ts": 38.2         // free to capture, powers v3
    }
  ],
  "steps": [
    { "text": "string", "ts_start": 38.2, "ts_end": 51.0 }
  ],
  "equipment": ["string"],
  "source_url": "string",
  "creator_handle": "string",
  "food_confidence": 0.91
}
```

- **Capture timestamps from day one.** Free once the video is already going through
  the model, and they unlock v3. Don't let the playback question block the pipeline.
- **Rewrite steps in neutral instructional voice.** An ingredient list is a
  functional statement of facts and isn't protectable (*Publications International
  v. Meredith*, 7th Cir. 1996). The creative expression in written instructions is.
  The model paraphrases, always.

### Queue (TikTok path only)

- One job per post, jittered delays, per-platform token bucket. Getting blocked at
  post 40 is the failure mode that kills the feature.
- Cache by platform video ID, not per user.
- Optimistic UI: write "processing" rows immediately.
- Per-platform success-rate metrics on a dashboard. Learn about breakage from a
  graph, not a one-star review.

### Onboarding

The export takes up to 48 hours to generate, and users can break it four ways: HTML
instead of JSON, a limited date range, wrong categories selected, or never returning
for the file.

- Share sheet works from minute one. The zip is an optional power move.
- Remind them when the file is likely ready.
- **Validate and give specific errors.** Detect an HTML export and say re-request as
  JSON. Detect a missing saved-content folder and name the category to select. A
  generic "import failed" after a two-day wait loses the user permanently.
- Log the file inventory of every failed import so you can see the variants you
  didn't anticipate.

### Success criteria

- 50 real users who aren't friends
- Median import completes without a platform block
- Classification precision above 90% on the auto-import tier
- Extraction success above 85% on the caption path
- A third of importers return in week two

---

## V2: Smart Substitution

**Goal:** the reason people stay after the novelty of the import.

**Don't build this first.** It only works once someone has a corpus, so it's
retention, not acquisition. Shipping it early means shipping the copyable half of
the product before the defensible half.

"Ask an LLM to swap an ingredient" is a prompt — any competitor ships it in an
afternoon, and at least one already has an AI chat that fills in missing amounts.
Two layers make it defensible:

**Layer 1: structural substitution.** Model ingredient *function*, not identity.
Buttermilk is acid plus dairy and swapping it changes the leavening. An egg is a
binder, leavener, emulsifier, or wash depending on context. Fat swaps change texture
and smoke point.

Build a curated table of ingredient roles, substitution rules, ratio adjustments,
and knock-on effects. Use the LLM to *select from it*, not to invent. A wrong
substitution ruins dinner, which is a much worse failure than a wrong search result.

**Layer 2: corpus grounding.** You know what this user cooks because they imported
hundreds of recipes. No competitor has that.

- Substitute using their inferred pantry
- Rank suggestions by ingredients recurring across their own library
- Update pantry state from cook-mode completions and grocery checkoffs

**Success criteria:** substitution used in >20% of cook sessions; week-four
retention measurably above the pre-v2 baseline; users describe it as the reason they
stayed.

---

## V3+ (parked)

- **Timestamped steps and quantity provenance.** Tap an ingredient, see the frame
  where it appeared or the transcript line where it was said. Directly answers the
  "check the amounts before you cook" complaint every app in this category gets. The
  schema already captures what this needs.
- **On-device video for cooking mode.** Server fetch stays transient; the device
  fetches its own copy from the platform CDN. Lazy fetch on first cook, LRU evict.
  Most saved recipes never get cooked.
- **Non-English food video.** Spanish, Korean, Hindi, Arabic. Every incumbent is
  English-first. Ingredient-name and unit conversion is the hard part and the value.
  Doubles as a channel, since those creator communities are underserved.

---

## Risks

| Risk | Severity | Mitigation |
| --- | --- | --- |
| **Target user's backlog isn't as big as assumed** | **Kills the hook** | **Partly confirmed.** 786 favourites, but 34% no longer resolve and ~23% of the rest look like food: an estimated ~117 recipes, not ~214. Still a real cookbook, but the demo number is half the pitch. Measure on more accounts before scaling |
| TikTok oEmbed doesn't return descriptions | ~~High~~ **Resolved** | Works, returns full untruncated captions. Requires rewriting the export's share URL to `tiktok.com/video/{id}` |
| Saved videos rot | Medium | 34% of a 2019-onward backlog is already unavailable. Treat as a normal outcome in the queue, never an error |
| Export layout varies by account | High | Defensive recursive parsing, remote-config patterns, log failed inventories |
| Instagram/TikTok block the fetch pattern | Medium | Token bucket, jitter, ID cache. Instagram path needs no fetching at all |
| Platform endpoints break | Ongoing | Permanent maintenance cost. Monitor success rate |
| App Store removal after a complaint | High | Never host media. Market as a recipe tool, never a downloader |
| Competitor copies the importer | Medium | Real, but annoying enough that nobody has bothered. Get to v2 first |
| Crowded category | Medium | Confirms demand. Your risk is no advantage in food, so import and substitution must be genuinely better |

**Legal posture:** transient server-side fetch for analysis, nothing persisted,
steps always paraphrased, creator always credited and linked. Not risk-free, but the
lowest-risk version that works. Not legal advice. Past a few thousand users, pay an
IP attorney for an hour.

---

## Sequence

1. **This weekend:** run caption extraction against the 14 real Instagram captions
   you already have on disk. Does caption-only parsing produce usable recipes? Also
   curl TikTok oEmbed. Both answerable in an afternoon.
2. **Week 1:** extraction cascade + classification as a CLI. No app. Run against 100
   real posts and read the output yourself.
3. **Weeks 2-4:** parsers, queue, schema, API, minimal React Native app.
4. **Week 5:** ship to 5 people who aren't you. Watch them onboard without helping.
5. **Then:** 50 users before writing a line of v2.

Step 1 is now free — you already have the data. Step 2 is the one most likely to be
skipped and the one that tells you whether the product works. Don't build UI on top
of an extraction pipeline whose output you haven't read.
