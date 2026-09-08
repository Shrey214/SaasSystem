# Conventions

Rules every service follows. Written before the first service exists, so
service #12 looks like service #1.

Source of truth for *what* we are building: `Goal/`. Source of truth for
*how*: this file plus `docs/adr/`.

---

## 1. Repository layout

```
HotelSaas/
├── Goal/                    the original brief (Saas, Domain, TechStack,
│                            LearningGoal, Plan) - read-only history
├── docs/
│   ├── 00-conventions.md    this file
│   ├── 0X-*.md              design docs, one per stage/service
│   ├── adr/                 architecture decision records
│   └── learning/            what broke, what we tried, what we chose
├── src/
│   ├── BuildingBlocks/      shared libraries (no business logic ever)
│   └── services/<name>/     one folder per service
├── tests/                   test projects, mirroring src
└── infra/docker/            compose files, init scripts, gateway config
```

`Goal/` is not edited to reflect implementation drift. If reality diverges
from the brief, the divergence is recorded in an ADR.

## 2. Service layout

Most services are four projects; a few are fewer. The tier is set per
service by [ADR-0009](adr/0009-right-size-clean-architecture-per-service.md)
and listed in `docs/05-code-structure.md` §2.4. The dependency arrows
point inward at every tier.

```
src/services/<name>/
├── HotelSaas.<Name>.Api/              endpoints, DI, middleware
├── HotelSaas.<Name>.Application/      use cases, one folder per feature
├── HotelSaas.<Name>.Domain/           entities, value objects, rules
│                                      (no EF, no HTTP, no NuGet)
└── HotelSaas.<Name>.Infrastructure/   EF Core, repositories, outbox,
                                       HTTP clients, message consumers
```

Use cases are **vertical slices**: `Application/Businesses/RegisterBusiness/`
holds the command, the handler, the validator and the response together.
Not `Commands/`, `Handlers/`, `Validators/` split by kind.

`Domain` has no package references beyond the framework. If a domain rule
needs a database to be expressed, it is not a domain rule.

## 3. Database

One PostgreSQL instance locally, **one database per service**, named
`hs_<service>` — `hs_tenant`, `hs_property`, `hs_booking`.

- No cross-database queries. No foreign keys between services. Ever.
- A service reads another service's data only via API call or by
  projecting from events into its own tables.

### Naming

| Thing | Convention | Example |
|---|---|---|
| table | snake_case, plural | `room_types` |
| column | snake_case | `check_in_time` |
| primary key | `id` | `id` |
| foreign key (same service) | `<singular>_id` | `room_type_id` |
| reference to another service | `<singular>_id` + no FK constraint | `property_id` |
| index | `ix_<table>_<cols>` | `ix_bookings_tenant_id_status` |
| unique index | `ux_<table>_<cols>` | `ux_rooms_property_id_room_number` |
| check constraint | `ck_<table>_<rule>` | `ck_bookings_dates_ordered` |

### Required columns

Every entity table:

```sql
id          uuid        primary key,     -- v7, generated in .NET
created_at  timestamptz not null default now(),
updated_at  timestamptz not null default now()
```

Internal log and queue tables use `id bigint generated always as identity`
instead. Which key type a table gets is decided by
[ADR-0010](adr/0010-key-strategy-uuidv7-bigint-natural.md), not by
habit — `uuid` everywhere would put 16-byte keys on the highest-volume,
append-only tables in exchange for properties they never use.

Every **tenant-scoped** table adds `tenant_id uuid not null`, and it is the
**first column of the primary index**. Every **property-scoped** table also
adds `property_id uuid not null`.

Aggregate roots that can be updated concurrently add `version integer not null`
for optimistic concurrency (`xmin` is not used — it does not survive a
logical restore).

### Types

| Concept | Type | Why |
|---|---|---|
| entity identifier | `uuid` (v7) | time-ordered so it indexes like a sequence; unique across all 14 databases with no coordination; not enumerable in a URL |
| internal log/queue key | `bigint` identity | outbox, processed_messages, idempotency_keys, error_logs, audit, reporting read models — never exposed, never cross-referenced, highest volume, and a monotonic sequence is useful for ordered draining |
| reference/lookup key | natural key | `currencies.code = 'INR'`, not a surrogate. One fewer join on every price query |
| money | `numeric(18,4)` + `currency char(3)` | never `float`, never `money`. Amount and currency always travel together |
| instant | `timestamptz` | stored UTC, rendered in the property's timezone |
| **stay night** | `date` | a hotel night is a calendar date, not an instant. Arrival 20 Sept means the night of the 20th regardless of check-in clock time |
| enum | `text` + check constraint | readable in `psql`, no migration to add a value; the C# side is a real enum |
| json blob | `jsonb` | only for genuinely open-ended config (theme, SEO). Never for anything queried by a business rule |

### Per-service infrastructure tables

Every service that publishes events gets an `outbox_messages` table.
Every service with an externally-triggered write gets `idempotency_keys`.
Shapes are defined in `BuildingBlocks` at Stage 3, identical everywhere.

## 4. Tenant isolation

Three layers, all mandatory:

1. `tenant_id` is read from the validated JWT via `ITenantContext`. It is
   **never** accepted from a route, query string or request body.
2. EF Core global query filter on every tenant-scoped entity, applied by
   convention in `BuildingBlocks`, not per-`DbContext`.
3. PostgreSQL row-level security on hot tables (added later as
   defence-in-depth, not as the primary mechanism).

Every service's test suite contains a test asserting that tenant A's token
cannot read tenant B's rows. A service without that test is not done.

## 5. HTTP API

- Route prefix `/api/v1/...`. Version in the path, bumped only on a
  breaking change.
- Errors are RFC 9457 `application/problem+json`, produced in one place.
  No service invents its own error shape.
- Validation failures: `400` with per-field detail. Authorization
  failures: `403`. Missing or cross-tenant resource: `404`, never `403`
  — a `403` confirms the resource exists, which leaks across tenants.
- Every state-changing request accepts an `Idempotency-Key` header, and
  every externally-triggered one requires it.
- Lists are cursor-paginated (`?cursor=&limit=`), not offset-paginated —
  offsets drift under concurrent inserts.
- Times in payloads are ISO-8601 with an offset. Stay dates are plain
  `YYYY-MM-DD`.

## 6. Events

Name: `<service>.<aggregate>.<past-tense-verb>.v<n>` —
`booking.reservation.confirmed.v1`, `property.room.blocked.v1`.

Every message carries the same envelope:

```
message_id      uuid      unique, the consumer's idempotency key
correlation_id  uuid      the originating request, propagated everywhere
causation_id    uuid      the message that caused this one
occurred_at     timestamptz
tenant_id       uuid
event_type      text      the name above
payload         jsonb
```

Rules:

- Published only through the transactional outbox — never directly from a
  request handler, or a rollback will publish a lie.
- Consumers are idempotent. Assume every message arrives at least twice
  and out of order.
- Events carry the facts a consumer needs, not just an id, so the
  consumer does not have to call back synchronously.
- A new version is a new event name. Existing events are never reshaped.

## 7. Tests

- `tests/HotelSaas.<Name>.Tests` — domain rules, no infrastructure.
- `tests/HotelSaas.<Name>.IntegrationTests` — Testcontainers PostgreSQL,
  real migrations, real HTTP through `WebApplicationFactory`. No mocked
  database, no in-memory provider: the in-memory provider does not
  enforce constraints, and constraints are where booking bugs die.
- Tests are named `Method_Scenario_ExpectedOutcome`.

## 8. Git

Branch from `main`, one branch per stage: `feat/stage-04-tenant-service`.

Commit subjects follow `type(scope): subject`, scope = service or area:

```
feat(tenant): register business with idempotent email
fix(booking): release hold when payment webhook arrives late
docs(adr): record availability ownership decision
chore(infra): add postgres init script
```

Types: `feat`, `fix`, `refactor`, `test`, `docs`, `chore`, `perf`.

Each finished stage is tagged `stage-<nn>`, so any stage can be checked
out and run as it was.

Identity for this repository is set **locally**, not globally, so it never
mixes with a work account:

```
git config --local user.email shreyashporwal10@gmail.com
```

## 9. ADRs

One file per decision: `docs/adr/NNNN-short-title.md`.

```markdown
# NNNN. Title

Date: YYYY-MM-DD
Status: Accepted | Superseded by NNNN

## Context
The forces at play. What made this a decision rather than a default.

## Decision
What we are doing.

## Consequences
What this makes easy, what it makes hard, what we now owe.

## Alternatives rejected
Each option and the specific reason it lost.
```

The rejected alternatives are the point. A year from now the question is
never "what did we do" — the code says that — it is "what did we already
think about".

## 10. Learning notes

One per stage in `docs/learning/`. Not a summary of the code: a record of
the problem that was hit, what was tried, what actually fixed it, and what
would break at ten times the load. This is the reason the project exists.
