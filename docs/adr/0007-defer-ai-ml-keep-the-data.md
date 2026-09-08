# 0007. Defer AI/ML, but capture the data now

Date: 2026-09-08
Status: Accepted

## Context

The user asked whether minimal, unique AI/ML features could be part of
the product, then decided to park the idea and revisit later.

Parking it is easy. The trap is that **the decision to defer ML is not
symmetric with the decision to defer the data.** A model can be added at
any time. The history it needs cannot be created retroactively: if
`booking.booking.confirmed.v1` never carried the booking channel, then a
year of bookings simply has no channel, and no amount of later work
recovers it.

## Decision

No `intelligence` service, no ML library, no model, no Stage 20. Service
15 and Stage 20 in `Goal/Plan.txt` are marked `[PARKED]`, with the
feature ideas kept in a parking lot for later.

**One thing survives**: the event catalog in `docs/03-communication.md`
§6 records the fields that cannot be backfilled —

- `lead_time_days`, `channel`, `refundable`, `deposit_amount` on
  `booking.booking.confirmed.v1`
- `hours_before_arrival`, `policy_version` on
  `booking.booking.cancelled.v1`
- `booking.capacity.snapshot_taken.v1` — the nightly capacity snapshot
- `duration_minutes` on `operations.cleaning.completed.v1`

Each of these is justified **on its own merit today**, independent of ML:
`reporting` needs every one of them to compute occupancy, ADR, RevPAR,
cancellation timing and housekeeping throughput. None was added
speculatively for a model.

## Consequences

- Zero cost now: no dependency, no service, no training job, no extra
  container.
- If ML is picked up later, there is real history to train on from day
  one rather than a six-month wait to accumulate it.
- If ML is never picked up, nothing was wasted — the fields are on
  reporting's critical path anyway.
- The hard questions recorded in the parking lot (multi-tenant cold
  start, no tenant identifier as a feature, point-in-time correctness,
  suggestion-not-authority, never in the hot path) stay written down, so
  the thinking does not have to happen twice.

## Alternatives rejected

**Build the ML features now.** Nothing to train on. Any model built
before Stages 4–19 have generated data would be fitted to synthetic
bookings, which teaches the plumbing and none of the actual problem.

**Defer ML *and* the fields.** The tempting version of "we'll decide
later", and the one that quietly removes the option. Adding a field to an
event is trivial; inventing a year of history is not.

**Add a generic `metadata jsonb` catch-all and sort it out later.**
Undisciplined: unqueryable in practice, unversioned, and it becomes the
place every unexamined idea is dumped. Named fields with a stated
purpose, or nothing.

**Keep the parked design out of the repository entirely.** The reasoning
about multi-tenant ML — that a pooled model leaks demand patterns
between businesses and a per-tenant model has nothing to learn from — is
the expensive part, and it is worth more written down than remembered.
