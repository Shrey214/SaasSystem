# Architecture Decision Records

One file per decision. Format in `docs/00-conventions.md` §9.

The **Alternatives rejected** section is the reason these exist. A year
from now the question is never "what did we do" — the code answers that.
It is "what did we already consider, and why did it lose".

| # | Decision | Status | The short version |
|---|---|---|---|
| [0001](0001-fourteen-services-not-one-per-module.md) | Fourteen services, not one per module | Accepted | A five-question test collapses ~30 modules into 14 services; a shared transaction or a lock-holding network hop forces a merge |
| [0002](0002-availability-owned-by-booking.md) | Availability, holds and bookings in one service | Accepted | The most consequential call in the project: a separate availability service would put a distributed transaction on the hottest, most race-prone path |
| [0003](0003-database-per-service-single-instance.md) | Database per service, one local instance, one role each | Accepted | Per-service PostgreSQL roles make a cross-service join a permission error rather than a code-review argument |
| [0004](0004-tenant-isolation-in-three-layers.md) | Tenant isolation in three enforced layers | Accepted | Token-only tenant context, filter by convention, RLS where a leak would be worst — plus a mandatory test per service |
| [0005](0005-self-hosted-auth-in-dotnet.md) | Auth built in .NET, no external IdP | Accepted | Replaces Keycloak. ASP.NET Core Identity + RS256 JWT + JWKS + rotating refresh tokens; we now own the revocation-staleness problem |
| [0006](0006-outbox-at-least-once-idempotent-consumers.md) | Outbox, at-least-once, idempotent consumers | Accepted | No dual writes; exactly-once *effects* rather than exactly-once delivery, which does not exist |
| [0007](0007-defer-ai-ml-keep-the-data.md) | Defer AI/ML, keep the data | Accepted | Parking a model is reversible; failing to record `lead_time_days` is not |
| [0008](0008-error-logs-in-each-services-own-database.md) | Errors to stdout **and** each service's own `error_logs` table | Accepted | Platform-admin support screens need a queryable tenant-scoped table; a shared log database would break ADR-0003 |
| [0009](0009-right-size-clean-architecture-per-service.md) | Clean Architecture, right-sized per service | Accepted | The rule is the inward dependency, not the project count. 8 services Full, 4 Lean, 2 Minimal — assigned by whether the service protects invariants |
| [0010](0010-key-strategy-uuidv7-bigint-natural.md) | uuid v7 for entities, bigint for log tables, natural keys for lookups | Accepted | `bigint` is still right for a monolith; three architecture-specific reasons make it wrong for entities here, and uuid wrong for the outbox |

## Superseded

None yet. When one is superseded, it stays in place with
`Status: Superseded by NNNN` — a decision that was reversed is more
instructive than one that was never written down.
