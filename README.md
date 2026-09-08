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
| Stage | **0 — repository foundation** |
| Next | Stage 1 — bounded contexts and service boundaries |
| Services running | none yet |
| Frontend | deliberately last (Stage 23) |
| AI/ML | parked — see `Goal/Plan.txt` |

## Where to read what

| Question | File |
|---|---|
| What are we building? | `Goal/Saas.txt` — product and functional design |
| What are the business rules? | `Goal/Domain.txt` — domain blueprint, 18 tables, 8 workflows |
| What technology, and what does it cost? | `Goal/TechStack.txt` |
| Why does this project exist? | `Goal/LearningGoal.txt` |
| What is the build order? | `Goal/Plan.txt` — 24 stages, first to last |
| How do we write code here? | `docs/00-conventions.md` |
| Why is it built that way? | `docs/adr/` |
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

Nothing to run yet — Stage 2 brings up PostgreSQL, Stage 3 the first
service. Instructions land here as they become true.

## Repository layout

```
Goal/     the original brief, kept as written
docs/     design docs, ADRs, learning notes
src/      BuildingBlocks + services
tests/    unit and Testcontainers integration tests
infra/    docker compose, database init, gateway config
```
