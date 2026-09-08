# 0008. Errors logged to stdout and to each service's own database

Date: 2026-09-08
Status: Accepted

## Context

The user asked for a global exception middleware that writes errors to a
table, including where the exception occurred.

Two questions had to be answered: **which** database the table lives in,
and whether a table is the right destination at all — the usual answer in
a microservice system is structured logs to stdout plus a collector
(Seq, Loki, ELK), not SQL.

There is also a product driver. `Goal/Saas.txt` §2 lists **Support** and
**System Monitoring** among platform-admin capabilities. "Show me this
tenant's recent failures" is therefore a feature of the product, and a
feature needs a queryable, tenant-scoped table — not a log search a
platform admin cannot reach.

## Decision

**Both, with different jobs.**

1. **Structured logs to stdout are the primary record.** They always
   work, including when the database is the thing that is broken. Stage
   22 gives them proper tooling.
2. **An `error_logs` table in each service's own database** is the
   queryable, tenant-scoped record that powers the platform-admin support
   screens. Created by `BuildingBlocks.Persistence` migrations, so all
   fourteen have an identical shape.

Only unhandled exceptions and `5xx` responses are recorded. Expected
business failures travel as `Result<T>` and are never logged as errors
(`docs/05-code-structure.md` §7).

The writer is constrained by four rules, each of which exists because
violating it is a real, common bug:

- **Its own connection.** Never the failed request's `DbContext`, which
  may hold a rolled-back transaction — writing through it throws again
  and loses the original error.
- **Never throws.** Failures inside the writer fall back to stdout.
- **Never blocks the response.** Entries go into a bounded
  `Channel<ErrorLogEntry>` drained by a batching background writer. On
  overflow, oldest entries are dropped and a counter incremented.
- **Records the fault location in *our* code**, by walking the stack for
  the first `HotelSaas.*` frame. The top frame is usually framework
  internals and tells you nothing.

Rows carry `correlation_id`, so an error in `booking` can be joined by
hand to an error in `payment` from the same customer request. A
`fingerprint` column groups repeats, so one bug firing 4,000 times is one
row to read.

## Consequences

- A platform admin can be shown a tenant's recent errors without access
  to log infrastructure. That is the feature.
- Tracing one request across services means querying up to fourteen
  tables by `correlation_id`. Acceptable at this scale, and the reason
  correlation ids are mandatory rather than optional.
- Every service carries one more table and one background worker. Written
  once in `BuildingBlocks`, so the cost is paid once.
- The table only grows, so it needs pruning — a retention job, 30 days
  locally. An unpruned error table becomes the largest in the database.
- Under an error storm, some rows are deliberately lost. Stdout keeps the
  complete record; the table is the convenient one, not the authoritative
  one.
- Log rows can contain personal data (paths, headers, messages), so
  headers are redacted against an allowlist and the table is subject to
  the same retention rules as other tenant data.

## Alternatives rejected

**One shared `hs_logging` database that all services write to.** The
obvious choice, and it breaks
[ADR-0003](0003-database-per-service-single-instance.md): a table written
by fourteen services is a shared database, with the coupling and the
schema-migration coordination that implies. It also becomes a single
point of failure for every service's error path.

**Stdout only, no table.** The textbook answer, and it loses the
platform-admin support feature. Revisited at Stage 22 — if a collector
ends up serving those screens directly, this ADR gets superseded.

**A dedicated `logging` service, written to over RabbitMQ.** Attractive:
no table in each service, natural fan-in. Rejected because the error path
would then depend on the broker being up, and "RabbitMQ is down" is
exactly when errors matter most. A local table needs nothing but the
database the service already has open.

**Serilog with a PostgreSQL sink.** Would have worked and saved code. Not
chosen because we want control over the schema — the `fault_*` columns,
the fingerprint, the tenant column — and a generic sink writing a
`properties` blob makes those unqueryable. Serilog is still used for the
stdout side, where a generic shape is exactly right.

**Log every `4xx` as well.** Fills the table with normal behaviour. A
`409` from a lost race for the last room is the system working correctly
(`docs/05-code-structure.md` §7); recording it as an error would hide the
faults that matter.
