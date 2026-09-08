# 04 — Data Ownership

Phase 5 of `Goal/Saas.txt` §31. The last document before tables.

One rule, and everything else follows from it:

> **Exactly one service may write a given fact. Everyone else holds a
> copy, and knows it is a copy.**

---

## 1. Database-per-service, mechanically

One PostgreSQL container locally. Inside it, one database per service,
and **one database role per service** that can reach only its own:

```
postgres (container)
├── hs_identity      owner: hs_identity_user
├── hs_tenant        owner: hs_tenant_user
├── hs_subscription  owner: hs_subscription_user
├── hs_property      owner: hs_property_user
├── hs_pricing       owner: hs_pricing_user
├── hs_guest         owner: hs_guest_user
├── hs_booking       owner: hs_booking_user
├── hs_payment       owner: hs_payment_user
├── hs_stay          owner: hs_stay_user     (created at stage 14)
├── hs_operations    owner: hs_operations_user
├── hs_notification  owner: hs_notification_user
├── hs_reporting     owner: hs_reporting_user
├── hs_audit         owner: hs_audit_user
└── hs_content       owner: hs_content_user
```

Separate roles matter more than separate databases. A shared superuser
would make "just join to the other service's table" a two-line change
that passes review; with per-service roles it is a permission error. The
boundary has to be enforced by something other than discipline, because
discipline loses to a deadline.

One instance is a **local development** choice, not an architectural one
(`Goal/TechStack.txt`: free while learning). Nothing in the code knows
the databases are co-located — each service has its own connection
string, its own `DbContext`, its own migration history. Moving one to its
own instance is a config change.

`hs_stay` does not exist until Stage 14. Until then, `stay` lives in
`hs_booking` **in its own schema**, `stay.*`, with no foreign key
crossing into `booking.*`. That constraint is the entire reason the
Stage 14 extraction will be possible.

---

## 2. Ownership register

The owner is the only writer. "Copied by" means that service holds a
replica it maintains from events.

| Fact | Owner | Copied by | Copy type |
|---|---|---|---|
| user, credentials, refresh token | `identity` | — | — |
| role, permission, property access | `identity` | (all, as JWT claims) | token claim |
| business identity + lifecycle | `tenant` | `subscription`, `reporting` | projection |
| plan, limits, subscription state | `subscription` | `property` (entitlement) | **cache, with TTL** |
| property config, timezone, currency | `property` | `booking`, `content`, `reporting` | projection |
| room type definition | `property` | `booking`, `content` | projection |
| physical room | `property` | `booking` (as capacity), `operations` | projection |
| room operational status | `property` | `booking` | projection |
| room cleanliness | `operations` | `property` (display only) | projection |
| rate plan, rate calendar, tax, promo | `pricing` | — | — |
| restriction (min/max stay, closed) | `pricing` | `booking` | projection |
| **the price of a specific booking** | `booking` | `stay`, `reporting` | **immutable snapshot** |
| customer account | `guest` | `booking` | snapshot (id + name) |
| guest person + documents | `guest` | `booking` | snapshot (name only) |
| **capacity, holds, bookings** | `booking` | `reporting` | projection |
| cancellation policy config | `property` | `booking` | **snapshot at confirm** |
| payment, refund | `payment` | `booking`, `stay`, `reporting` | projection (status only) |
| stay, folio, charge, invoice | `stay` | `reporting` | projection |
| housekeeping / maintenance task | `operations` | `reporting` | projection |
| website config, domain map, review | `content` | `reporting` | projection |
| notification delivery | `notification` | — | — |
| audit entry | `audit` | — | — |

---

## 3. Three kinds of copy — do not mix them up

Most data-consistency bugs in a system like this come from treating one
of these as another.

### Snapshot — frozen on purpose, never updated

The price the guest agreed to. The cancellation policy in force at the
time of booking. The guest's name as given at booking.

These are **historical facts**, not references. When `pricing` raises the
Deluxe rate tomorrow, the confirmed booking must not change
(`Goal/Domain.txt` Part 3, *Lock Price at Booking*). When the property
edits its cancellation policy, bookings made under the old one keep the
old terms — with `policy_version` stored so a refund can be justified
years later.

A snapshot is never refreshed. Refreshing one is a bug, not an
improvement.

### Projection — eventually consistent, maintained from events

`booking`'s capacity ledger, built from `property.room.*`. `reporting`'s
read models. `content`'s copy of amenities for the website.

Rules: rebuildable from the event stream alone; never written by anything
but its event handlers; and never treated as authoritative for a decision
the owner should be making. `booking` decides capacity from its own
ledger — that *is* its authority — but it would never decide whether a
room number is valid, because that is `property`'s to answer.

### Cache — a copy with an expiry and a fallback

`property`'s copy of subscription entitlements. Availability search
results at Stage 13.

Rules: always has a TTL; always has a defined behaviour when absent
(fail closed for entitlements, recompute for search); and never used on a
path where being wrong is unacceptable. A cached entitlement may let one
extra property through in a race — recoverable. A cached capacity number
must never be allowed to create a booking.

---

## 4. No foreign keys across services

`booking.property_id` has no `references` clause, because the row it
points at is in another database. So integrity has to come from
somewhere else:

| Risk | How it is handled |
|---|---|
| booking references a property that never existed | `booking` only ever learns `property_id` from a `property` event, or from a token claim already validated against the user's property access |
| property deleted while bookings exist | **properties are never deleted.** `ARCHIVED` is a terminal state (`Goal/Saas.txt` §4), and `Goal/Domain.txt` Part 1 requires historical bookings to stay intact |
| room removed while booked | `property.room.removed.v1` lowers capacity; existing bookings are unaffected because they hold a *room type*, not a room (`01-bounded-contexts.md` §2.1). Front desk reassigns at check-in |
| guest deleted, bookings remain | booking holds a name snapshot; erasure redacts the snapshot, keeping the financial record |
| the event that would have created a projection row is lost | projections are rebuildable; every consumer can replay from the publisher's outbox history |
| an event arrives before its dependency | consumers tolerate out-of-order arrival (`03-communication.md` §2) — a room event for an unknown room type creates the type as a stub and reconciles later. Never crash, never drop |

Within a service, foreign keys are used **aggressively** — `not null`,
`on delete restrict`, real check constraints. Local integrity is free;
there is no reason to give it up just because global integrity is not.

---

## 5. Tenant isolation

Three layers, per `docs/00-conventions.md` §4. Mechanically:

**Layer 1 — context.** `tenant_id` is read from validated JWT claims into
a request-scoped `ITenantContext`. It is never accepted from a route,
body or query string. A platform admin token carries no `tenant_id`, and
platform-scoped queries take an explicit, audited code path rather than a
nullable tenant filter — a `where tenant_id = @t or @t is null` is one
typo away from leaking everything.

**Layer 2 — query filter.** Applied by convention in `BuildingBlocks` to
every entity implementing `ITenantScoped`, so a new entity is filtered
because of what it implements, not because someone remembered. Writes
are covered too: `tenant_id` is stamped in `SaveChanges`, never set by
a handler.

**Layer 3 — row-level security.** On the tables where a leak would be
worst (`bookings`, `guests`, `payments`), Postgres RLS with the tenant
set per connection. Defence-in-depth: it catches raw SQL, a bad Dapper
query, and a `DbContext` misconfiguration — the exact places layer 2
cannot reach.

**Indexes.** `tenant_id` is the **first column** of every index on a
tenant-scoped table. Not for correctness — for the query plan. An index
on `(status)` in a table holding 200 businesses' bookings makes every
tenant's query scan every tenant's rows.

**The test.** Every service's suite contains "tenant A's token cannot
read tenant B's rows", written against the HTTP surface, not the
repository. A service without that test is not finished.

---

## 6. Identifiers

**UUID v7 for entity tables** — anything referenced across services or
addressable in a URL. Time-ordered, so it indexes like a sequence instead
of fragmenting a B-tree the way v4 does; globally unique, so `booking`
can mint an id without asking anyone; assigned in the constructor, so an
aggregate can raise a domain event carrying its own id before the row
exists; and it does not leak volume the way `/bookings/1043` does.

**`bigint` identity for internal log and queue tables** — outbox,
processed messages, idempotency keys, error logs, audit entries,
reporting read models. Never exposed, never referenced from another
service, highest volume in the system, and the sequence gives ordered
draining for free.

**Natural keys for reference data** — `currencies.code = 'INR'`, not a
surrogate id nobody can read.

Full reasoning and the rejected options in
[ADR-0010](adr/0010-key-strategy-uuidv7-bigint-natural.md).

**No shared sequences.** A sequence is a coordination point, and
coordination between services is what we are trying to avoid.

**Human-facing references** are a separate concern from primary keys.
A booking reference is what a guest reads over the phone:

```
PB-2609-K7M4QX
│  │    └── 6 chars, Crockford base32 (no I, L, O, U — misread aloud)
│  └─────── year+month, so staff can date it at a glance
└────────── per-property prefix
```

Generated randomly with a unique index and a bounded retry on collision —
**not** from a counter. A counter is a contention point under concurrent
booking (`Goal/Domain.txt` Part 6, *Booking Number Generation*), and a
guessable reference is an enumeration attack on other guests' bookings.

**Reference data** — currency codes, country codes, timezones — is not
a service. It is a small versioned table with a **natural primary key**,
seeded by migration in each service that needs it. A service to answer "is INR a currency" is a
network hop in exchange for nothing.

---

## 7. Personal data

`guest` holds the most sensitive data in the system: names, phone
numbers, passport and ID document numbers, nationality.

- Document numbers are stored **encrypted at column level**, with the key
  supplied by configuration, never in the database. `Goal/Domain.txt`
  Part 7 requires restricted access; a database dump should not be a
  passport dump.
- Access to document fields is a distinct permission from viewing a guest,
  and every read of them is audited. Reading a document number is an event
  worth recording; reading a guest's name is not.
- PII lives in `guest`, and only the minimum travels: `booking` keeps the
  guest's **name** for the front desk, never their documents.

**Erasure vs. retention.** These conflict, and the resolution is written
down now rather than improvised later:

| Data | On an erasure request |
|---|---|
| guest profile, documents, preferences | deleted |
| booking financial record | **kept** — legally required, and it is our own accounting |
| name on the booking snapshot | redacted to initials |
| audit entries | kept; they record an action, not a person's details |

The financial record survives without identifying anyone. That is the
whole trick.

---

## 8. Archival

`Goal/Domain.txt` Part 1: *"Historical bookings must remain intact."*

Property lifecycle ends at `ARCHIVED`, never `DELETED`. An archived
property stops accepting bookings, disappears from search and from the
property switcher, and keeps every row.

This is exactly why `booking` snapshots the property name. Three years
later, a report over an archived property still renders — with no
surviving call to `property` at all.

---

## 9. Reporting

`reporting` owns **only** derived read models, built from events, and is
authoritative for nothing. Its tables are shaped by the question, not by
the domain: pre-aggregated occupancy by property-date, revenue by
property-month.

It is allowed to be behind. Occupancy that is thirty seconds stale is a
correct dashboard; a booking that is thirty seconds stale is a double
booking. That asymmetry is precisely why reporting is a separate service
and availability is not (`02-service-boundaries.md` §3).

Any projection can be rebuilt from zero by replaying events. That is not
a disaster-recovery story — it is the normal way a new report gets its
history.

---

## 10. What is now settled

Phases 3, 4 and 5 are complete. Before a single table exists we have
decided:

- 14 contexts, defined by where a word changes meaning
- 14 services, with every merge and rejected split justified
- 5 synchronous calls in the entire system; everything else is events
- outbox for publishing, three separate idempotency mechanisms
- the booking saga, including the late-capture branch
- one writer per fact, and three distinct kinds of copy
- no cross-service foreign keys, with integrity handled per-risk
- tenant isolation in three enforced layers
- uuid v7 keys, random human references, no shared sequences
- PII confined to `guest`, with erasure and retention reconciled

Next: **Phase 6–7 — table design**, per service, starting with `tenant`
at Stage 4. And the ADRs in `docs/adr/` record why each of the above beat
its alternative.
