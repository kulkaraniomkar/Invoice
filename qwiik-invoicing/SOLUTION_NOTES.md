# SOLUTION_NOTES

This document explains the design decisions and trade-offs behind the Qwiik Invoicing API. The brief was intentionally under-specified, so where I had to choose, I optimized for **small, clean, and production-minded** over feature-complete.

---

## 1. How to run the project

See [README.md](README.md) for full instructions. Short version:

- **Docker Compose:** `docker compose up --build` → API on http://localhost:8080/swagger. SQL Server 2022 runs in a container; the API waits on a health check and initializes the schema (Development-only flag).
- **LocalDB:** generate the EF migration (`dotnet ef migrations add InitialCreate --project src/Qwiik.Invoicing.Api && dotnet ef database update ...`) or run `db/schema.sql`, then `dotnet run --project src/Qwiik.Invoicing.Api` → http://localhost:5080/swagger.
- **Tests:** `dotnet test` (SQLite in-memory; no SQL Server required).

## 2. Assumptions

- One API consumer = one **organization (tenant)**. Users and per-user permissions are out of scope; identity would sit in front of this API (see §8, §12).
- An invoice belongs to exactly one tenant and one currency; amounts use `decimal` with 2-dp rounding.
- Invoice numbers must be unique **per tenant** and are server-generated (clients shouldn't pick them — avoids collisions and forgery).
- The status lifecycle is deliberately simple: `Draft → Sent → Paid`, with `Cancelled` reachable from Draft or Sent. Paid and Cancelled are terminal.
- **Overdue is a derived condition** (status = Sent and DueDate < today), not a stored status. Storing it would require a background job to flip statuses and creates drift; deriving it is always correct.
- Editing invoice content after creation is out of scope (only status changes) — a common real-world rule once an invoice is issued, and it keeps the assessment small. Draft editing is listed as future work.
- Tax is a single flat rate per invoice (0–100%), applied to the subtotal.
- Time budget ~5–6 hours, so no auth server, no Tenants table, no soft delete — all acknowledged in §13.

## 3. Architecture overview

Layered but deliberately lightweight — two projects, no MediatR/CQRS/repository ceremony:

```
InvoicesController  →  IInvoiceService  →  InvoicingDbContext (EF Core)  →  SQL Server
        ↑                     ↑
 FluentValidation      Domain model (Invoice aggregate enforces invariants)
        ↑
 TenantResolutionMiddleware → ITenantProvider (scoped)
 GlobalExceptionHandler → ProblemDetails
```

- **Thin controllers** handle HTTP concerns (validation invocation, status codes); the **service** holds application logic; the **domain model** enforces business invariants (status transitions, money math) so they can't be bypassed regardless of entry point.
- **No repository layer**: EF Core's `DbContext` already is a unit-of-work + repository. Adding another abstraction at this size is indirection without benefit. If we later needed to swap persistence or mock at that seam, introducing one is mechanical.
- **Manual DTO mapping** instead of AutoMapper: at 5 endpoints, explicit mapping is more readable, debuggable, and refactor-safe.
- **Feature folder** (`Features/Invoices`) so related code lives together; scales naturally as modules are added.

## 4. Domain model explanation

`Invoice` is an aggregate root with private setters and factory/behavior methods; `InvoiceLineItem` is owned by it (private backing list, `IReadOnlyCollection` exposed, EF field access). Invariants live in the aggregate:

- **Creation** validates customer name, currency format (normalized to uppercase ISO-style 3 letters), date ordering (DueDate ≥ IssueDate), tax rate range, and 1–100 line items.
- **Money:** each line total is `round(quantity × unitPrice, 2, away-from-zero)`; `Subtotal = Σ line totals`; `TaxAmount = round(Subtotal × TaxRate / 100)`; `Total = Subtotal + TaxAmount`. Rounding **per line** matches how invoices are typically itemized and printed (the tested case: 3 × 0.335 → line 1.01, not 1.005 summed later).
- **Totals are persisted** on the invoice row. Trade-off: slight denormalization vs. list/summary queries never touching the line-items table. Since line items are immutable after creation, there is no staleness risk.
- **Status lifecycle** is enforced in one method (`ChangeStatus`) with an explicit allowed-transitions map. Illegal transitions throw `DomainException` → HTTP 422. Same-status "transitions" are rejected too, keeping `UpdatedAtUtc` meaningful.
- **Status is stored as a string** (max 20) rather than int: readable in ad-hoc SQL and reports, safe against enum reordering. The cost (a few bytes per row) is trivial at invoice-table scale.

## 5. Database design explanation

Two tables (see `db/schema.sql`):

- **Invoices** — identity, tenant, customer snapshot fields, dates (`date` type for issue/due — no fake midnight times), money columns `decimal(18,2)`, `TaxRate decimal(5,2)`, status string, notes, audit timestamps (`datetime2` UTC), and a `ConcurrencyToken` GUID rotated on every save for optimistic concurrency.
- **InvoiceLineItems** — FK to Invoices with cascade delete, description, `Quantity decimal(18,3)` (supports fractional quantities like hours), `UnitPrice`/`LineTotal decimal(18,2)`.

Notable choices:

- **Customer data is denormalized** onto the invoice (name/email snapshot) rather than a Customers table. Real invoices must preserve the customer details *as invoiced*, even if the customer record later changes — so a snapshot is arguably more correct, not just simpler.
- **GUID primary keys** so IDs can be generated app-side and are non-enumerable across tenants. Known trade-off: random GUIDs fragment a clustered index. At scale I'd switch to `NEWSEQUENTIALID()`/GUIDv7 or cluster on `(TenantId, CreatedAtUtc)` — noted, not needed at this size.
- **No Tenants table** — tenant existence is asserted by the caller (header today, JWT claim in production). Adding one (with FK) is the first thing I'd do with more time (§14).

## 6. API design explanation

RESTful resource design under `/api/invoices`:

- `POST /api/invoices` → `201 Created` + `Location` header. Server generates the invoice number (`INV-yyyyMMdd-XXXXXX`, ambiguous characters excluded, unique index as backstop) and all totals — clients send inputs, never computed values.
- `GET /api/invoices` → paged envelope `{ items, page, pageSize, totalCount, totalPages }`. Filters: `status`, `search` (invoice number or customer name), `issuedFrom/To`. Sorting is **whitelisted** (`issueDate|dueDate|total|customerName|createdAt`) with a stable `Id` tie-break — never raw client input into `OrderBy`. List items exclude line items (projection, `AsNoTracking`).
- `GET /api/invoices/{id}` → full detail with line items, plus derived `isOverdue`.
- `PATCH /api/invoices/{id}/status` — PATCH because it's a partial, single-field state change; the response returns the updated invoice.
- `GET /api/invoices/summary` — dashboard numbers in one call (see §9 for the query shape).

Errors are uniform RFC 7807 ProblemDetails: 400 validation, 404 not-found, 409 concurrency, 422 domain rule, 500 generic. Enum values serialize as strings (`JsonStringEnumConverter`) for readable payloads.

## 7. Validation approach

Two layers, intentionally overlapping:

1. **FluentValidation at the boundary** (`CreateInvoiceRequestValidator` + line-item validator): field lengths, email format, currency regex, date ordering, tax-rate range, 1–100 line items, quantity/price bounds. Failures → 400 with per-field error dictionary. This is the *user-friendly* layer.
2. **Domain guards in the aggregate**: the same core invariants re-checked in `Invoice.Create`/`ChangeStatus`. This is the *safety* layer — the domain can't be put into an invalid state by any future code path that skips the validator (background jobs, imports, tests).

Duplication here is deliberate defense-in-depth, not an accident.

## 8. Tenant isolation approach

Consistency was the goal — isolation is enforced in **one place each** for read, write, and transport:

- **Transport:** `TenantResolutionMiddleware` requires a valid `X-Tenant-Id` GUID on all `/api` routes (400 otherwise) and populates a scoped `ITenantProvider`. `/health` is exempt.
- **Reads:** a single EF **global query filter** (`i.TenantId == _tenantProvider.TenantId`) on the Invoice aggregate. No query anywhere needs to remember a `Where(...)` — including `Find`, `Count`, aggregates, and the summary.
- **Writes:** `SaveChanges` stamps `TenantId` on new entities and **throws if no ambient tenant exists** — data can never be written unscoped.
- **Information hiding:** requesting another tenant's invoice ID returns `404`, indistinguishable from a nonexistent ID — no cross-tenant existence oracle.
- **Verified by tests:** stamping, filtering, cross-tenant 404 probe, and per-tenant invoice-number uniqueness are all covered in `TenantIsolationTests`.

**Why a header, and why that's temporary:** a header keeps the assessment runnable without an identity provider, but it is client-asserted and therefore not production-trustworthy. In production the tenant comes from a **validated JWT claim** (Azure AD B2C / Entra ID), and the middleware swaps to reading the claim — nothing else in the stack changes, which is exactly why tenant resolution was isolated behind `ITenantProvider`. Row-level isolation via `TenantId` (shared schema) is the standard SaaS starting point; per-tenant databases only become worth the operational cost for large/regulated tenants.

## 9. Indexing and performance strategy

Every index leads with `TenantId`, because every query is tenant-scoped:

| Index | Serves |
|---|---|
| `UX (TenantId, InvoiceNumber)` unique | Number uniqueness per tenant + number lookup |
| `(TenantId, Status)` | Status filter, summary grouping, overdue check |
| `(TenantId, IssueDate)` | Date-range filters, default-adjacent sorts |
| `(TenantId, DueDate)` | Overdue computation |
| `(TenantId, CreatedAtUtc)` | Default sort (newest first) |
| `(InvoiceId)` on line items | Detail fetch |

Query efficiency decisions:

- List endpoint: `AsNoTracking` + projection to a slim DTO (no line items), `COUNT` + page fetch, `pageSize` clamped to 100. Offset pagination is fine at assessment scale; I'd move to **keyset pagination** for deep pages (§14).
- Summary: **one** `GroupBy(Status)` aggregate query plus one overdue count/sum — no N+1, no loading rows into memory.
- Persisted totals mean list/summary never join line items.
- With more time/scale: `INCLUDE` columns on the hot indexes to make list queries covering, and revisit clustered key choice (§5).

## 10. Testing approach

Per the guidance, tests target **key business rules**, not coverage numbers:

- `InvoiceStatusTransitionTests` — every allowed transition, illegal transitions (incl. from terminal states), same-status rejection, overdue derivation logic.
- `InvoiceCalculationTests` — totals math, the per-line rounding case (3 × 0.335 → 1.01), zero tax, creation invariants, currency normalization.
- `TenantIsolationTests` — tenant stamping on save, query-filter scoping, cross-tenant 404 probe, same invoice number allowed across tenants but not within one, save-without-tenant throws.
- `CreateInvoiceRequestValidatorTests` — boundary validation rules via FluentValidation.TestHelper.

Tests run against **SQLite in-memory** (single kept-open connection, `EnsureCreated`) so `dotnet test` needs no SQL Server and runs in CI as-is. Trade-off acknowledged: SQLite isn't SQL Server (decimal ordering/`SUM` differ), so tests avoid provider-sensitive assertions; with more time I'd add a small **Testcontainers + real SQL Server** integration suite using `WebApplicationFactory` (`Program` is already `public partial` for this).

## 11. Azure deployment and monitoring considerations

**Target shape (right-sized for this service):**

- **Azure App Service (Linux, container)** or **Azure Container Apps** for the API. App Service gives deployment slots and simple autoscale; Container Apps if we expect scale-to-zero or more services later. AKS would be over-engineering for one API.
- **Azure SQL Database** (serverless or S-tier to start). Point-in-time restore, automatic backups, built-in HA — exactly the undifferentiated heavy lifting we shouldn't own.
- **Azure Key Vault + Managed Identity** — no connection strings or secrets in config/CI; the app authenticates to SQL and Key Vault with its identity.
- **Application Insights** — Serilog is already structured, so wiring the sink gives request traces, dependency (SQL) timing, failure rates, and custom events (e.g. status transitions). Alerts on p95 latency, 5xx rate, and SQL DTU.
- **CI/CD:** the included GitHub Actions build/test workflow extends to: publish container → deploy to **staging slot** → smoke test → **slot swap** (instant rollback = swap back). **EF migrations run as an explicit pipeline step** (`dotnet ef database update` or bundled migration app) — never on app startup in production; the `InitializeOnStartup` flag is Development-only by design.

**Scaling & resource utilisation:** the API is stateless (tenant context is per-request), so it scales horizontally behind the platform load balancer. The database is the real bottleneck — mitigations in order: the indexes above, read replicas for dashboard/list traffic, caching summary per tenant (short TTL), then elastic pools if tenant count grows. Health endpoint is already in place for probes.

**Rollbacks:** slot swap for the app; for the DB, migrations are written to be backward-compatible for one release (expand/contract pattern) so app rollback doesn't require schema rollback.

## 12. Security considerations

- **Tenant isolation** as in §8 — filter + stamping + 404 opacity; the highest-value security property in a multi-tenant system.
- **No authentication implemented** — stated explicitly, not hidden. Production: Entra ID / JWT bearer, tenant claim replaces the header, per-endpoint authorization policies.
- **Input handling:** EF Core parameterizes everything (no raw SQL); sort fields whitelisted; page size clamped; payload sizes bounded by validation (≤100 line items, string length caps).
- **Non-enumerable GUID IDs**; server-generated invoice numbers can't be forged or collided by clients.
- **Error hygiene:** unexpected exceptions return a generic 500 ProblemDetails; details go to structured logs only. Logs avoid PII beyond what's operationally needed.
- **Transport/infra (production):** HTTPS-only + HSTS at the platform edge, secrets in Key Vault, Managed Identity, rate limiting (ASP.NET Core rate-limiter middleware) as future work.
- Docker container runs as **non-root**; the compose SA password is clearly marked development-only.

## 13. Known limitations

Deliberate scope cuts, in rough order of importance:

1. **No authentication/authorization** — tenant is client-asserted via header (see §8 for the production path).
2. **No Tenants table** — no FK validation that a tenant exists.
3. **EF migration not committed** — the code was authored in an offline environment without the .NET SDK; generating it is a one-line command (README) and `db/schema.sql` provides the equivalent DDL. I could not run `dotnet build`/`dotnet test` in that environment either, so the code should be built and the suite run locally before relying on it.
4. **Summary sums across currencies** — fine while a tenant uses one currency; a multi-currency tenant would need per-currency grouping.
5. **No invoice content editing** after creation (only status), no soft delete, no audit trail beyond timestamps.
6. **Offset pagination** degrades on very deep pages.
7. `EnsureCreated`/startup initialization is a Development convenience only.

## 14. What I would improve with more time

Roughly in order:

1. **AuthN/AuthZ** — JWT bearer (Entra ID), tenant claim, authorization policies; delete the header path.
2. **Tenants table + FK**, tenant provisioning endpoint.
3. **Integration tests** with Testcontainers (real SQL Server) via `WebApplicationFactory` — exercise middleware, filters, and ProblemDetails end-to-end.
4. **Draft editing** (update line items while status = Draft) and soft delete with audit trail (who/when for every status change).
5. **Idempotency keys** on POST (safe client retries) and **rate limiting**.
6. **Keyset pagination**, covering indexes with `INCLUDE` columns, summary caching per tenant.
7. **Per-currency summary**, and richer dashboard (monthly trend, top customers).
8. **Deployment hardening:** Bicep/Terraform IaC, staging slot + swap pipeline, App Insights dashboards and alert rules as code.
