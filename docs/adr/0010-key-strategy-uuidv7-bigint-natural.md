# 0010. Key strategy: uuid v7 for entities, bigint for internal log tables, natural keys for lookups

Date: 2026-09-08
Status: Accepted
Amends: `docs/00-conventions.md` §3 and `docs/04-data-ownership.md` §6,
which both said uuid v7 for *every* primary key

## Context

`bigint identity` is smaller (8 bytes vs 16), sequential, human-readable
in `psql`, and cheaper in every foreign key and index. For a
single-database application it is still the right default in 2026, and
any claim otherwise is cargo cult.

`uuid` was historically a poor primary key for a real reason: **v4 is
random**, so inserts land at random points in a B-tree, causing page
splits, poor cache locality and WAL amplification. Benchmarks showing
UUIDs are "slow" are almost always measuring v4.

**v7 removes that objection.** RFC 9562 (2024) defines a
millisecond-timestamp prefix, so v7 values sort by creation time and
insert at the right-hand edge of the index, like a sequence. .NET 10 has
`Guid.CreateVersion7()` natively; no library needed.

So the question is not "uuid or int" globally. It is which properties
each *table* actually needs.

## Decision

Three key types, chosen by what the table is for.

### uuid v7 — entities

Aggregate roots and any row that is referenced across services or
appears in a URL: `businesses`, `properties`, `rooms`, `bookings`,
`holds`, `payments`, `guests`, `folios`.

Three reasons specific to this architecture, not to fashion:

1. **The id must exist before the row does.** Our aggregates raise domain
   events inside themselves, and those events carry the aggregate id:

   ```csharp
   var booking = Booking.Create(...);   // id assigned in the constructor
   // raises BookingConfirmed(booking.Id, ...) immediately
   ```

   With an identity column the aggregate does not know its own id until
   after `SaveChanges`, so the outbox row cannot be written in the same
   transaction from inside the domain
   ([ADR-0006](0006-outbox-at-least-once-idempotent-consumers.md)). That
   pattern is load-bearing here, and identity columns break it.

2. **Fourteen databases, no shared sequence.** `booking` stores
   `property_id` with no foreign key
   ([ADR-0003](0003-database-per-service-single-instance.md)). If both
   services used `bigint identity`, `property_id = 42` would be
   ambiguous across databases, environments and any future merge or
   shard. A uuid means the same value everywhere, forever.

3. **`/bookings/1043` is an enumeration attack.** Sequential ids in a
   multi-tenant product leak volume and invite IDOR, which
   [ADR-0004](0004-tenant-isolation-in-three-layers.md) names as a risk.

### bigint identity — high-volume, internal-only tables

`outbox_messages`, `processed_messages`, `idempotency_keys`,
`error_logs`, `audit_entries`, and `reporting`'s read models.

These are never exposed in an API, never referenced from another
service, and never appear in a URL. So they get the cheaper key — and in
the outbox's case, a **monotonic sequence is actively useful**, because
draining in id order is draining in creation order without relying on a
timestamp with clock-skew and equal-millisecond ties.

These tables are also the highest-volume in the system, which is exactly
where 8 bytes versus 16 in the index actually shows up.

`outbox_messages` keeps its uuid `message_id` **as well** — that is the
consumer's idempotency key and must be globally unique. The `bigint` is
the local ordering key. Two columns, two jobs.

### Natural keys — reference and lookup data

Currencies, countries, plan codes, role codes, permission codes,
amenity types:

```sql
create table currencies (
    code    char(3) primary key,     -- 'INR', not 42
    name    text not null,
    minor_units smallint not null
);
```

A surrogate key here forces a join to answer "what currency is this
booking in". `currency = 'INR'` is readable, stable, self-documenting,
and one fewer join on every price query. Reference data does not get a
surrogate key just because other tables have one.

### Child rows inside an aggregate

`booking_nights`, `folio_charges`, `availability_daily`: uuid v7 if they
are addressable in an API (`/bookings/{id}/charges/{chargeId}`), bigint
if they are pure line items only ever read through their parent.
Decided per table when the table is designed, and stated in that
service's design document.

## Consequences

- Not one rule, so `docs/00-conventions.md` §3 must state all three and
  which applies where. A single rule would have been easier to follow and
  wrong in two places.
- 16 bytes per key instead of 8, in the primary index and in every
  referencing column. Accepted on entity tables; avoided precisely where
  volume makes it matter.
- Debugging is worse: `where id = '01931f2e-8c4a-7d31-...'` instead of
  `where id = 42`. Mitigated by the human-facing reference that already
  exists for the case that matters — `PB-2609-K7M4QX` on a booking
  (`docs/04-data-ownership.md` §6) — and by v7 ids sorting chronologically,
  so `order by id` is `order by created_at` for free.
- **Verify once, at Stage 3:** that Npgsql writes `Guid` in RFC
  big-endian byte order, so PostgreSQL's `uuid` sort order matches
  creation order. It does, but this is the exact place other stacks get
  it wrong (SQL Server's `uniqueidentifier` sorts differently from the
  string form), and the whole benefit of v7 depends on it. It gets an
  integration test.
- Ids are generated in **application code**, not by the database.
  PostgreSQL 18 added `uuidv7()`, but we are on 17, and generating in
  .NET is what makes reason (1) above work anyway.
- Mixed keys mean a reviewer must know which tier a table is in. That is
  what this ADR is for.

## Alternatives rejected

**`bigint identity` everywhere.** The right answer for a monolith, and it
breaks all three reasons above: no id before insert, ambiguous
cross-service references across fourteen databases, and enumerable URLs
in a multi-tenant product.

**`uuid` v4 everywhere.** The version that earned UUIDs their bad
reputation. Random inserts fragment the B-tree, and index locality
collapses as the table grows. There is no reason to choose v4 over v7 now
that v7 is standardised and in the framework.

**uuid v7 everywhere** — what the docs originally said. Simple, and it
puts 16-byte keys on the outbox, audit and error tables, which are the
highest-volume and most append-heavy in the system, in exchange for
properties (external addressability, cross-service uniqueness) that those
tables never use.

**Hybrid: `bigint` clustered PK plus a separate public `uuid` column** on
entity tables. Genuinely the best-performing option, and standard advice
on SQL Server, where a random clustered key is catastrophic. Much weaker
here: PostgreSQL tables are heaps, so the primary key is just a unique
B-tree index and does not dictate physical row order. That removes most
of the benefit while adding a second key to every table, two lookups on
every external reference, and a real risk of leaking the internal id in
an API response. Rejected as a cost with no matching payoff on this
engine.

**ULID or Snowflake ids.** ULID is functionally equivalent to uuid v7
with a nicer text encoding, but has no native PostgreSQL or .NET type, so
it becomes a `char(26)` and loses tooling support. Snowflake needs
coordinated worker ids — machinery we would have to run for no gain over
v7.

**Composite `(tenant_id, id)` primary keys.** Attractive for tenant
isolation and genuinely used in some multi-tenant systems. Rejected:
every foreign key becomes two columns, every URL needs both, and
`tenant_id` is already the first column of every index
([ADR-0004](0004-tenant-isolation-in-three-layers.md)), which delivers
the query-plan benefit without the key complexity.
