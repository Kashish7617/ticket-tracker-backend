# Support Ticket Tracker — Backend (API)

ASP.NET Core Web API (.NET 10 LTS) + EF Core 10 + SQL Server, with xUnit tests.
The React frontend is in [`../FE`](../FE/README.md).

Also in this folder:
- [`REVIEW.md`](REVIEW.md): Part 2 code review (C# and SQL samples)
- [`BONUS.md`](BONUS.md): Bonus LLM integration design

---

## How to run

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (check with `dotnet --list-sdks`)
- SQL Server: LocalDB (comes with Visual Studio), a local or Docker SQL Server, or Azure SQL

### No SQL Server? Quick start with SQLite

To try the app without installing SQL Server:

```bash
cd BE
dotnet run --project src/TicketTracker.Api --launch-profile sqlite
```

This stores the data in a local file, `src/TicketTracker.Api/tickets-dev.db` (git-ignored); delete the file to start fresh.
It is for demos and local development only. **SQL Server is the real database**, and steps 1–2 below use it.

### 1. Set the connection string

The API reads `ConnectionStrings:TicketTracker`. The default in
[`src/TicketTracker.Api/appsettings.json`](src/TicketTracker.Api/appsettings.json) is a placeholder pointing at **LocalDB**:

```
Server=(localdb)\MSSQLLocalDB;Database=TicketTracker;Trusted_Connection=True;TrustServerCertificate=True
```

To use your own server, prefer user-secrets so the password never gets committed:

```bash
cd src/TicketTracker.Api
dotnet user-secrets set "ConnectionStrings:TicketTracker" "Server=<server>;Database=TicketTracker;User Id=<user>;Password=<password>;TrustServerCertificate=True"
```

(Or set the environment variable `ConnectionStrings__TicketTracker`.)

### 2. Start the API

```bash
cd BE
dotnet run --project src/TicketTracker.Api
```

| URL | What |
|---|---|
| http://localhost:5080/swagger | Swagger UI |
| http://localhost:5080/openapi/v1.json | OpenAPI document |
| http://localhost:5080/api/tickets | API |
| http://localhost:5080/health | Health check |

In `Development`, EF Core migrations run on startup, so the database and table are created automatically.
To run them by hand instead:

```bash
dotnet tool restore                        # installs the pinned dotnet-ef
dotnet ef database update --project src/TicketTracker.Api
```

[`scripts/migrate.sql`](scripts/migrate.sql) is an idempotent SQL script of the same migrations.

### 3. Run the tests

```bash
cd BE
dotnet test
```

The tests use an in-memory SQLite database, so they need no SQL Server.

### Docker (optional, whole stack)

```bash
cd BE
cp .env.example .env        # set MSSQL_SA_PASSWORD
docker compose up --build
```

UI: http://localhost:8080 · Swagger: http://localhost:5080/swagger

---

## API

| Method | Route | Description | Success | Errors |
|---|---|---|---|---|
| `GET` | `/api/tickets?status=&search=&page=1&pageSize=10` | List, newest first. `status` = `open`/`in_progress`/`closed`; `search` = case-insensitive "title contains"; `pageSize` ≤ 100 | 200 `PagedResult<Ticket>` | 400 |
| `GET` | `/api/tickets/{id}` | One ticket | 200 | 404 |
| `POST` | `/api/tickets` | Create. `title` (required, ≤ 120), `description` (≤ 4000), `priority` (required), `status` (optional, default `open`) | 201 + `Location` | 400 |
| `PUT` | `/api/tickets/{id}` | Replace title, description, status and priority | 200 | 400, 404 |
| `PATCH` | `/api/tickets/{id}/status` | Change only the status (used by the UI) | 200 | 400, 404 |
| `DELETE` | `/api/tickets/{id}` | Delete | 204 | 404 |
| `GET` | `/api/tickets/summary` | Counts per status and per priority (SQL `GROUP BY`) | 200 | |

All errors are [RFC 9457 ProblemDetails](https://www.rfc-editor.org/rfc/rfc9457) (`application/problem+json`):

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": { "Title": ["The field Title must be a string or array type with a maximum length of '120'."] }
}
```

Example ticket:

```json
{
  "id": 42,
  "title": "Cannot log in",
  "description": "Customer gets a 500 on the login page.",
  "status": "in_progress",
  "priority": "high",
  "createdAt": "2026-11-12T09:30:00.000Z",
  "updatedAt": "2026-11-12T10:02:13.511Z"
}
```

---

## Project structure

```
src/TicketTracker.Api/
  Controllers/TicketsController.cs   HTTP only: routing, status codes, ProblemDetails
  Services/TicketService.cs          Queries and business rules (ITicketService)
  Contracts/TicketContracts.cs       Request/response DTOs + validation attributes
  Domain/Ticket.cs                   Entity and enums
  Data/AppDbContext.cs               EF Core mapping, indexes
  Data/Migrations/                   EF Core migrations
  Infrastructure/EnumNames.cs        One place that defines enum wire names ("in_progress")
  Program.cs                         DI, JSON, ProblemDetails, CORS, OpenAPI, migrations
tests/TicketTracker.Api.Tests/       xUnit integration tests (WebApplicationFactory + SQLite)
```

---

## Technology choices and why

- **.NET 10 (LTS).** The brief says ".NET 8 or later". .NET 8's support ended on 10 Nov 2026, so the current LTS is the right choice for new code.
- **Controllers + a thin service layer.** Controllers only translate HTTP. `TicketService` holds the queries, so it can be tested and reused. A repository on top of EF Core would just wrap `DbSet`, so I didn't add one.
- **DTOs, not entities, at the boundary.** Prevents over-posting (clients can't set `Id`/`CreatedAt`) and lets the database schema change without breaking the API.
- **Validation with DataAnnotations + `[ApiController]`.** Gives automatic `400 ValidationProblemDetails`. The query object uses `IValidatableObject` to validate `status`. Enum JSON is strict: only `"open"`/`"in_progress"`/`"closed"`; integers and unknown values give 400.
- **Enums stored as strings in SQL** (`nvarchar(20)`), so the data reads clearly in ad-hoc queries and reports.
- **Search with `EF.Functions.Like` and escaped wildcards.** Parameterised, so no injection, and `50%` matches literally. Case-insensitivity comes from SQL Server's default collation.
- **The summary is two `GROUP BY` queries.** Only `(key, count)` rows come back from the database. Missing keys are filled with 0 so the client always gets every status and priority.
- **Indexes** on `(Status, CreatedAt)`, `(CreatedAt)` and `(Priority)` match the list filter and sort and the summary. A `'%term%'` title search can't use a B-tree index (see REVIEW.md, Sample 2). Full-text search is the next step if the table grows.
- **`TimeProvider` injected** for timestamps, so time can be faked in tests. Timestamps are stored as UTC and returned as ISO-8601 with `Z`.
- **`DELETE` uses `ExecuteDeleteAsync`:** one SQL statement, no load-then-delete round trip.
- **Integration tests.** `WebApplicationFactory` runs the real pipeline (routing, binding, validation, JSON, ProblemDetails, EF) against **SQLite in-memory**. SQLite is relational, so `GROUP BY`, `LIKE` and `ExecuteDelete` run as real SQL, unlike the EF InMemory provider. 19 tests cover create, validation (400s), invalid enums, 404s, filter, search (including wildcard escaping), paging, update, delete and summary.

---

## Assumptions

- **No authentication** (JWT was optional). It's the first thing I'd add.
- `description` is optional, up to 4000 characters. Empty or whitespace becomes `null`. Titles are trimmed.
- `priority` is required on create. `status` is optional on create and defaults to `open`.
- Search matches **title only**, case-insensitively, as a substring.
- Delete is a **hard delete**.
- The list is sorted newest first, with `Id` as a tiebreaker so paging is stable. `pageSize` is limited to 100.
- Any status can change to any other status (reopening is allowed).

---

## What I would improve with more time

1. **Auth:** JWT / Entra ID, with `[Authorize]` on write endpoints and roles (agent vs. viewer).
2. **Optimistic concurrency:** a `rowversion` column + ETag/`If-Match`, returning 409 on conflicting edits.
3. **Search:** full-text index on `Title`/`Description`.
4. **Observability:** OpenTelemetry, structured logs with correlation ids, and a database health check.
5. **Delivery:** Azure App Service + Azure SQL, with migrations run as a separate pipeline step (`dotnet ef migrations bundle`) instead of at app startup.
6. **Audit trail** of status changes, and **rate limiting** on write endpoints.

---

## CI

[`../.github/workflows/ci.yml`](../.github/workflows/ci.yml) runs on every push and PR: restore → build → `dotnet test` → check that no EF model change is missing a migration. It also lints and builds the frontend.
