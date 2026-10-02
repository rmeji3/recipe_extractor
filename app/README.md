# Sousie

Expo client for the recipe API. Bundle id `app.souschef`.

```sh
npx expo start
```

## What is here

Plumbing, not design. Every screen is deliberately plain — the point is a working app
against the real API that you can then style.

| Route | |
|---|---|
| `sign-in` | Sign in with Apple |
| `index` | Paste a link, get a recipe. The core flow. |
| `cookbook` | Search, with variants shown as alternates on one dish |
| `recipe/[id]` | Full recipe, sectioned ingredients |
| `cook/[id]` | Cook mode: numbered steps, parsed timers, serving scaler |

## Things that will bite

- **Your phone cannot reach `localhost`.** `extra.apiUrl` in `app.json` is set to this
  Mac's LAN address, and the API binds `0.0.0.0` so it is reachable. Both change when you
  switch networks or deploy.
- **Sign in with Apple needs a real device or a simulator signed into an Apple ID**, and it
  does not work in Expo Go — it needs a development build (`expo-dev-client`, already
  installed). `npx expo run:ios` once, then `npx expo start --dev-client`.
- **Access tokens last an hour.** `lib/api.ts` refreshes transparently on a 401 and replays
  the request. Refresh tokens rotate on use, so concurrent refreshes are shared through one
  in-flight promise — two racing refreshes would sign the user out for no reason.
- **A cold extraction takes up to a minute.** `POST /api/recipes/from-url` returns 202 with
  a `Processing` row and `addFromUrl` polls until it settles. A cache hit returns finished
  immediately, so most popular videos feel instant.
- **Tokens live in the keychain** (`expo-secure-store`), not AsyncStorage. The refresh token
  is a 60-day bearer credential.

## Not built yet

- **Share extension** — the real entry point. Needs a second bundle id
  (`app.souschef.share`), App Groups, and Keychain Sharing so the extension can read the
  token. Paste works in the meantime and hits the same endpoint.
- Push notifications for "your import finished".
- The substitution, pantry, and grocery-list screens. The API has all three.
