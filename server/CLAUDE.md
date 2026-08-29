# Server — Code Guidelines

> **Status: one domain built.** `Import` is a complete vertical slice — entity, DTOs,
> service, controller, migration, tests — and everything below describes real files.
> `Recipe` and `Classification` are still targets.

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
| Tests | `Tests/Recipe.Tests/` | xUnit (v2) against SQLite, driven through `AppFixture`. |

Domains, from the build plan in the root [README](../README.md): `Import` (built —
normalised post arrays from on-device zip parsing), `Recipe` (the extraction schema),
`Classification` (food confidence, the ranked queue).

## Local run

`dotnet run --project Recipe.Api` serves Swagger UI at `/swagger` and the OpenAPI
document at `/openapi/v1.json`, both Development-only. Under Development and Testing,
`DevAuthenticationHandler` authenticates every request as `dev-user` so authorized
endpoints are reachable without a token; send an `X-Dev-User` header to act as someone
else. **It is registered only under those two environments** — production gets JWT
bearer, and registering the dev handler there would make every endpoint public.

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
