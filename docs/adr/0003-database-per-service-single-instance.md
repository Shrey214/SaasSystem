# 0003. Database per service, one local instance, one role each

Date: 2026-09-08
Status: Accepted

## Context

`Goal/LearningGoal.txt` names "Database-per-service & data ownership" as a
learning goal, and `Goal/TechStack.txt` requires everything to be free
during the learning phase. Fourteen PostgreSQL containers on one laptop
is not free in the currency that matters — memory.

We also needed the boundary to survive a deadline. "Don't join across
services" as a code-review convention fails the first time a report is
needed in a hurry.

## Decision

One PostgreSQL container. Inside it, **one database per service**
(`hs_tenant`, `hs_booking`, …) and **one owning role per service** with no
grants on any other database.

Each service has its own connection string, `DbContext` and migration
history. Nothing in the code is aware that the databases share a process.

No cross-database queries. No foreign keys between services. Within a
service, foreign keys and check constraints are used aggressively.

`stay` has no database until Stage 14; until then it occupies a separate
**schema** (`stay.*`) inside `hs_booking`, with no foreign key crossing
into `booking.*`.

## Consequences

- The boundary is enforced by PostgreSQL permissions, not by review. A
  cross-service join is a permission error, which is the point.
- Fourteen migration histories to run. Startup ordering and a single
  "migrate everything" entry point are needed for local development.
- Moving any service to its own instance later is a connection-string
  change, so the local shortcut costs nothing architecturally.
- **We cannot use a database transaction across services**, which is
  intended: it is what forces the saga in `docs/03-communication.md` §7.
- Cross-service reporting must be built from events
  (`reporting`), because the convenient SQL join is genuinely unavailable.
- One instance means one `shared_buffers`, one WAL and one point of
  failure locally. Fine for learning; called out here so it is not
  mistaken for a production topology.

## Alternatives rejected

**One database, schema per service.** Cheaper still, and the usual
recommendation for a modular monolith — but a single role can see every
schema, so the boundary is back to being a convention. Search-path
mistakes silently cross services. Rejected because the enforcement is
the feature.

**One database, shared tables, `tenant_id` everywhere.** The thing this
project exists to learn how *not* to do.

**One container per service (14 containers).** Honest, and unusable: on a
development laptop it competes with the fourteen service processes plus
RabbitMQ, Redis, Kong and Postgres itself.

**A managed cloud database per service.** Not free, and
`Goal/TechStack.txt` rules it out until the AWS stage.

**SQLite per service.** No row-level security, no `jsonb`, no
`for update skip locked`, different concurrency semantics — so the
concurrency lessons at Stage 10 would be learned against the wrong
engine.
