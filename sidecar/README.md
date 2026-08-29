# Extraction sidecar

Transcribes a cooking video and structures it into a recipe. Separate from the ASP.NET
API because yt-dlp and Whisper cannot run on device, and because the API should not hold
a subprocess open for the length of a download.

```sh
../.venv/bin/uvicorn app.main:app --reload --port 8000
```

`GET /health` reports whether `yt-dlp` and `ffmpeg` are on the host.

## Endpoints

| Route | Input | Notes |
|---|---|---|
| `POST /transcribe` | `{url, caption?, structure?}` | Fetches audio, transcribes, optionally structures |
| `POST /transcribe/upload` | multipart `file`, `caption?`, `structure?` | Same pipeline for a file you already hold |

Both return a transcript with per-segment timestamps, and a `Recipe` when `structure` is
true and there is enough narration to work with.

## Things that will bite you

- **TikTok URLs need the creator handle.** oEmbed resolves `tiktok.com/video/{id}`, but
  yt-dlp 404s on it — it requires `tiktok.com/@{handle}/video/{id}`. Stage 1 captures the
  handle for exactly this reason; build the canonical URL before calling here.
- **`is_speech: false` is the signal to escalate, not an error.** Plenty of recipe videos
  narrate nothing and show the method as on-screen text over music. Those transcribe to a
  few stray words, and the service declines to spend a structuring call on them. Route
  them to the vision path instead.
- **Transcription recovers method, not amounts.** Narrators say "season the chicken", not
  "add 1.5 teaspoons". Pass the caption alongside — the creator typed those numbers, and
  the extractor is told to prefer the caption when the two disagree. Caption and audio are
  complementary; neither alone is enough.
- **Media never persists.** Every request runs inside a temp directory removed in a
  `finally`, including on failure. Keep it that way — the legal posture in the root README
  depends on it.
- **yt-dlp breaks when platforms change.** Pin nothing, update often, and monitor the
  fetch success rate rather than learning about it from a bug report.

## Configuration

Read from the repo-root `.env` (shell environment wins):

| Variable | Default | |
|---|---|---|
| `ANTHROPIC_API_KEY` | — | required when `structure` is true |
| `WHISPER_MODEL` | `small` | `tiny`/`base` drop ingredient quantities; `medium` is ~3x slower for little gain |
| `WHISPER_DEVICE` / `WHISPER_COMPUTE` | `cpu` / `int8` | |
| `RECIPE_MODEL` | `claude-opus-5` | |
| `MAX_DURATION_SECONDS` | `900` | |
| `MAX_UPLOAD_BYTES` | `209715200` | |

Roughly 0.2x realtime on CPU: a 45-second video takes about 10 seconds, a 3-minute one
about 35. Transcription itself costs nothing; only the structuring call is billed.
