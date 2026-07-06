# Architecture, Design Decisions, Trade-offs & Production Considerations

This is the deep-dive companion to SOLUTION_NOTES.md. Where SOLUTION_NOTES answers the brief's 14 sections, this document explains **every significant decision**: what was chosen, what was rejected, what it costs, and the question a reviewer is most likely to ask about it. If you can explain everything here without notes, you can defend the codebase.

---

## Part 1 — Architecture

### 1.1 Overall shape

```
HTTP request
   │
   ├─ Serilog request logging
   ├─ GlobalExceptionHandler (IExceptionHandler → ProblemDetails)
   ├─ TenantResolutionMiddleware (/api/* only) ──► TenantProvider (scoped, per-request)
   │                                                      │
   ▼                                                      ▼
InvoicesController ──► IInvoiceService ──► InvoicingDbContext ──► SQL Server
   │                        │                    │
FluentValidation      application logic    global query filter (TenantId)
                            │              SaveChanges: stamp TenantId,
                            ▼              audit timestamps, rotate concurrency token
                     Invoice aggregate
                     (invariants: status lifecycle, money math)
```

Two projects only: `Qwiik.Invoicing.Api` and `Qwiik.Invoicing.Tests`.

**Decision: layered-but-flat, not Clean/Onion/Hexagonal with separate Domain/Application/Infrastructure assemblies.**

- *Why:* project boundaries are a compile-time enforcement tool. With one module and 5 endpoints, folder boundaries + code review enforce the same discipline at zero ceremony. The dependency **direction** is still correct (domain depends on nothing; infrastructure depends on domain), so extracting assemblies later is mechanical, not a rewrite.
- *Rejected:* 4–6 project Clean Architecture template. It signals "I copy templates" more than "I exercise judgment" at this scale — the brief explicitly rewards not over-engineering.
- *Cost:* nothing physically prevents a future dev from referencing EF types in the domain. Mitigation: the domain files have zero infrastructure `using`s today, and tests would catch most leaks.
- *Likely question:* "How would this scale to more modules?" → Feature folders (`Features/Invoices`) already group by capability; the next module is `Features/Payments` etc. When two modules need to share domain concepts or teams need enforced boundaries, split assemblies then — reversible decision, deferred until it pays.

### 1.2 Thin controller + service, no repository, no MediatR

- **Controller** owns HTTP: model binding, invoking the validator, translating results to status codes. It contains no business logic.
- **`IInvoiceService`** owns application logic: orchestrating the aggregate, queries, pagination, the summary aggregation.
- **Domain aggregate** owns invariants (see Part 2). Business rules live where they cannot be bypassed.

Rejected abstractions, and why:

| Rejected | Reason |
|---|---|
| Repository + Unit of Work | `DbContext` **is** a repository + UoW (Microsoft's own guidance). A wrapper would hide EF's most valuable features (`Include`, projections, change tracking) or leak them anyway. The `IInvoiceService` interface is already the mockable seam. |
| MediatR / CQRS | 5 endpoints, one writer model. CQRS pays off with divergent read/write models or pipelines of cross-cutting behaviors; here it's indirection. We still get "queries are cheap" via `AsNoTracking` projections — CQRS-lite without the framework. |
| AutoMapper | Manual mapping is ~30 lines total, is refactor-safe (rename breaks compile, not runtime), and shows up in stack traces. Convention-based mapping hides bugs exactly where money fields are involved. |

*Likely question:* "Isn't the service a god class risk?" → It's one feature's application service, ~300 lines. The split points are already visible (queries vs. commands) if it grows.

### 1.3 Request pipeline ordering (this ordering is deliberate)

`UseExceptionHandler` → `UseSerilogRequestLogging` → Swagger (dev) → `TenantResolutionMiddleware` → `MapControllers` / `MapHealthChecks`.

- Exception handler is **outermost** so even middleware failures become ProblemDetails.
- Tenant middleware runs **before** controllers so no controller can execute without a tenant, but **after** Swagger and around `/health` exemption so probes and docs need no header.
- `/health` is unauthenticated and tenant-free by design: Azure load balancer probes can't send tenant headers.

---

## Part 2 — Domain design decisions

### 2.1 Rich aggregate, not anemic entities

`Invoice` has private setters, factory-style creation, and behavior methods; line items live in a private backing list exposed as `IReadOnlyCollection` (EF configured for field access). You **cannot** construct an invalid invoice or mutate line items from outside.

- *Why:* invariants enforced in the type system are enforced for every future caller — controller, background job, import script, test. Validation-only-at-the-boundary rots the moment a second entry point appears.
- *Cost:* slightly more ceremony than `{ get; set; }` POCOs, and EF needs explicit configuration (field access, private constructor). Worth it: the money math and status rules are the product.
- *Likely question:* "Why duplicate rules between FluentValidation and the domain?" → Deliberate defense-in-depth with different jobs: FluentValidation produces **user-friendly 400s with per-field errors**; domain guards are the **last line** that makes invalid states unrepresentable. They overlap on content, not on purpose.

### 2.2 Status lifecycle: explicit transition map

```
Draft ──► Sent ──► Paid (terminal)
  │         │
  └────┬────┘
       ▼
   Cancelled (terminal)
```

Implemented as a static `IReadOnlyDictionary<InvoiceStatus, InvoiceStatus[]>` consulted by a single `ChangeStatus` method. Illegal transitions → `DomainException` → **422**. Same-status changes are also rejected (keeps `UpdatedAtUtc` honest and makes the API idempotency story explicit rather than accidental).

- *Why a map, not if/else:* the whole state machine is readable in five lines, and adding a state is a data change, not a logic change.
- *Why 422 not 400:* the request was syntactically valid; the *business rule* rejected it. Distinguishing these helps client error handling.

### 2.3 "Overdue" is derived, never stored ← the highest-signal decision in the codebase

Overdue = `Status == Sent && DueDate < today`, computed at read time.

- *Why:* a stored `Overdue` status requires a scheduled job to flip rows at midnight — per timezone? what about clock skew, missed runs, backfills? Every one of those is a state-drift bug. A derived predicate is **always correct by construction** and costs one indexed comparison.
- *Cost:* you can't `WHERE Status = 'Overdue'`; the summary computes it with a dedicated `(TenantId, DueDate)`-indexed query. Acceptable.
- *Likely question:* "What if the business wants overdue notifications?" → That's an *event*, not a *status*: a scheduled job **detects** the derived condition and sends notifications, without ever mutating invoice state. Detection and state stay decoupled.

### 2.4 Money

- `decimal` everywhere (never `double` — binary floats cannot represent 0.1).
- **Per-line rounding**, away-from-zero, to 2 dp: `LineTotal = round(qty × price)`; `Subtotal = Σ LineTotals`; `TaxAmount = round(Subtotal × rate/100)`; `Total = Subtotal + TaxAmount`.
- *Why per-line:* the printed invoice itemizes lines; if the displayed lines don't sum to the displayed subtotal, customers dispute invoices. Test-pinned case: 3 × 0.335 → line 1.01 (not 1.005 carried and rounded later).
- **Totals are persisted** on the invoice row. Denormalization is safe here because line items are immutable after creation → no staleness path exists. Payoff: list and summary queries never touch the line-items table.
- *Likely question:* "Away-from-zero vs banker's rounding?" → It's the convention consumers expect on invoices and what most tax authorities use; banker's rounding (`ToEven`) minimizes aggregate drift in statistics, which isn't the goal here. The real answer in production: the rounding rule is a jurisdiction/tax-engine concern and would be configurable.

### 2.5 Server-generated invoice numbers

Format `INV-yyyyMMdd-XXXXXX`, 6 chars from an alphabet excluding `0/O/1/I` (human-readable over phone/email), generated with `RandomNumberGenerator` (not `Random` — no seed-collision cluster under concurrency). Pre-check loop (max 5 attempts) + **unique index `(TenantId, InvoiceNumber)` as the authoritative backstop** — the check is an optimization; the index is the guarantee.

- *Why not client-supplied:* collision handling and forgery risk move to the server where they belong.
- *Why not a sequence:* per-tenant gap-free sequences require serialized allocation (a lock or a counters table) — real contention cost. Random suffix is contention-free.
- *Likely question:* "Some jurisdictions require sequential gap-free numbering." → True, and it's a known limitation; the fix is a per-tenant counter row allocated inside the same transaction as the insert. Documented as future work rather than silently half-done.

### 2.6 Status stored as string; dates as `DateOnly`

- String status (`HasConversion<string>`, max 20): readable in ad-hoc SQL, reports, and support debugging; immune to enum reorder bugs. Cost of a few bytes/row is irrelevant at invoice-table cardinality. Check-constraint in `db/schema.sql` keeps garbage out.
- `IssueDate`/`DueDate` are `DateOnly` → SQL `date`: an invoice date has no meaningful time-of-day; storing `datetime` invites timezone-off-by-one bugs at midnight boundaries. Audit fields (`CreatedAtUtc`, `UpdatedAtUtc`) are `datetime2` in UTC — instants, not dates.

### 2.7 Immutability after creation

Only status can change post-creation. This mirrors real invoicing (an issued invoice is a legal document; corrections are credit notes) *and* is what makes persisted totals safe (§2.4). Draft editing is explicitly future work — the aggregate design (encapsulated line items) already supports adding it safely.

---

## Part 3 — Multi-tenancy (the consistency story)

The brief said isolation is fine "if implemented consistently." Consistency here means: **one enforcement point per concern, impossible to forget per-query.**

| Concern | Single enforcement point |
|---|---|
| Transport | `TenantResolutionMiddleware`: valid `X-Tenant-Id` GUID required on `/api/*`, else 400 ProblemDetails. Populates scoped `TenantProvider`. |
| Reads | One EF **global query filter**: `i.TenantId == _tenantProvider.TenantId`. Applies to every LINQ query, `Count`, aggregate, and the summary — no `Where(...)` to forget, ever. |
| Writes | `SaveChanges`/`SaveChangesAsync` override stamps `TenantId` on added entities and **throws if no ambient tenant** — unscoped writes are impossible, not just discouraged. |
| Information leakage | Cross-tenant `GET /invoices/{id}` returns 404 identical to a nonexistent id — no existence oracle across tenants. |

All four properties are pinned by tests in `TenantIsolationTests` (stamping, filter scoping, cross-tenant 404 probe, same invoice number allowed across tenants but unique within one, save-without-tenant throws).

**Why a header (and why that's honest, not naive):** a header is client-asserted and worthless as a security boundary — this is stated openly rather than hidden. It exists so the assessment runs without an identity provider. The production swap: JWT bearer (Entra ID), tenant claim read by the same middleware into the same `ITenantProvider`. Because everything downstream depends only on `ITenantProvider`, **nothing else changes** — that seam is the point of the abstraction.

**Why shared-schema row-level isolation (vs. schema- or database-per-tenant):** shared schema is the standard SaaS starting point — one migration path, one connection pool, cheap tenant onboarding. Database-per-tenant buys hard isolation and per-tenant restore at the cost of N× operational surface; it's the right call later for large/regulated tenants, and the row-level design doesn't preclude moving specific tenants out.

*Likely question:* "Global query filters have gotchas — do you know them?" → Yes: they're ignored by raw SQL (we use none), can be bypassed via `IgnoreQueryFilters()` (used nowhere in app code; a test uses it deliberately to *verify* stamping), and the filter references a scoped service so the `DbContext` must be scoped too (it is — default `AddDbContext` lifetime).

---

## Part 4 — API design decisions

- **POST → 201 + `Location`**; the client sends inputs only — the server computes number, totals, timestamps. Clients never send derived values (a classic tamper vector).
- **PATCH `/invoices/{id}/status`** rather than `PUT /invoices/{id}`: it's a partial, single-concern state change; PUT would imply full-resource replacement we deliberately don't support.
- **List endpoint contract:** paged envelope `{ items, page, pageSize, totalCount, totalPages }`; `pageSize` clamped 1–100 (an unbounded page size is a self-DoS invitation); filters `status`, `search` (invoice number or customer name), `issuedFrom/To`; **whitelisted** sort fields with a stable `Id` tie-break.
  - *Why whitelist + tie-break:* never feed client strings into `OrderBy` (injection/typo surface), and without a tie-break, equal sort keys make pagination non-deterministic — rows can appear twice or vanish across pages.
- **Summary endpoint:** one round trip for the dashboard: totals, outstanding (Sent), paid, overdue count+amount, per-status breakdown. Implemented as a single `GroupBy(Status)` aggregate + one overdue aggregate — the numbers are computed **in SQL**, not by loading rows.
- **Errors are uniformly RFC 7807 ProblemDetails**, mapped centrally in `GlobalExceptionHandler`: 400 validation/tenant, 404 not-found (incl. cross-tenant), 409 `DbUpdateConcurrencyException`, 422 `DomainException`, 500 generic-with-details-logged-only. One mapping table, no scattered try/catch.
- **Optimistic concurrency:** a GUID `ConcurrencyToken` rotated on every save; two racing status updates → one wins, one gets 409. *Why not `rowversion`?* `rowversion` is more idiomatic on SQL Server, but the GUID token is provider-portable — which is what lets the test suite run on SQLite. Honest trade-off, easy to swap.
- **Enum values serialize as strings** (`JsonStringEnumConverter`): `"status": "Sent"` beats `"status": 1` for every human who will ever read a payload or log.

---

## Part 5 — Data & performance

### 5.1 Indexing (every index leads with `TenantId` — because every query does)

| Index | Serves |
|---|---|
| `UX (TenantId, InvoiceNumber)` unique | Per-tenant number uniqueness (the generator's backstop) + direct lookup |
| `(TenantId, Status)` | Status filter; summary `GROUP BY` |
| `(TenantId, DueDate)` | Overdue predicate |
| `(TenantId, IssueDate)` | Date-range filters |
| `(TenantId, CreatedAtUtc)` | Default newest-first sort |
| `(InvoiceId)` on line items | Detail fetch |

Deliberately **not** indexed: `CustomerName` search — a b-tree can't serve `contains` (leading wildcard); the honest production answer is full-text search or normalized search columns, listed as future work rather than a fig-leaf index.

### 5.2 Query discipline

- List: `AsNoTracking` + projection to a slim DTO (no line items loaded), `COUNT` + page fetch. No change tracking cost, no over-fetch.
- Detail: one query with line items.
- Summary: two aggregate queries total. No N+1 anywhere — the classic EF failure mode this design structurally avoids (persisted totals mean nothing ever iterates invoices loading children).

### 5.3 Known performance trade-offs (chosen, documented)

- **Random GUID clustered PK** fragments the clustered index under insert load. At scale: GUIDv7/`NEWSEQUENTIALID()`, or cluster on `(TenantId, CreatedAtUtc)` with the GUID as a nonclustered PK. Not needed at assessment scale; flagged so it's visibly a known, not an unknown.
- **Offset pagination** (`OFFSET/FETCH`) degrades on deep pages (the DB still scans skipped rows). Keyset pagination (`WHERE (CreatedAtUtc, Id) < (@cursor...)`) is the upgrade path and is why the stable sort tie-break already exists.
- **Summary sums across currencies.** Correct while a tenant invoices in one currency; a multi-currency tenant needs per-currency grouping. Documented limitation rather than silent wrongness.

---

## Part 6 — Validation & error-handling philosophy

- **Boundary layer (FluentValidation):** name ≤200, optional valid email ≤320, currency `^[A-Za-z]{3}$` (normalized uppercase in the domain), `DueDate ≥ IssueDate`, tax 0–100, notes ≤2000, 1–100 line items, description ≤500, `0 < qty ≤ 1M`, `0 ≤ price ≤ 1B`. Bounds aren't bureaucracy: they cap payload size and rule out absurd arithmetic.
- Invoked **explicitly in the controller** (not auto-validation middleware): the flow is visible, debuggable, and returns the standard `ValidationProblem` dictionary shape.
- **Domain layer** re-guards invariants (see §2.1 for why the duplication is intentional).
- **Exception→HTTP mapping is centralized** in one `IExceptionHandler`. Services throw meaningful exceptions (`NotFoundException`, `DomainException`); nothing else in the codebase knows HTTP status codes. 500s log full detail via Serilog and return a generic ProblemDetails — internals never leak to clients.

---

## Part 7 — Testing strategy

**Principle applied:** test the rules that lose money or leak data; skip tests that restate the framework.

| Suite | The business rules it pins |
|---|---|
| `InvoiceStatusTransitionTests` | Every legal transition; illegal ones (incl. from terminal states) throw; same-status rejected; overdue derivation truth table |
| `InvoiceCalculationTests` | Totals math; per-line rounding (3 × 0.335 → 1.01); zero tax; creation invariants; currency normalization |
| `TenantIsolationTests` | Stamping on save; filter scoping; cross-tenant 404; number uniqueness scope; save-without-tenant throws |
| `CreateInvoiceRequestValidatorTests` | Boundary rules via `FluentValidation.TestHelper` |

Not written (deliberately): controller tests that would only re-test ASP.NET model binding, and trivial mapper tests. That's the "meaningful over coverage" instruction taken literally.

**SQLite in-memory as the test DB** — the most contestable choice, so know it cold:

- *Why:* `dotnet test` works with zero infrastructure, locally and in the CI workflow, out of the box. EF's InMemory provider was rejected because it's not relational (no FK/unique-constraint enforcement — it would fake exactly the guarantees we test).
- *Cost:* SQLite ≠ SQL Server (decimal ordering/`SUM` semantics differ). Tests deliberately avoid provider-sensitive assertions.
- *Upgrade path:* Testcontainers spinning real SQL Server behind `WebApplicationFactory` (Program is already `public partial` for exactly this) — first item on the testing roadmap.

---

## Part 8 — Production considerations (Azure)

### 8.1 Target topology

- **Compute:** Azure App Service (Linux container) — or Container Apps if scale-to-zero/multi-service is expected. **Not AKS**: one stateless API doesn't justify cluster operations.
- **Data:** Azure SQL Database (serverless → S-tier). PITR, automated backups, built-in HA — undifferentiated heavy lifting we shouldn't own. Elastic pools if tenant/db count grows.
- **Secrets:** Key Vault + **Managed Identity** — no connection strings in config, CI, or env vars; the app authenticates to SQL/Key Vault as itself.
- **Observability:** Serilog is already structured, so the App Insights sink is one line: request traces, SQL dependency timing, failure rates, custom events (status transitions are the obvious business metric). Alerts: p95 latency, 5xx rate, DTU/CPU.

### 8.2 Delivery & rollback

- CI (included) extends to: build container → deploy to **staging slot** → smoke test (`/health` + one tenant-scoped call) → **slot swap**. Rollback = swap back, seconds.
- **EF migrations run as an explicit pipeline step** — never on production startup (the `InitializeOnStartup` flag is Development-gated in code, not just config). Startup migrations are a classic outage: N instances racing to migrate on deploy.
- Migrations follow **expand/contract**: each release's schema is compatible with the previous app version, so app rollback never demands schema rollback.

### 8.3 Scalability model

- API is **stateless** (tenant context is per-request scoped) → horizontal scale-out is trivial; `/health` already serves the probes.
- The database is the real ceiling. Mitigation ladder, cheapest first: the tenant-led indexes → covering `INCLUDE` columns on hot list queries → short-TTL per-tenant cache for the summary → read replica for dashboard/list traffic → elastic pool / move hot tenants to dedicated DBs (which the row-level design permits per-tenant).

### 8.4 Security posture (what exists vs. what's stated)

Implemented: tenant isolation with fail-closed writes and 404 opacity; parameterized-everything via EF (zero raw SQL); whitelisted sorting; clamped paging; bounded payloads; non-enumerable GUID ids; server-side generation of anything derivable; generic 500s with details only in logs; non-root container.
Explicitly deferred (stated, not hidden): real authN/Z (JWT/Entra), rate limiting (ASP.NET Core rate-limiter middleware), Tenants table + FK, audit trail of who changed status. Naming what's missing is itself the production-minded move — the dangerous gaps are the undocumented ones.

---

## Part 9 — Decisions most likely to be challenged (rapid-fire defense)

1. **"Why no repository?"** → DbContext is one; `IInvoiceService` is the test seam; a wrapper hides EF's value and gets leaked anyway.
2. **"Header-based tenancy is insecure."** → Agreed, and documented as such; it's a stand-in for a JWT claim behind the same `ITenantProvider` seam — the swap touches one middleware.
3. **"Why isn't Overdue a status?"** → Stored state needing a cron to stay true is a drift bug factory; derived state is correct by construction. Notifications are events detecting the condition, not mutations of it.
4. **"SQLite tests don't prove SQL Server behavior."** → Correct; they prove domain/relational logic with zero infra. Testcontainers integration suite is the named next step; `Program` is already prepared for it.
5. **"GUID PKs fragment the clustered index."** → Known; GUIDv7/sequential or reclustering is the at-scale fix; non-enumerable ids across tenants were worth it now.
6. **"No committed EF migration?"** → Authored in an offline sandbox without the SDK; the generation command is one line in the README and `db/schema.sql` is the equivalent DDL. Also why local build/test verification is a required step before trusting the code.
7. **"Totals stored on the row — denormalization?"** → Safe only because line items are immutable post-creation; that pairing is deliberate. If draft editing arrives, recalculation lives in the same aggregate method that mutates lines.
8. **"Why can't I edit an invoice?"** → Issued invoices are legal documents; corrections are credit notes. Draft-only editing is the roadmap item, and the encapsulated aggregate is already shaped for it.

---

*If any explanation above doesn't match your own reasoning, change the code or the doc until they agree — the interview risk isn't a wrong decision, it's a decision you can't own.*
