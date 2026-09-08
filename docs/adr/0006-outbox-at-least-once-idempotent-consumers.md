# 0006. Transactional outbox, at-least-once delivery, idempotent consumers

Date: 2026-09-08
Status: Accepted

## Context

A handler that changes state and then publishes a message has two
failure modes, both of which will happen:

- publish first, transaction rolls back → we announced something that
  never happened, and `reporting` now shows a booking that does not exist
- write first, publish fails → the booking exists and no notification is
  ever sent, no projection updated, no audit entry written

`Goal/Saas.txt` §30 Problem 4 asks what happens when the notification
service is down, and §30 Problem 3 asks what happens when a payment
succeeds but confirmation fails. Both need the answer to this question
first.

## Decision

**Nothing is ever published directly from a request handler.**

Every service has an `outbox_messages` table. The event row is inserted
in the **same transaction** as the business change, so the two commit or
fail together. A background publisher drains it with
`for update skip locked`, allowing multiple instances to share the work
without duplicating it.

This gives **at-least-once** delivery, so:

- **every consumer is idempotent.** Either naturally (`set status = X`
  rather than incrementing), or via a `processed_messages` table written
  in the same transaction as the side effect, keyed by
  `(message_id, consumer)`.
- **consumers tolerate out-of-order arrival.** Ordering is guaranteed per
  aggregate only, never globally. A room event for an unknown room type
  creates a stub and reconciles later; it never crashes and never drops.

Idempotency is handled separately at three levels — inbound HTTP
(`Idempotency-Key`), inbound messages (`processed_messages`), and outbound
to a payment gateway (the attempt id *is* the gateway's idempotency key).
Details in `docs/03-communication.md` §4.

## Consequences

- **The broker going down breaks nothing user-facing.** Outbox rows
  accumulate; users keep booking; delivery catches up. This is the single
  biggest reason for the pattern.
- Events lag their transaction by up to one poll interval. Anything that
  cannot tolerate that lag is not an event — it is a synchronous call, or
  it belongs in the same transaction.
- Every service carries a publisher background worker and an outbox
  table. That is real, repeated infrastructure, which is why it lives in
  `BuildingBlocks` and is written once.
- Duplicate delivery is **normal**, not an incident. Any handler that
  breaks on a duplicate is a bug in the handler.
- The outbox is a queryable log of everything a service ever published,
  which is what makes projections rebuildable
  (`docs/04-data-ownership.md` §9) and makes "why did this not happen"
  answerable with SQL.
- Retention: outbox rows need pruning once published, or the table
  becomes the largest in the database.

## Alternatives rejected

**Publish directly from the handler.** The dual-write problem above. It
works in development and lies in production.

**Two-phase commit between PostgreSQL and RabbitMQ.** Technically
possible, operationally miserable, and a liveness risk: a coordinator
failure can leave locks held. Also unavailable to us in any pleasant
form.

**Change Data Capture (Debezium reading the WAL).** Genuinely good, and
the thing to reach for at scale — no publisher loop, no polling, lower
latency. Rejected for now: it means Kafka Connect or equivalent, which is
a large piece of infrastructure to run and understand before we have met
the problem it solves. The outbox table is the version we can debug with
`psql`.

**Exactly-once delivery.** Does not exist across a network boundary. What
exists is at-least-once plus idempotent consumers, which produces
exactly-once *effects*. Pretending otherwise just moves the duplicate
handling somewhere it is not written down.

**Ordered global event log.** Would simplify some consumers and cap
throughput at one partition. Per-aggregate ordering is what the domain
actually needs — one booking's events in order — and nothing more.
