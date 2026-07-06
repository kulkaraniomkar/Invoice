# Qwiik Invoicing API

A small, production-minded, multi-tenant invoice management backend built with **C# / ASP.NET Core 8**, **EF Core**, and **SQL Server**.

Built for the Qwiik technical assessment. See [SOLUTION_NOTES.md](SOLUTION_NOTES.md) for architecture, trade-offs, and Azure deployment thinking, and [AI_USAGE.md](AI_USAGE.md) for AI disclosure.

---

## Quick start

### Option A — Docker Compose (easiest, no local SQL Server needed)

Requires Docker Desktop.

```bash
docker compose up --build
```

This starts SQL Server 2022 and the API. The API waits for the database health check, creates the schema on startup (development convenience flag), and listens on:

- API: http://localhost:8080
- Swagger UI: http://localhost:8080/swagger

### Option B — Run locally against LocalDB

Requires .NET 8 SDK and SQL Server LocalDB (ships with Visual Studio).

```bash
# Create the schema (choose one):

# 1. EF Core migration (preferred — see note below)
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate --project src/Qwiik.Invoicing.Api
dotnet ef database update --project src/Qwiik.Invoicing.Api

# 2. Or run the handwritten SQL script against LocalDB
#    db/schema.sql (identical schema to the EF model)

# Run the API
dotnet run --project src/Qwiik.Invoicing.Api
```

- API: http://localhost:5080
- Swagger UI: http://localhost:5080/swagger

> **Note on migrations:** the initial implementation was produced in an offline environment without the .NET SDK, so the EF migration is generated with the one-line command above rather than committed as a snapshot. `db/schema.sql` is the equivalent handwritten DDL and satisfies the "database migration or SQL script" requirement on its own. In Development, setting `Database:InitializeOnStartup=true` (already set for Docker Compose) also creates the schema automatically.

### Run the tests

```bash
dotnet test
```

Tests use SQLite in-memory, so no SQL Server is needed to run them (see SOLUTION_NOTES.md → Testing approach).

---

## Multi-tenancy

Every `/api/*` request must include a tenant header:

```
X-Tenant-Id: 11111111-1111-1111-1111-111111111111
```

Requests without a valid GUID header get a `400 ProblemDetails`. All queries are automatically scoped to the tenant via an EF Core global query filter; entities are stamped with the tenant on save. In production this would come from a JWT claim instead — see SOLUTION_NOTES.md → Tenant isolation.

## Endpoints

| Method | Route | Description |
|---|---|---|
| POST | `/api/invoices` | Create an invoice (server generates invoice number, calculates totals) |
| GET | `/api/invoices` | List invoices — pagination, filtering, search, sorting |
| GET | `/api/invoices/{id}` | Invoice details including line items |
| PATCH | `/api/invoices/{id}/status` | Update status (enforced lifecycle: Draft → Sent → Paid, cancellable before Paid) |
| GET | `/api/invoices/summary` | Dashboard summary: totals, outstanding, paid, overdue, per-status breakdown |
| GET | `/health` | Health check (no tenant header required) |

### List query parameters

`page` (default 1), `pageSize` (default 20, max 100), `status`, `search` (matches invoice number or customer name), `issuedFrom`, `issuedTo`, `sortBy` (`issueDate` | `dueDate` | `total` | `customerName` | `createdAt`), `sortDir` (`asc` | `desc`, default `desc`).

## Example requests

```bash
TENANT="11111111-1111-1111-1111-111111111111"
BASE="http://localhost:8080"   # or http://localhost:5080 for LocalDB run

# Create an invoice
curl -s -X POST "$BASE/api/invoices" \
  -H "Content-Type: application/json" \
  -H "X-Tenant-Id: $TENANT" \
  -d '{
    "customerName": "Acme Logistics",
    "customerEmail": "billing@acme.example",
    "issueDate": "2026-07-01",
    "dueDate": "2026-07-31",
    "currency": "usd",
    "taxRate": 8.5,
    "notes": "July shipping services",
    "lineItems": [
      { "description": "Container handling", "quantity": 3, "unitPrice": 250.00 },
      { "description": "Customs processing", "quantity": 1, "unitPrice": 120.50 }
    ]
  }'

# List (filter + pagination)
curl -s "$BASE/api/invoices?status=Draft&page=1&pageSize=10&sortBy=issueDate" \
  -H "X-Tenant-Id: $TENANT"

# Details
curl -s "$BASE/api/invoices/{id}" -H "X-Tenant-Id: $TENANT"

# Move Draft -> Sent
curl -s -X PATCH "$BASE/api/invoices/{id}/status" \
  -H "Content-Type: application/json" \
  -H "X-Tenant-Id: $TENANT" \
  -d '{ "status": "Sent" }'

# Summary dashboard
curl -s "$BASE/api/invoices/summary" -H "X-Tenant-Id: $TENANT"
```

## Project layout

```
src/Qwiik.Invoicing.Api/
  Domain/            Invoice aggregate, line items, status lifecycle, domain exceptions
  Features/Invoices/ Controller + service (application logic)
  Infrastructure/    DbContext, tenant resolution, DB initializer
  Contracts/         Request/response DTOs
  Validation/        FluentValidation validators
  Middleware/        Global exception handling (ProblemDetails)
tests/Qwiik.Invoicing.Tests/   Business-rule tests (status lifecycle, money, tenant isolation, validation)
db/schema.sql        Handwritten SQL Server DDL matching the EF model
.github/workflows/   CI: restore, build, test
```

## Error handling

All errors return RFC 7807 `ProblemDetails`:

- `400` — validation failures / missing tenant header
- `404` — not found (including cross-tenant access, deliberately indistinguishable)
- `409` — optimistic concurrency conflict
- `422` — domain rule violation (e.g. illegal status transition)
- `500` — unexpected error (details logged, never leaked)
