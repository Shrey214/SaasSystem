# 0001. Fourteen services, not one per module

Date: 2026-09-08
Status: Accepted

## Context

`Goal/Saas.txt` §28 lists roughly thirty functional modules. §29 warns
against the obvious move:

> We should not create 20 microservices just because we have 20 modules.
> That would be a classic beginner mistake.

We needed a rule for collapsing modules into services that was something
better than taste, and a record of it, because in six months the question
will be "why is housekeeping not its own service" and "because it felt
right" is not an answer.

## Decision

Fourteen services. Each candidate pair of contexts was tested against
five questions, in order:

1. Must they commit in the same database transaction?
2. Would splitting them put a network hop on a lock-holding path?
3. Do they fail for different reasons?
4. Do they change on different rhythms?
5. Do they scale differently?

A "yes" to (1) or (2) forces a merge and ends the discussion. (3)–(5) are
judgement.

Resulting merges: availability + holds + bookings → `booking`;
housekeeping + maintenance → `operations`; website + reviews → `content`;
plans + entitlements + SaaS billing → `subscription`. Full reasoning per
merge in `docs/02-service-boundaries.md` §3.

`stay` is a special case: it begins as a module inside `booking` with its
own schema and no cross-schema foreign keys, and is extracted at Stage 14.

## Consequences

- Fourteen services is still a lot to run on one laptop. Docker Compose
  profiles will be needed so a stage can start only what it requires.
- Every merge we made is reversible; a split we made wrongly is expensive.
  So where the tests were ambiguous we merged.
- The five-question test is now the standard for any future service, and
  `docs/02-service-boundaries.md` §7 lists the signals that would prove a
  boundary wrong.
- `BuildingBlocks` must stay free of business logic, or the fourteen
  services become one deployable again.

## Alternatives rejected

**One service per module (~30).** Rejected by the brief itself, and by
arithmetic: thirty services on one machine, most of them CRUD over a
handful of tables, with the interesting problems replaced by plumbing.

**Modular monolith first, split later.** Genuinely the better engineering
default, and rejected on purpose: the project exists to learn
microservices (`Goal/LearningGoal.txt`), and a monolith defers every
problem we are trying to meet. We keep one instance of the pattern —
extracting `stay` at Stage 14 — so the technique is learned without the
whole system depending on it.

**Four coarse services** (platform, property, booking, ops). Would hide
exactly the problems we want: no cross-service entitlement check, no
saga, no projection lag. Correct for a startup shipping this product;
useless for the goal.

**Service per aggregate.** Produces a service for `Coupon` and a service
for `Bed`. Rejected as (1) with extra steps.
