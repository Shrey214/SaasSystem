# 0004. Tenant isolation in three enforced layers

Date: 2026-09-08
Status: Accepted

## Context

`Goal/Saas.txt` §30 Problem 6 states the requirement bluntly:

> Business A → GET /bookings → Must NEVER return Business B

`Goal/Domain.txt` Part 18 adds *Tenant Data Filtering*, *Property Data
Filtering* and *Resource Authorization* with "data leakage" and "IDOR" as
the named risks.

This is the one failure in a multi-tenant SaaS with no acceptable
recovery. A double booking is an apology; showing one hotel chain another
chain's revenue is the end of the product. So it needs more than one
mechanism, because any single mechanism will eventually be missed.

## Decision

Three layers, all mandatory.

**Layer 1 — the tenant comes from the token, only.** `tenant_id` is read
from validated JWT claims into a request-scoped `ITenantContext`. It is
never accepted from a route, query string, body or header. A handler
cannot ask for a different tenant, because there is no parameter through
which to ask.

Platform-admin tokens carry no `tenant_id` and take an explicit, audited
code path. There is no `where tenant_id = @t or @t is null` anywhere: one
typo in that expression leaks the entire platform.

**Layer 2 — a query filter by convention, not by memory.** Every entity
implementing `ITenantScoped` gets an EF Core global query filter applied
in `BuildingBlocks`, so a new entity is filtered because of the interface
it implements. `tenant_id` is stamped during `SaveChanges`, so a handler
cannot write a row into the wrong tenant either.

**Layer 3 — row-level security on the tables where a leak is worst.**
`bookings`, `guests`, `payments`, `folios`: Postgres RLS with the tenant
set per connection. This catches raw SQL, hand-written Dapper, a
misconfigured `DbContext` and a migration script — precisely the paths
layer 2 cannot see. Added incrementally, not on day one, and never
treated as the primary mechanism.

**Plus one non-negotiable test.** Every service's suite contains a test
asserting tenant A's token cannot read tenant B's rows, written against
the HTTP surface. A service without it is not finished.

**And one API rule:** a resource belonging to another tenant returns
`404`, never `403`. A `403` confirms the row exists, which is itself a
leak.

## Consequences

- `tenant_id` is the first column of every index on a tenant-scoped
  table. Not for correctness — for the query plan. An index on
  `(status)` across 200 businesses' bookings scans every tenant's rows.
- Platform-wide queries (admin business search, platform reports) need a
  deliberate bypass. That bypass is a small, named, audited surface, not
  a flag on the normal path.
- RLS adds a per-connection `set` and constrains connection pooling; it
  is applied selectively for that reason.
- Cross-tenant work that is genuinely legitimate — platform reporting —
  happens in `reporting` over event data, not by relaxing layer 2.

## Alternatives rejected

**Database per tenant.** The strongest isolation available, and rejected
on operations: 200 businesses × 14 services is 2,800 databases and
2,800 migrations per release. It also makes platform-wide reporting
almost impossible. Revisit only for a large enterprise customer who pays
for it.

**Schema per tenant.** Same migration explosion, weaker isolation, and
connection pools that cannot be shared.

**Filtering in the repository layer only.** One forgotten `.Where()` is a
breach. This is layer 2 without layers 1 and 3, and it is the most common
way real SaaS products leak.

**`tenant_id` as a request parameter.** Trivially tampered with. It is
IDOR by design.

**RLS alone.** Attractive — the database enforces everything — but it
puts the security boundary in a place the application cannot test easily,
interacts badly with pooling, and gives no help at all with *property*-level
access, which is a second dimension the token has to carry anyway.
