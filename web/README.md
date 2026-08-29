# Web client

A local client for the API. Not the shipping product — the plan calls for a React Native
app with a share sheet — but the fastest way to use the pipeline as a person rather than
as curl, and the only way to check whether extractions are actually *correct* by reading a
recipe next to its source video.

```sh
npm run dev      # http://localhost:3000
```

Needs the API on :5141 (what `dotnet run` binds) and the sidecar on :8000. Configure in `.env.local`:

| Variable | Default | |
|---|---|---|
| `RECIPE_API` | `http://localhost:5141` | the ASP.NET API |
| `RECIPE_USER` | `rafael` | there is no login yet; the API stubs auth from a header under Development |

## Pages

| Route | |
|---|---|
| `/` | Paste a link, get a recipe. The core product flow. |
| `/cookbook` | Search across everything extracted. |
| `/recipe/[id]` | One recipe, with an edit view. |
| `/library` | Every imported post, ranked by food confidence — the bulk-import side. |

## Notes

- **Every API call is server-side** (`lib/api.ts`), so the dev-auth header never reaches
  the browser and there is no CORS to configure.
- **Editing uses textareas, not a row per ingredient.** This page exists to check quality
  fast; typing a list beats tabbing through three inputs per line. The server re-parses
  amounts, and anything unparseable becomes the item with no quantity — honest, since a
  guessed amount is worse than a missing one.
- **A cold extraction takes up to a minute** — fetch, transcribe, read frames. One already
  in the cross-user cache returns immediately.
