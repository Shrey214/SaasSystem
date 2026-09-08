# 02 — Service Boundaries

Phase 3 of `Goal/Saas.txt` §31, continued from `01-bounded-contexts.md`.

A bounded context is a **language** boundary. A service is a **deployment
and failure** boundary. They are not the same thing, and mapping them 1:1
by reflex is how projects end up with forty services and a distributed
monolith.

---

## 1. The test we applied

For each pair of contexts we asked five questions. Any single "yes" to the
first two forces them together; the rest are judgement.

1. **Must they commit in the same database transaction?**
   If yes, they are one service. This is absolute. A distributed
   transaction is not an acceptable answer to a consistency requirement
   that a single `BEGIN` would have solved.
2. **Would splitting them put a network hop on a lock-holding path?**
   If yes, they are one service.
3. Do they fail for different reasons? (external gateway, message broker,
   heavy read load)
4. Do they change on different rhythms?
5. Do they scale differently?

The result is **14 services**, from ~30 functional modules in
`Goal/Saas.txt` §28. The compression is where the design work is.

---

## 2. The services

| # | Service | Database | Owns |
|---|---|---|---|
| 1 | `identity` | `hs_identity` | users, credentials, refresh tokens, tenant memberships, roles, permissions, role↔permission, user↔property access, invitations, platform admins, signing keys |
| 2 | `tenant` | `hs_tenant` | businesses, business profiles, lifecycle history, verification tokens |
| 3 | `subscription` | `hs_subscription` | plans, features, plan limits, subscriptions, trials, usage counters, SaaS invoices, SaaS payment records |
| 4 | `property` | `hs_property` | properties, settings, policies, buildings, floors, room types, rooms, beds, amenities, room blocks, operational status, media metadata |
| 5 | `pricing` | `hs_pricing` | rate plans, meal plans, seasons, rate calendar, occupancy rules, restrictions, tax rules, promotions, coupons, redemptions |
| 6 | `guest` | `hs_guest` | customers, guests, identity documents, preferences, stay-history index |
| 7 | `booking` | `hs_booking` | **capacity ledger, holds, bookings**, booking rooms/nights, price snapshots, guest snapshots, cancellation records |
| 8 | `payment` | `hs_payment` | payments, attempts, gateway events, refunds, reconciliation runs |
| 9 | `stay` | `hs_stay` | stays, room assignments, folios, charges, invoices *(lives inside `booking` until Stage 14)* |
| 10 | `operations` | `hs_operations` | housekeeping tasks, room cleanliness state, maintenance issues, work orders |
| 11 | `notification` | `hs_notification` | templates, channel preferences, delivery attempts, dead letters |
| 12 | `reporting` | `hs_reporting` | read models: occupancy, revenue, ADR, RevPAR, cancellations, no-shows |
| 13 | `audit` | `hs_audit` | append-only audit entries |
| 14 | `content` | `hs_content` | website config, themes, CMS content, custom domains, domain→property map, reviews |

Plus `src/BuildingBlocks/` — shared libraries only. **No business logic
ever lives there.** A shared library with a business rule in it is a
shared database with extra steps: change it and all 14 services must
redeploy together, which is the one thing microservices are supposed to
prevent.

---

## 3. Merges, justified individually

### Availability + Holds + Bookings → `booking`

The most consequential decision in the project. Recorded as **ADR-0002**.

`availability = capacity − confirmed − held − blocked`. Creating a hold
must read that number and decrement it **atomically**, or two customers
get the same last room. If availability lived in its own service, every
hold would be a distributed transaction on the single hottest, most
race-prone path in the system — a two-phase commit, or a saga that can
briefly oversell.

Test 1 and test 2 both answer yes. They are one service.

The read side is different: *searching* availability is high-volume and
tolerates staleness (§2.7 of `01-bounded-contexts.md`). That is solved
with a cache at Stage 13, not with a separate service.

### Housekeeping + Maintenance → `operations`

Same shape: a task with a lifecycle, assigned to staff, on a room, whose
completion changes whether the room can be sold. Same actors, same
screens, same event consumers, same scaling profile. Splitting them would
create two services that differ only in a `reason` column.

### Website + Reviews → `content`

Both are public-facing content with a moderation/publish step, both are
read-dominated and cacheable, both are driven by the same manager
screens. Reviews depend on a completed booking, but only through an
event — never a query.

### Plans + Entitlements + SaaS Billing → `subscription`

Entitlement is a pure function of plan and usage; splitting it from plans
means a network hop to answer "what is the limit". SaaS billing shares
the same subscription lifecycle. One service, three modules.

### Stay & Folio → starts inside `booking`, extracted at Stage 14

Check-in mutates booking state, and folio charges reference booking
rooms. Splitting it on day one means a distributed transaction across the
check-in path before we have any of the tooling to handle one.

So it starts as a module inside `booking` with **its own schema and no
foreign keys crossing into booking tables**, then gets extracted at Stage
14 as a deliberate exercise. The no-FK constraint from day one is what
makes that extraction possible later instead of theoretical.

---

## 4. Splits we rejected

| Proposed split | Why rejected |
|---|---|
| A separate `availability` service | See above: distributed transaction on the hottest path. |
| A separate `hold` service | A hold *is* a decrement of the capacity ledger. Same transaction. |
| `audit` logic inside every service | Audit must be tamper-evident and queryable across tenants and services. Thirteen local audit tables cannot answer "what did this user do today". Audit is a consumer, and each service simply emits truthful events. |
| A separate `search` service | Premature. Availability search is a query against `booking`'s own ledger, cached at Stage 13. Revisit only if search traffic actually dwarfs booking traffic. |
| Splitting `identity` into auth + authz | Both read the same user, role and property-access tables in the same transaction when issuing a token. Test 1 says no. |
| `media` / file storage service | Only metadata is ours; the bytes go to disk now and object storage later. A service to hold a URL is not a service. |
| A per-tenant service instance | Defeats the entire point of multi-tenancy and does not scale past a handful of customers. |

---

## 5. Who depends on whom

**Synchronous** (caller blocks; failure is visible to the user):

| Caller | Callee | Why it must be synchronous | If callee is down |
|---|---|---|---|
| `property` | `subscription` | "may this business create another property?" must be answered before the property exists | cached entitlement, then fail closed — Stage 7 builds exactly this, and breaks it on purpose |
| `booking` | `pricing` | an exact, current quote at commit time; a stale price is a financial error | fail the request. Booking at a guessed price is worse than not booking |
| `booking` | `payment` | needs a gateway order id to hand back to the browser | fail the request; the hold expires on its own |
| `stay` | `booking` | front desk looking up a booking by reference | degraded: today's arrivals are already projected locally |
| `stay` | `pricing` | late-checkout and extra-service charges | fall back to the configured default charge |

Five synchronous calls in the whole system. Everything else is events.

**Asynchronous** (events, via RabbitMQ, published through each service's
outbox):

| Publisher | Consumers | Carries |
|---|---|---|
| `tenant` | `subscription`, `identity`, `reporting`, `audit` | business registered / activated / suspended |
| `subscription` | `property`, `reporting`, `notification`, `audit` | subscription changed, limits changed, expired |
| `property` | **`booking`**, `content`, `operations`, `reporting`, `audit` | room added/removed, room blocked/unblocked, operational status changed, property activated |
| `pricing` | `booking`, `content`, `audit` | restriction changed, closed dates changed |
| `booking` | `payment`, `stay`, `guest`, `operations`, `notification`, `reporting`, `audit` | held, confirmed, cancelled, no-show, modified |
| `payment` | `booking`, `stay`, `notification`, `reporting`, `audit` | captured, failed, refunded |
| `stay` | `operations`, `booking`, `notification`, `reporting`, `audit` | checked in, checked out, charge added, invoiced |
| `operations` | `property`, `booking`, `notification`, `reporting`, `audit` | cleaning completed, room inspected, maintenance opened/resolved |
| `identity` | all services | access revoked, permissions changed |
| `content` | `reporting`, `audit` | review published |

Note what is **absent**: `booking` never calls `property`. It holds its
own capacity ledger, built by translating property's room events. That
absence is the single most important line in this document — it is what
makes the booking path survive `property` being down, and it is why
`01-bounded-contexts.md` §2.1 mattered.

### The one chain worth tracing

A room breaks. Property owns operational status
(`01-bounded-contexts.md` §2.2), so:

```
manual case:  manager → property.MarkOutOfOrder
                     → property.room.blocked.v1 → booking decrements capacity

from a work order:  operations.maintenance.opened.v1
                     → property consumes, sets OUT_OF_ORDER
                     → property.room.blocked.v1 → booking decrements capacity
```

Two paths, one owner, one event that `booking` reacts to. The second path
is eventually consistent: for a few hundred milliseconds the room is
still sellable. That is acceptable for maintenance and unacceptable for a
hold — which is exactly why holds are local and this is not.

---

## 6. Build and dependency order

Nothing depends on a service built later, which is what makes the stage
order in `Goal/Plan.txt` runnable at every point:

```
identity ─┐
tenant ───┼─→ subscription ─→ property ─→ pricing ─┐
                                    │              │
                                 guest ────────────┼─→ booking ─→ payment
                                                   │       │
                                                   │       └─→ stay ─→ operations
                                                   │
                                        content ───┘
                          notification / reporting / audit  (pure consumers, any time)
```

`notification`, `reporting` and `audit` consume events and are called by
nobody, so they can be built whenever — which is why they sit late in the
plan without blocking anything.

---

## 7. When to revisit this

Boundaries are a hypothesis. These are the signals that would prove one
wrong, written down now so we notice them later instead of rationalising:

| Signal | Likely meaning |
|---|---|
| Two services are always deployed together | the boundary is wrong; merge them |
| A "read another service's data" API grows past a couple of endpoints | the data is on the wrong side |
| A saga needs more than 3 compensating steps | the transaction boundary is wrong |
| A service's tables are only ever read by another service | it is a table, not a service |
| Adding a field means changing 4 services | a shared concept is modelled in the wrong place |
| `booking` starts calling `property` synchronously | the capacity projection is incomplete — fix the projection, do not add the call |

Each one gets an ADR if it fires.

---

Next: `03-communication.md` (how these interactions actually work — outbox,
idempotency, the saga, retries and the event catalog) and
`04-data-ownership.md` (who owns which field, and what may be copied).
