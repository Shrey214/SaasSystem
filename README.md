# HotelSaas

A multi-tenant Hotel Management SaaS, built as microservices, to learn
production-grade distributed systems by hitting the actual problems.

Not one hotel's software — the **platform**:

```
SaaS Platform
   └── Business (tenant)          our paying customer
         └── Property (hotel)     many per business
               └── Operations     inventory, rates, bookings, stays
```

## Status

| | |
|---|---|
| Stage | **3 done — solution + 6 BuildingBlocks libraries, 29 tests passing** |
| Next | Stage 4 — the `tenant` service, first runnable vertical slice |
| Services running | none yet — Stage 4 brings up the first one |
| Frontend | deliberately last (Stage 23) |
| AI/ML | parked — [ADR-0007](docs/adr/0007-defer-ai-ml-keep-the-data.md) |

## Where to read what

| Question | File |
|---|---|
| What are we building? | `Goal/Saas.txt` — product and functional design |
| What are the business rules? | `Goal/Domain.txt` — domain blueprint, 18 tables, 8 workflows |
| What technology, and what does it cost? | `Goal/TechStack.txt` |
| Why does this project exist? | `Goal/LearningGoal.txt` |
| What is the build order? | `Goal/Plan.txt` — 24 stages, first to last |
| How do we write code here? | `docs/00-conventions.md` |
| Where does one word stop meaning one thing? | `docs/01-bounded-contexts.md` |
| Why these 14 services? | `docs/02-service-boundaries.md` |
| How do services talk, and what breaks when? | `docs/03-communication.md` |
| Who owns which fact? | `docs/04-data-ownership.md` |
| Where does my code go? | `docs/05-code-structure.md` |
| Why is it built that way? | [`docs/adr/`](docs/adr/README.md) |
| What did we learn the hard way? | `docs/learning/` |

## Architecture in one paragraph

Fourteen services, not one per module. Each owns its own PostgreSQL
database — no shared tables, no cross-service foreign keys. `tenant_id`
comes only from a validated JWT, never from a request. Availability,
holds and reservations live together inside `booking` because splitting
them would put a distributed transaction on the most race-prone path in
the system. Authentication and authorization are ours, written in
.NET 10, with no external identity provider. Infrastructure —
Kong, RabbitMQ, Redis — is introduced only at the stage where a real
problem demands it, never up front.

## Stack

.NET 10 / C# 14 · PostgreSQL · EF Core 10 · Docker Compose ·
Kong Gateway OSS · RabbitMQ · Redis · Razorpay sandbox · Next.js (last) ·
later AWS free tier, Kubernetes, OpenTelemetry, Prometheus, Grafana.

Everything free for the whole learning phase.

## Running it

```powershell
./infra/scripts/up.ps1               # start postgres + pgAdmin
./infra/scripts/verify-isolation.ps1 # prove the service boundary holds
./infra/scripts/down.ps1             # stop (add -Purge to delete data)
```

`up.ps1` creates `infra/docker/.env` from `.env.example` on first run.
Add `-Fresh` to wipe the volumes and re-run the database init scripts —
needed after editing anything in `infra/docker/postgres/init/`, because
those scripts only execute on an empty volume.

| | |
|---|---|
| pgAdmin | http://localhost:5050 |
| PostgreSQL | `localhost:5432`, user `postgres` |
| Databases | 13 x `hs_<service>`, each with its own login role |

`verify-isolation.ps1` is the one that matters: it checks each role can
reach its own database and that all 156 cross-service combinations are
refused. Creating 13 databases is easy; a cluster with no isolation looks
identical until you try the connection that should fail.

### Build and test

```powershell
dotnet build          # warnings are errors
dotnet test           # 29 tests; the integration ones start their own postgres
```

Integration tests use Testcontainers, so they need Docker running but not
`up.ps1` - they start and dispose their own PostgreSQL.

## Repository layout

```
Goal/     the original brief, kept as written
docs/     design docs, ADRs, learning notes
src/      BuildingBlocks + services
tests/    unit and Testcontainers integration tests
infra/    docker compose, database init, gateway config
```
