# 03 — Service Communication

Phase 4 of `Goal/Saas.txt` §31.

`02-service-boundaries.md` decided *who* talks to *whom*. This decides
*how*, and what happens when it fails — which is the part that actually
matters, because in a distributed system the failure path is the normal
path, just less often.

---

## 1. The decision rule

Synchronous only when **all three** hold:

1. The caller cannot produce a correct answer without it, and
2. a stale answer would be *wrong*, not merely old, and
3. failing the user's request is an acceptable outcome.

Everything else is an event. Applying this to the whole system leaves
**five synchronous calls** (`02-service-boundaries.md` §5). Every other
interaction is asynchronous.

The rule that follows from it: **never make a synchronous call while
holding a lock or an open transaction.** Booking's quote call to `pricing`
happens *before* the capacity transaction opens, and the result is
snapshotted so the transaction needs no network.

---

## 2. Transport

RabbitMQ. One **topic exchange per publishing service** (`property.events`,
`booking.events`, …), routing key = the event type. One **queue per
consuming service per exchange** (`booking.property-events`,
`reporting.property-events`).

Queues are per-consumer on purpose: if `reporting` falls an hour behind,
`booking` does not notice. A shared queue would couple every consumer's
throughput to the slowest one.

Ordering guarantee: **per aggregate only.** Messages for one booking
arrive in order because they share a routing key and a single queue.
There is no global ordering, and no design may assume one.

---

## 3. The transactional outbox

The problem it solves: a handler that writes a row and then publishes a
message has two failure modes, and both are real. Publish first and the
transaction rolls back — you have announced something that never
happened. Write first and the publish fails — the world never hears about
something that did.

So nothing is ever published directly. Every service has:

```sql
create table outbox_messages (
    id              uuid        primary key,
    tenant_id       uuid,
    aggregate_type  text        not null,
    aggregate_id    uuid        not null,
    event_type      text        not null,   -- booking.booking.confirmed.v1
    payload         jsonb       not null,
    correlation_id  uuid        not null,
    causation_id    uuid,
    occurred_at     timestamptz not null,
    published_at    timestamptz,
    attempts        integer     not null default 0,
    last_error      text
);
create index ix_outbox_messages_unpublished
    on outbox_messages (occurred_at)
    where published_at is null;
```

The row is written **in the same transaction as the business change**. A
background publisher then drains it:

```sql
select * from outbox_messages
 where published_at is null
 order by occurred_at
 limit 100
 for update skip locked;
```

`skip locked` is what lets several instances of a service drain the same
outbox without fighting or duplicating.

Consequences we accept:

- **At-least-once delivery.** The publisher can crash between the broker
  ack and the `published_at` update. Every consumer is therefore
  idempotent. This is not a corner case; it will happen.
- **Latency.** Events lag the transaction by up to one poll interval.
  Anything that cannot tolerate that lag is not an event — it is a
  synchronous call or the same transaction.
- **The broker going down breaks nothing.** Rows accumulate, users keep
  booking, delivery catches up. This is the main reason for the pattern.

---

## 4. Idempotency

Three separate places, three mechanisms. Conflating them is a common way
to end up charging a card twice.

### Inbound HTTP

Every state-changing endpoint accepts `Idempotency-Key`; externally
triggered ones require it.

```sql
create table idempotency_keys (
    key             text        not null,
    tenant_id       uuid        not null,
    endpoint        text        not null,
    request_hash    text        not null,
    response_status integer,
    response_body   jsonb,
    created_at      timestamptz not null default now(),
    completed_at    timestamptz,
    primary key (tenant_id, endpoint, key)
);
```

- Same key, same body, already completed → **replay the stored response**.
  Do not re-execute.
- Same key, same body, still in flight → `409`, retry later. The row is
  inserted before the work starts, so it doubles as a lock.
- Same key, **different** body → `422`. The client has a bug, and
  guessing which request they meant is how money moves twice.

### Inbound messages

```sql
create table processed_messages (
    message_id   uuid        not null,
    consumer     text        not null,
    processed_at timestamptz not null default now(),
    primary key (message_id, consumer)
);
```

Insert inside the same transaction as the side effect. A duplicate insert
means we already did this; the handler returns quietly. Keyed by consumer
as well, because the same message is legitimately processed by six
different services.

Better still, where it is cheap: make the handler naturally idempotent
(`set status = 'CONFIRMED'` rather than `increment`), so a duplicate is
harmless regardless.

### Outbound to a gateway

The payment attempt id **is** the gateway's idempotency key. A retried
capture with the same attempt id cannot charge twice, even if our
first response was lost.

---

## 5. Correlation

Every envelope (`docs/00-conventions.md` §6) carries `correlation_id` and
`causation_id`.

- `correlation_id` is minted at the edge (Kong, or the first service if
  absent) and copied unchanged into every downstream HTTP call
  (`X-Correlation-Id`) and every message. One value spans the entire
  customer booking, across nine services.
- `causation_id` is the `message_id` of whatever directly caused this one,
  giving a parent chain rather than a flat set.

Both go into every log line. Long before OpenTelemetry arrives at Stage
22, this is what makes "why did this booking not confirm" answerable with
`grep`.

---

## 6. Event catalog v1

Naming per `docs/00-conventions.md` §6. Fields shown are the meaningful
ones; every event also carries the standard envelope.

### `tenant`
| Event | Key payload |
|---|---|
| `tenant.business.registered.v1` | business_id, legal_name, owner_email, country |
| `tenant.business.activated.v1` | business_id, activated_at |
| `tenant.business.suspended.v1` | business_id, reason, suspended_at |

### `subscription`
| Event | Key payload |
|---|---|
| `subscription.subscription.created.v1` | subscription_id, plan_code, trial_ends_at, limits{properties, rooms, users} |
| `subscription.subscription.changed.v1` | old_plan_code, new_plan_code, limits{}, effective_at |
| `subscription.subscription.expired.v1` | subscription_id, expired_at |
| `subscription.usage.limit_reached.v1` | resource, limit, current |

### `property`
| Event | Key payload |
|---|---|
| `property.property.created.v1` | property_id, name, timezone, currency, check_in_time, check_out_time |
| `property.property.activated.v1` / `.suspended.v1` | property_id, at |
| `property.room_type.created.v1` / `.updated.v1` | room_type_id, code, name, max_adults, max_children |
| `property.room.added.v1` | room_id, room_type_id, room_number, floor |
| `property.room.removed.v1` | room_id, room_type_id |
| `property.room.blocked.v1` | room_id, room_type_id, from_date, to_date, reason |
| `property.room.unblocked.v1` | room_id, room_type_id, from_date, to_date |
| `property.policy.updated.v1` | policy_type, version, payload |

`booking` consumes `room_type.*`, `room.*` and `property.activated` — and
nothing else — to build its capacity ledger.

### `pricing`
| Event | Key payload |
|---|---|
| `pricing.restriction.changed.v1` | room_type_id, from_date, to_date, min_stay, max_stay, closed |

Rates are **not** published. `booking` asks for a quote synchronously and
snapshots it; broadcasting a rate calendar would duplicate the pricing
engine in every consumer.

### `booking`
| Event | Key payload |
|---|---|
| `booking.hold.created.v1` | hold_id, property_id, room_type_id, arrival, departure, rooms, expires_at |
| `booking.hold.expired.v1` / `.released.v1` | hold_id, reason |
| `booking.booking.confirmed.v1` | booking_id, reference, property_id, customer_id, **channel**, arrival, departure, nights, rooms[{room_type_id, qty}], adults, children, rate_plan_code, meal_plan_code, **refundable**, **deposit_amount**, total_amount, currency, **lead_time_days**, **booked_at**, guest_snapshot[], cancellation_policy_snapshot{} |
| `booking.booking.modified.v1` | booking_id, changes{}, new_total |
| `booking.booking.cancelled.v1` | booking_id, cancelled_at, cancelled_by, reason, **hours_before_arrival**, refund_amount, policy_version |
| `booking.booking.no_show.v1` | booking_id, recorded_at |
| `booking.capacity.snapshot_taken.v1` | property_id, stay_date, room_type_id, capacity, sold, held, blocked |

The bolded fields are the ones `Goal/Plan.txt` asks Stage 1 to capture:
lead time, channel, refundability, deposit, cancellation timing, and the
nightly capacity snapshot. `reporting` needs every one of them for
occupancy and ADR, so they earn their place today regardless of whether
the parked ML work ever happens — and none of them can be reconstructed
after the fact.

### `payment`
| Event | Key payload |
|---|---|
| `payment.payment.initiated.v1` | payment_id, booking_id, amount, currency, gateway_order_ref |
| `payment.payment.captured.v1` | payment_id, booking_id, amount, method, gateway_ref, captured_at |
| `payment.payment.failed.v1` | payment_id, booking_id, reason_code, final(bool) |
| `payment.refund.completed.v1` | refund_id, payment_id, booking_id, amount, refunded_at |

### `stay`
| Event | Key payload |
|---|---|
| `stay.stay.checked_in.v1` | stay_id, booking_id, room_id, checked_in_at, early(bool) |
| `stay.stay.checked_out.v1` | stay_id, booking_id, room_id, checked_out_at, late(bool), final_total |
| `stay.charge.added.v1` | folio_id, charge_type, amount |
| `stay.invoice.issued.v1` | invoice_id, booking_id, total, balance |

### `operations`
| Event | Key payload |
|---|---|
| `operations.cleaning.completed.v1` | task_id, room_id, duration_minutes |
| `operations.room.inspected.v1` | room_id, passed(bool) |
| `operations.maintenance.opened.v1` | issue_id, room_id, priority, blocks_sale(bool) |
| `operations.maintenance.resolved.v1` | issue_id, room_id, resolved_at |

### `identity`
| Event | Key payload |
|---|---|
| `identity.access.revoked.v1` | user_id, tenant_id, revoked_at |
| `identity.permissions.changed.v1` | subject_id, subject_type, perms_version |

### `content`
| Event | Key payload |
|---|---|
| `content.review.published.v1` | review_id, booking_id, property_id, ratings{}, published_at |

---

## 7. The booking saga

`Goal/Domain.txt` Workflow C, and `Goal/Saas.txt` §30 Problem 3.

**Orchestrated, not choreographed** — `booking` is the orchestrator,
because `booking` holds the capacity and is therefore the only service
able to decide what the correct outcome is. `payment` is a participant
that knows nothing about bookings.

```
  search (cached, no commitment)
        │
        ▼
  [1] quote            sync → pricing        snapshot the price
        │
        ▼
  [2] create hold      LOCAL TRANSACTION     capacity decremented, expires_at set
        │
        ▼
  [3] initiate payment sync → payment        gateway order id returned to browser
        │
        ▼
  [4] ... customer pays at the gateway ...    (minutes; we hold nothing but a row)
        │
        ├── payment.captured  ──► [5a] confirm booking, consume hold
        ├── payment.failed    ──► [5b] release hold
        └── nothing at all    ──► [5c] hold expires (background job)
```

Steps 1–3 are separate transactions. There is no distributed transaction
anywhere in this flow.

### Compensations

| Failure | Compensation |
|---|---|
| quote fails | nothing to undo; request fails |
| hold fails (no capacity) | nothing to undo; `409`, and this is a **normal** outcome, not an error |
| initiate payment fails | release hold immediately; do not wait for expiry |
| payment fails | release hold, notify |
| hold expires with no payment | release capacity, mark hold expired |
| confirm fails *after* capture | retry; if it keeps failing, refund and notify — see below |

### The case that teaches the most

**The hold expired, and then the payment captured.** The money moved; the
capacity is gone. `Goal/Saas.txt` §30 asks for exactly this.

Our rule, on receiving a late `payment.captured`:

1. Re-check capacity for those dates.
2. **Still available** → confirm the booking anyway. The customer paid and
   gets their room; nobody is harmed by being generous with a race we lost.
3. **No longer available** → do not confirm. Trigger an automatic refund,
   emit `booking.confirmation_failed`, notify the customer. Never silently
   keep the money, and never oversell to hide our own race.

That branch is a business decision, not a technical one, which is why it
belongs in `booking` and is written down here rather than discovered
later in an incident.

### Timeouts

| Thing | Value | Why |
|---|---|---|
| hold lifetime | 10 min (per property, configurable) | `Goal/Saas.txt` §12 |
| sync call timeout | 2 s | longer and the user has already left |
| payment webhook wait | hold lifetime + 5 min grace | covers the late-capture case above |
| expiry sweep | every 30 s | worst case 30 s of capacity held after expiry |

---

## 8. Retries and the dead-letter queue

Not everything should be retried. Retrying a validation failure just
fails 5 more times and delays the alert.

| Class | Examples | Policy |
|---|---|---|
| **Transient** | timeout, connection reset, `503`, deadlock, broker unavailable | retry with backoff: 1s, 5s, 25s, 2m, 10m — then DLQ |
| **Permanent** | schema mismatch, unknown event version, business-rule rejection | **straight to DLQ**, no retries |
| **Ambiguous** | gateway timeout on a capture | never retry blindly — **reconcile**. Ask the gateway what actually happened. This is what the Stage 11 reconciliation job is for |

Every queue has `<queue>.dlq`. A DLQ message keeps the original envelope,
the failure reason and the attempt count, so it can be replayed after a
fix. A non-empty DLQ is an alert, not a log line — Stage 16 builds the
replay tool and Stage 22 the alert.

Retries are **capped by attempt count in the message header**, not by
wall-clock. A poison message that crashes the consumer on deserialisation
would otherwise loop forever and starve the queue.

---

## 9. Versioning

- Events are **additive only**. New optional field: same version.
  Removing, renaming or retyping a field: **new event name**
  (`...confirmed.v2`), publish both until every consumer has moved.
- Consumers **ignore unknown fields** and must not fail on them.
- HTTP: version in the path (`/api/v1`). New version only for a breaking
  change, old version supported until the frontend has moved.
- The JWT claim set is a published contract (`01-bounded-contexts.md` §3)
  and changes additively for the same reason.

---

## 10. Degradation matrix

Written now, verified by killing containers at Stage 21. This is the
answer to `Goal/Saas.txt` §30 Problem 7.

| Service down | Still works | Breaks |
|---|---|---|
| `identity` | everything already holding a valid token (tokens are validated locally against cached JWKS) | login, refresh, permission changes |
| `tenant` | all operations — `tenant_id` comes from the token | business admin screens |
| `subscription` | everything, on cached entitlements | creating a property once the cache is cold |
| `property` | **search, holds, bookings, check-in** — booking owns its capacity ledger | inventory admin; capacity changes queue up as events |
| `pricing` | search on cached prices, check-in, checkout | **new bookings** — deliberate: no guessed prices |
| `booking` | nothing much. This is the core | search, holds, bookings |
| `payment` | search, holds, front-desk cash operations | online payment; holds expire naturally |
| `stay` | booking, search | front desk |
| `operations` | everything; rooms simply stay in their last known state | housekeeping screens |
| `notification` | everything | messages queue and send later |
| `reporting` | everything | dashboards go stale |
| `audit` | everything | audit events queue |
| RabbitMQ | **everything user-facing** — outbox rows accumulate | all propagation, until it returns |
| PostgreSQL | nothing | everything |

The two rows to notice: `property` being down does not stop bookings, and
RabbitMQ being down does not stop users. Both are direct consequences of
decisions in `02-service-boundaries.md` §5 and §3 of this document.

---

Next: `04-data-ownership.md`.
