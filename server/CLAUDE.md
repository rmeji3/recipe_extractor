# Server — Code Guidelines

> **Status: feature-complete for v1 and the v2 substitution work.** `Import`, `Metadata`
> (stage 1), `Classification`, `Recipes`, `Auth`, `Substitution`, `Cooking`, and `Pantry`
> are all built, with a Redis-backed queue behind them and a Python sidecar for
> transcription, vision, and classification. Everything below describes real files. Not
> built: rate limiting, metrics, deployment, and the Expo client.

ASP.NET Core (.NET 10) API with EF Core. **Postgres in production, SQLite in tests** —
keep queries provider-agnostic. Once a folder has real files in it, read the
neighboring files before writing code and match their patterns.

## Where things go

| Concern | Location | Notes |
|---|---|---|
| Controllers | `Controllers/<Domain>/` | Thin: auth/claims extraction, model binding, mapping service exceptions to status codes. No business logic. |
| Services | `Services/<Domain>/` | `IXService` interface + `XService` implementation (primary constructors, registered in `Program.cs`). All business logic and EF queries live here. |
| DTOs | `Dtos/<Domain>/` | `record` types. Input DTOs carry DataAnnotations validation; output DTOs are positional records projected in queries. |
| Entities | `Models/<Domain>/` | EF entities + domain enums. |
| Data | `Data/App/AppDbContext.cs` | Entity configuration in `OnModelCreating`. |
| Middleware | `Middleware/` | `ExceptionHandlingMiddleware` — last-resort 500 handler, already wired first in the pipeline. |
| Shared helpers | `Common/` | `PaginatedResult<T>`, `CaptionText`, `Exceptions/DomainValidationException`. |
| Auth | `Auth/` | `DevAuthenticationHandler` — Development/Testing only, stubs a fixed user. |
| Reference data | `Data/Seed/` | Curated substitution rules. Versioned with the code because a wrong ratio ruins dinner. |
| Tests | `Tests/Recipe.Tests/` | xUnit (v2) against SQLite, driven through `AppFixture`. |

Domains, from the build plan in the root [README](../README.md): `Import` (built —
normalised post arrays from on-device zip parsing), `Recipe` (the extraction schema),
`Classification` (food confidence, the ranked queue).

## Local run

`dotnet run --project Recipe.Api` serves Swagger UI at `/swagger` and the OpenAPI
document at `/openapi/v1.json`, both Development-only.

**Authentication runs two schemes under Development and Testing**, chosen per request: an
`Authorization: Bearer` header goes to JWT validation, anything else falls back to
`DevAuthenticationHandler`, which authenticates as `dev-user` (or whoever `X-Dev-User`
names). That keeps the web client working with no login while still letting real
Sign-in-with-Apple tokens be exercised. **The dev handler is registered only under those
two environments** — production is JWT-only, and registering the stub there would make
every endpoint public.

**Production refuses to start** without `Auth:Jwt:Key` (32+ characters) and
`Auth:Apple:ClientId`. Both are deployment mistakes that must be loud: no key means no
tokens, and no client id means an identity token minted for *any other app* would be
accepted.

Endpoints need Postgres (`ConnectionStrings:AppDb`); with no database reachable, writes
return 500 by design.

`POST /api/import/file` takes a raw export JSON upload and parses it server-side
(`Services/Import/ExportParser.cs`). The shipping app parses on device and posts to
`POST /api/import` instead — this endpoint is the documented fallback for exports the
on-device parser cannot read, and the way to test against a real file. It is the only
place that reads a whole export, so treat uploads as hostile: the size cap and the
detect-don't-assume parsing are load-bearing.

## House patterns

- **Controllers**: get the user via `User.FindFirstValue(ClaimTypes.NameIdentifier)`
  and return `Unauthorized()` if null; `[Authorize]` on the controller,
  `[Authorize(Roles = "Admin")]` on admin endpoints; both versioned route attributes
  (`api/[controller]` and `api/v{version:apiVersion}/[controller]`). Map
  `KeyNotFoundException` → `NotFound`, `DomainValidationException` → `BadRequest`
  with try/catch per action.
- **Never catch `InvalidOperationException` to produce a 400.** EF Core wraps database
  connection failures in it ("An exception has been raised that is likely due to a
  transient failure"), so a broad catch reports an unreachable database as a client
  error. Services signal bad input with `DomainValidationException`
  (`Common/Exceptions/`); anything else belongs to the 500 handler.
- **Services throw, controllers translate.** Services never return `ActionResult`.
- **Lists are paginated** with `PaginatedResult<T>.CreateAsync(query, pageNumber, pageSize)`
  (`Common/PaginatedResult.cs`). It clamps page size to `MaxPageSize` (100) and counts
  in the database. Call it on an already-projected DTO query, never on an entity query.
- **Project to DTOs inside the query** (`Select(x => new XDto(...))`) — don't return
  entities from list/read endpoints, and don't load whole entities to map in memory.
  When a row references a user by bare id, enrich with a left join in the same
  query (`from u in users.Where(...).DefaultIfEmpty()`) rather than a follow-up
  lookup per row.
- XML doc comments (`/// <summary>`) on controller actions.

## Auth

Sign in with Apple only. The client authorizes on device and posts the `identityToken`;
the server verifies it against Apple's published signing keys and this app's bundle id
before creating anything. Trusting the token body without checking the signature would
let anyone sign in as anyone.

- **Apple's `sub` is the identity**, not the email — an email can be a private relay
  address and can change.
- **Apple sends the name and email on the first authorization only.** Fill gaps on later
  sign-ins, never overwrite what is already stored, or a reinstall silently erases them.
- **Refresh tokens are stored as SHA-256 hashes and rotated on every use.** A leaked table
  must not hand anyone a live session, and rotation limits a stolen token to one use.
- **Access tokens cannot be revoked** — they are trusted until they expire (1 hour).
  Sign-out revokes the refresh token; it is not instant logout.

## Substitution is grounded, not generated

`POST /api/recipes/{id}/modify` adapts a recipe — vegetarian, healthier, higher protein —
and the model is **never asked how**. It is handed a fixed list of swaps drawn from
`Data/Seed/IngredientRules.json`, already filtered by the goal and the user's profile, and
asked only to choose among them. `ModificationService.Validate` re-checks every change
against that list and discards anything unbacked.

Three things must stay true or the feature stops being trustworthy:

- **Ratios come from the rule, never the model.** Butter is ~15% water and oil is not, so
  the swap is 0.75. That number decides whether the dish works.
- **Effect text comes from the rule.** The warning a user reads about their crumb going
  dense was written by a person, not improvised per request.
- **`avoid` is a filter, not a hint.** An allergy the model is merely told about is one
  that eventually gets ignored.

Substitutions are ranked so ingredients the user already cooks with come first — that
needs their imported library to exist, which is the half no competitor has.

A recipe with nothing substitutable returns no changes and says so. "No suggestion" is the
correct failure; confident nonsense is not.

**A variant is a new recipe with `SavedPostId = null` and `DerivedFromRecipeId` set.** One
recipe per saved post is a unique index, and the original came from a real video — it is
what the substitution was derived from and must stay intact.

## Cooking, pantry, and the review pile

- **`GET /api/recipes/{id}/cook?servings=N`** numbers the steps and parses timers out of
  their text. **Quantities scale, times do not** — doubling a recipe barely changes how
  long it cooks, and multiplying that number would be dangerous. Vague amounts ("a pinch")
  pass through untouched, and counts round to halves because "1.33 eggs" is unusable.
- **`POST /api/grocery-list`** merges ingredients across recipes. It refuses to merge what
  it cannot honestly combine — 100g of butter and 2 tbsp of butter need a density table
  nobody has, so the item keeps both sources and a null total. One wrong number is worse
  than two lines.
- **`GET /api/pantry/cookable`** ranks recipes by how much is already in the house. Its
  ingredient matching is deliberately loose: a recipe saying "boneless chicken thighs" is
  covered by a pantry saying "chicken", because sending someone shopping for what is in
  the fridge is the worse error.
- **`GET`/`POST /api/import/review`** is where the `Uncertain` tier goes. Without it,
  tuning classification for precision just strands posts forever. Approve queues
  extraction; reject marks them skipped and **keeps them visible** — that list is what
  makes aggressive precision safe.

## Long work is queued, never awaited

`POST /api/recipes/from-url` returns **202 with a `Processing` row**, and the client polls
`GET /api/recipes/{id}`. It does not wait for the extraction.

That is not an optimisation. A cold extraction fetches, transcribes, and reads a video —
up to a minute — and the client is a phone: iOS suspends backgrounded apps, and a
wifi-to-cellular handoff kills the socket. Anything that can take more than a couple of
seconds belongs on the queue with a status the client can poll.

A cross-user cache hit still returns **200 `Extracted`** immediately, so the common path
for a popular video has no waiting at all. Keep that distinction — the app is designed
around it.

## API compatibility

**Not yet binding — no client has shipped.** Break the wire format freely while the
schema is still being learned from real extraction output; that churn is expected
through v1.

**Becomes the prime directive the day the app hits the App Store.** The mobile app
ships through Apple review and updates slowly; the server deploys freely, so from
that point every server change must keep already-shipped clients working:

- Never rename or remove response fields, routes, or query params the app uses.
  Additive changes only; new required inputs need defaults.
- **Enums are wire contracts.** They serialize as strings
  (`JsonStringEnumConverter`) but are stored/sent as ints from the app — pin
  explicit numeric values (`Reel = 1, Carousel = 2`) and never reorder or reuse
  them. When a value is retired, leave its number permanently unused rather than
  reassigning it.
- Schema changes go through EF migrations (`Data/App/Migrations/`); prefer additive
  nullable columns.

## Database

- No provider-specific SQL/functions unless guarded by `AppDbContext.IsNpgsql`
  (which wraps the `Database.ProviderName` check) — tests run on
  SQLite and must still pass. Recipe search is the case this will bite first:
  Postgres full-text search (`tsvector` columns, GIN indexes) has no SQLite
  equivalent, so configure it inside the provider guard and fall back to `LIKE` in
  tests.
- **Timestamps are UTC `DateTime`, not `DateTimeOffset`.** SQLite cannot translate a
  `DateTimeOffset` in an `ORDER BY`, so any paginated list ordered by one fails in
  tests while passing on Postgres.
- Fire-and-forget background work must create its own DI scope
  (`IServiceScopeFactory`); never capture a request-scoped `DbContext` in a task
  that outlives the request. Import and extraction jobs are the obvious offenders —
  prefer the Redis queue over fire-and-forget for anything that can be retried.

## Testing — required, not optional

`AppFixture` (a `WebApplicationFactory<Program>`) boots the real API over an in-memory
SQLite database and holds the connection open for the fixture's lifetime. Take it with
`IClassFixture<AppFixture>`; use `CreateClient()` for HTTP-level tests and
`CreateDbContext()` to arrange or assert on data in its own DI scope.

**`Program.cs` registers no database provider under the `Testing` environment** — EF
permits only one provider per container, so the fixture owns that registration. If you
add provider-specific startup wiring, guard it the same way.

For any feature or service method you touch: check `Tests/Recipe.Tests` for existing
coverage of it first.

- **Coverage exists** → update/extend it to reflect the new behavior; don't leave it
  asserting the old behavior.
- **No coverage exists** → write it. New service methods, new controller behavior,
  and bug fixes (a fix without a regression test isn't done) all need tests.

Use `AppFixture.JsonOptions` when deserialising API responses — it mirrors the API's
`JsonStringEnumConverter` setup, and keep tests provider-agnostic (SQLite in `Tests/Recipe.Tests`, Postgres
in prod — see the Database section above).

Parser tests get real fixtures. The Instagram and TikTok export quirks documented in
the root README (duplicate captions, mojibake, label collisions across groups, the
`Date`/`date` case split) are all regressions waiting to happen — check in trimmed,
anonymised sample records and assert against them. Never check in the raw exports
from `data/`; they are gitignored and full of personal data.

## Review the code you touch, not just the task

Before finishing, re-read the file(s) you changed (and the immediate surrounding
code, not just your diff) and flag anything you notice, even if out of scope for the
requested change:

- **Refactoring**: does it match the house patterns above (thin controllers, logic
  in services, DTO projection in-query, etc.)? Call out anything that doesn't and
  either fix it if it's small, or say so explicitly if it's a larger change.
- **Security**: missing `[Authorize]`, unvalidated input, a query building
  provider-specific SQL without the `Database.ProviderName` guard, secrets or PII
  logged, an entity returned directly from a list/read endpoint instead of a
  projected DTO.
- **Performance/correctness**: N+1 queries, loading whole entities to map in memory,
  missing pagination on a list endpoint, a fire-and-forget task capturing a
  request-scoped `DbContext` instead of a fresh DI scope.

Report findings plainly at the end of your response — don't silently fix things
outside the requested scope without calling them out, and don't stay quiet about
something you noticed just because it wasn't asked about.

## Before you're done

Run from `server/`. Both must be clean:

```sh
dotnet build --nologo -v q   # zero warnings expected
dotnet test  --nologo -v q   # full suite, runs on SQLite
```
