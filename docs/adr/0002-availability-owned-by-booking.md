# 0002. Availability, holds and bookings live in one service

Date: 2026-09-08
Status: Accepted

## Context

`Goal/Saas.txt` §10 lists what availability depends on: physical rooms,
existing bookings, holds, maintenance, blocked rooms, restrictions and the
number of rooms requested. §12 then requires holds, and §30 Problem 1
requires that two customers cannot both get room 101.

So availability is:

```
capacity − confirmed − held − blocked
```

Creating a hold must **read that number and decrement it atomically**.
The question was where that atomic operation lives.

`Goal/Saas.txt` §28 lists "AVAILABILITY" as its own functional area
alongside "RESERVATIONS", which invites making it its own service. The
functional map is not a service map.

## Decision

`booking` owns the capacity ledger, holds and bookings. There is no
separate availability service.

`booking` does not read `property`'s database and does not call
`property` synchronously. It maintains its **own capacity projection**,
built by consuming `property.room_type.*`, `property.room.*` and
`property.room.blocked.*` (`docs/03-communication.md` §6). The projection
is `booking`'s own data, in `booking`'s own database, and it is
authoritative for the only question `booking` needs answered: how many
of this room type can I still sell on this date.

Searching availability is treated as a **different operation** from
committing it (`docs/01-bounded-contexts.md` §2.7). Search may be cached
and stale; the commit path re-reads under a lock and is allowed to reject
what search just displayed.

## Consequences

- Holds are a single local transaction. No two-phase commit, no saga, no
  distributed lock on the hottest path in the system.
- **`booking` keeps working when `property` is down.** Capacity changes
  queue up as events and apply when it returns
  (`docs/03-communication.md` §10).
- We owe a projection: room events must be consumed idempotently and
  tolerate out-of-order arrival. A bug there oversells or undersells, so
  it needs its own tests.
- Capacity is eventually consistent with the physical inventory. A room
  marked out of order is sellable for a few hundred milliseconds. Accepted
  for maintenance; would be unacceptable for holds, which is why holds
  are local.
- Search caching is now a free, separate optimisation (Stage 13) rather
  than a service boundary we have to live with.

## Alternatives rejected

**A separate `availability` service.** Every hold becomes a distributed
transaction between `availability` and `booking`, on the highest-traffic,
most race-prone path in the product. The options are two-phase commit
(unavailable to us, and a liveness risk) or a saga that can briefly
oversell — inventing the exact bug we are trying to prevent. This is the
central rejection of the whole design.

**`booking` queries `property` synchronously for capacity.** Puts a
network call inside the hold transaction, so a slow `property` becomes
lock contention in `booking`, and `property` being down stops all
bookings. It also drags `property` into every availability query it has
no business serving.

**Shared `rooms` table between `property` and `booking`.** A shared
database with two writers. Explicitly rejected by
`docs/04-data-ownership.md`, and "room" does not even mean the same thing
in the two contexts (`docs/01-bounded-contexts.md` §2.1).

**Assign a specific physical room at booking time.** Turns availability
from counting into bin-packing, and creates front-desk room-change churn
every time a checkout runs late. Room assignment is deliberately a late
decision, at or near check-in.

**Store `is_available` as a column.** It is a function of five inputs
owned by three services; stored, it is wrong the moment any of them
changes. Sellability is computed, never persisted.
