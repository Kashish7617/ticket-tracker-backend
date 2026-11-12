# Support Ticket Tracker: Backend (API)

[![CI](https://github.com/Kashish7617/ticket-tracker-backend/actions/workflows/ci.yml/badge.svg)](https://github.com/Kashish7617/ticket-tracker-backend/actions/workflows/ci.yml)


REST API for a support ticket tracker, built with **ASP.NET Core Web API (.NET 10 LTS)**, **EF Core 10** and **SQL Server**, with xUnit integration tests.

- Frontend (React + TypeScript): **[Kashish7617/ticket-tracker-frontend](https://github.com/Kashish7617/ticket-tracker-frontend)**
- [`REVIEW.md`](REVIEW.md): Part 2 code review (C# and SQL samples)
- [`BONUS.md`](BONUS.md): Bonus LLM integration design

---

## Contents

- [Features](#features)
- [Tech stack](#tech-stack)
- [Getting started](#getting-started)
- [Configuration](#configuration)
- [API reference](#api-reference)
- [Running the tests](#running-the-tests)
- [Docker](#docker)
- [Deploying to Render](#deploying-to-render)
- [Project structure](#project-structure)
- [Design decisions](#design-decisions)
- [Assumptions](#assumptions)
- [Future improvements](#future-improvements)

---

## Features

- Full ticket CRUD, plus a dedicated `PATCH` endpoint for status changes
- Filter by status, case-insensitive title search and stable pagination
- Summary endpoint with counts per status and per priority, computed in SQL (`GROUP BY`)
- Validation with consistent [RFC 9457 ProblemDetails](https://www.rfc-editor.org/rfc/rfc9457) error responses
- OpenAPI document + Swagger UI, and a `/health` endpoint
- EF Core migrations, plus an idempotent SQL script
- **SQLite mode**, so you can run it with zero database setup
- 19 integration tests that run the real HTTP pipeline

## Tech stack

| Area | Choice |
|---|---|
| Runtime | .NET 10 (LTS), ASP.NET Core Web API (controllers) |
| Data | EF Core 10, SQL Server (SQLite as an optional fallback) |
| Docs | Built-in OpenAPI + Swagger UI |
| Tests | xUnit, `WebApplicationFactory`, SQLite in-memory |
| Delivery | Dockerfile, docker-compose |

---

## Getting started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (check with `dotnet --list-sdks`)
- **Optional:** SQL Server: LocalDB (comes with Visual Studio), a local or Docker SQL Server, or Azure SQL

```bash
git clone https://github.com/Kashish7617/ticket-tracker-backend.git
cd ticket-tracker-backend
```

### Option A: quick start with SQLite (no SQL Server needed)

```bash
dotnet run --project src/TicketTracker.Api --launch-profile sqlite
```

The data goes into a local file, `src/TicketTracker.Api/tickets-dev.db`. It's git-ignored, and you can delete it to start fresh.
The tables are created automatically on startup. This mode is for demos and local development; **SQL Server is the real database**.

### Option B: SQL Server

1. **Set the connection string.** The default in [`appsettings.json`](src/TicketTracker.Api/appsettings.json) points at LocalDB:

   ```
   Server=(localdb)\MSSQLLocalDB;Database=TicketTracker;Trusted_Connection=True;TrustServerCertificate=True
   ```

   To use your own server, use user-secrets so the password never gets committed:

   ```bash
   cd src/TicketTracker.Api
   dotnet user-secrets set "ConnectionStrings:TicketTracker" "Server=<server>;Database=TicketTracker;User Id=<user>;Password=<password>;TrustServerCertificate=True"
   cd ../..
   ```

2. **Start the API:**

   ```bash
   dotnet run --project src/TicketTracker.Api
   ```

   In `Development`, migrations run on startup, which creates the database and table. To apply them by hand instead:

   ```bash
   dotnet tool restore        # installs the pinned dotnet-ef
   dotnet ef database update --project src/TicketTracker.Api
   ```

   [`scripts/migrate.sql`](scripts/migrate.sql) is an idempotent SQL script of the same migrations, for DBAs or pipelines.

### Local URLs

| URL | What |
|---|---|
| http://localhost:5080/swagger | Swagger UI |
| http://localhost:5080/openapi/v1.json | OpenAPI document |
| http://localhost:5080/api/tickets | API |
| http://localhost:5080/health | Health check |

To use the UI, run the [frontend](https://github.com/Kashish7617/ticket-tracker-frontend) with `npm run dev`. It proxies `/api` to `http://localhost:5080`.

---

## Configuration

Every setting can be supplied as an environment variable (use `__` in place of `:`).

| Setting | Env variable | Default | Purpose |
|---|---|---|---|
| `ConnectionStrings:TicketTracker` | `ConnectionStrings__TicketTracker` | LocalDB | SQL Server connection string |
| `Database:Provider` | `Database__Provider` | SQL Server | Set to `Sqlite` to use a SQLite file instead |
| `ConnectionStrings:Sqlite` | `ConnectionStrings__Sqlite` | `Data Source=tickets-dev.db` | SQLite file location (SQLite mode only) |
| `Database:ApplyMigrationsOnStartup` | `Database__ApplyMigrationsOnStartup` | `false` (`true` in Development) | Run EF migrations at startup (SQL Server mode) |
| `OpenApi:Enabled` | `OpenApi__Enabled` | `false` (always on in Development) | Expose `/openapi/v1.json` and `/swagger` |
| `Cors:AllowedOrigins` | `Cors__AllowedOrigins__0`, `__1`, … | none (`http://localhost:5173` in Development) | Browser origins allowed to call the API |

---

## API reference

| Method | Route | Description | Success | Errors |
|---|---|---|---|---|
| `GET` | `/api/tickets?status=&search=&page=1&pageSize=10` | List, newest first. `status` = `open` / `in_progress` / `closed`; `search` = case-insensitive "title contains"; `pageSize` ≤ 100 | 200 `PagedResult<Ticket>` | 400 |
| `GET` | `/api/tickets/{id}` | One ticket | 200 | 404 |
| `POST` | `/api/tickets` | Create. `title` (required, ≤ 120), `description` (≤ 4000), `priority` (required), `status` (optional, default `open`) | 201 + `Location` | 400 |
| `PUT` | `/api/tickets/{id}` | Replace title, description, status and priority | 200 | 400, 404 |
| `PATCH` | `/api/tickets/{id}/status` | Change only the status | 200 | 400, 404 |
| `DELETE` | `/api/tickets/{id}` | Delete | 204 | 404 |
| `GET` | `/api/tickets/summary` | Counts per status and per priority | 200 | |

`priority` is one of `low` / `medium` / `high`.

**Example ticket**

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

**Example error** (`application/problem+json`)

```json
{
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": { "Title": ["The field Title must be a string or array type with a maximum length of '120'."] }
}
```

**Try it with curl**

```bash
curl -X POST http://localhost:5080/api/tickets \
  -H "Content-Type: application/json" \
  -d '{"title":"Printer on fire","priority":"high"}'

curl "http://localhost:5080/api/tickets?status=open&search=printer"
```

---

## Running the tests

```bash
dotnet test
```

The tests use an in-memory SQLite database, so they don't need SQL Server.

---

## Docker

Build and run just the API (SQLite mode, no database needed):

```bash
docker build -t ticket-tracker-api .
docker run -p 5080:8080 \
  -e Database__Provider=Sqlite \
  -e "ConnectionStrings__Sqlite=Data Source=/tmp/tickets.db" \
  -e OpenApi__Enabled=true \
  ticket-tracker-api
```

The image runs as a non-root user, so put the SQLite file in a writable folder such as `/tmp`.

**Full stack** (SQL Server + API + web): [`docker-compose.yml`](docker-compose.yml) builds the web app from `../FE`, so clone the frontend next to this repo, into a folder named `FE`:

```bash
git clone https://github.com/Kashish7617/ticket-tracker-backend.git
git clone https://github.com/Kashish7617/ticket-tracker-frontend.git FE
cd ticket-tracker-backend
cp .env.example .env        # set a strong MSSQL_SA_PASSWORD
docker compose up --build
```

UI: http://localhost:8080 · Swagger: http://localhost:5080/swagger

---

## Deploying to Render

1. On [Render](https://render.com), create a **New → Web Service** from this repo and choose the **Docker** runtime.
2. Add these environment variables:

   | Key | Value |
   |---|---|
   | `PORT` | `8080` |
   | `Database__Provider` | `Sqlite` |
   | `ConnectionStrings__Sqlite` | `Data Source=/tmp/tickets.db` |
   | `Cors__AllowedOrigins__0` | your frontend URL, e.g. `https://ticket-tracker-frontend.vercel.app` (no trailing slash) |
   | `OpenApi__Enabled` | `true` (optional, turns on Swagger) |

3. Set the health check path to `/health` and deploy.

> **About SQLite on Render:** the container filesystem is temporary. Data is wiped on every redeploy and restart, and on the free plan the service sleeps after about 15 minutes idle. That's fine for a demo. To keep the data, attach a Render **Persistent Disk** (paid) and point `ConnectionStrings__Sqlite` at it, or use SQL Server: drop `Database__Provider`, set `ConnectionStrings__TicketTracker`, and run the migrations (`scripts/migrate.sql`).

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
  Program.cs                         DI, JSON, ProblemDetails, CORS, OpenAPI, DB provider, migrations
tests/TicketTracker.Api.Tests/       xUnit integration tests (WebApplicationFactory + SQLite)
scripts/migrate.sql                  Idempotent migration script
```

---

## Design decisions

- **.NET 10 (LTS).** .NET 8's support ended on 10 Nov 2026, so the current LTS is the right choice for new code.
- **Controllers + a thin service layer.** Controllers only translate HTTP. `TicketService` holds the queries, so it can be tested and reused. A repository on top of EF Core would just wrap `DbSet`, so I didn't add one.
- **DTOs, not entities, at the boundary.** Prevents over-posting (clients can't set `Id`/`CreatedAt`) and lets the schema change without breaking the API.
- **DataAnnotations + `[ApiController]`** give automatic `400 ValidationProblemDetails`. Enum JSON is strict: only the documented string values are accepted, and integers or unknown values return 400.
- **Enums stored as strings** (`nvarchar(20)`), so the data reads clearly in ad-hoc queries and reports.
- **Search uses `EF.Functions.Like` with escaped wildcards.** Parameterised (no injection), so `50%` matches literally.
- **The summary is two `GROUP BY` queries.** Only `(key, count)` rows come back, and missing keys are filled with 0.
- **Indexes** on `(Status, CreatedAt)`, `(CreatedAt)` and `(Priority)` match the list filter, the sort and the summary.
- **`TimeProvider` is injected**, so time can be faked in tests. Timestamps are UTC, ISO-8601 with `Z`.
- **`DELETE` uses `ExecuteDeleteAsync`:** one SQL statement, no load-then-delete.
- **Integration tests on SQLite in-memory.** SQLite is relational, so `GROUP BY`, `LIKE` and `ExecuteDelete` run as real SQL, unlike the EF InMemory provider.

## Assumptions

- No authentication (JWT was optional). It's the first thing to add.
- `description` is optional (≤ 4000 characters; blank becomes `null`). Titles are trimmed.
- `priority` is required on create. `status` defaults to `open`.
- Search matches the title only, case-insensitively, as a substring.
- Deletes are hard deletes. Any status can change to any other status (reopening is allowed).
- The list is sorted newest first, with `Id` as a tiebreaker so paging is stable.

## Future improvements

1. **Auth:** JWT / Entra ID, with roles (agent vs. viewer) on write endpoints.
2. **Optimistic concurrency:** `rowversion` + ETag/`If-Match`, returning 409 on conflicting edits.
3. **Full-text search** on title and description.
4. **Observability:** OpenTelemetry, structured logs with correlation ids, and a database health check.
5. **Delivery:** migrations as a separate pipeline step (`dotnet ef migrations bundle`) instead of at startup.
6. **Audit trail** of status changes, and **rate limiting** on write endpoints.

---

## CI

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on every push and pull request: restore → build → test → check that no EF model change is missing a migration.
