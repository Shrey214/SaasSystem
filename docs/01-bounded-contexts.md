# 01 — Bounded Contexts

Stage 1 of `Goal/Plan.txt`. Phase 3 of the sequence in `Goal/Saas.txt` §31.

`Goal/Domain.txt` gave us ~180 pieces of functionality across 18 blueprint
tables. This document groups them into **bounded contexts** — and a
bounded context is not a folder or a subsystem. It is **a region in which
one word means exactly one thing.**

That definition is the whole tool. Wherever a single word starts meaning
two different things, we have found a boundary. `Goal/Saas.txt` §8 already
noticed this without naming it:

> Otherwise we end up with one giant RoomStatus enum trying to represent
> five different concepts.

That sentence is the entire method. Below, we apply it deliberately.

---

## 1. The contexts

| # | Context | Answers the question | Blueprint source |
|---|---|---|---|
| 1 | **Identity & Access** | Who is this, and what may they touch? | Part 18, Part 1 (staff/roles) |
| 2 | **Tenant** | Which business is this, and is it allowed to operate? | Part 1 |
| 3 | **Subscription & Entitlement** | What has this business paid for, and has it hit a limit? | Part 1 (subscription rows) |
| 4 | **Property Inventory** | What physically exists at this hotel? | Part 2 |
| 5 | **Rate & Pricing** | What should this cost, and is it sellable under the rules? | Part 3 |
| 6 | **Guest & Customer** | Who books, and who sleeps here? | Part 7 |
| 7 | **Reservation** | Can we commit this capacity, and to whom? | Parts 4, 5, 6, 9 |
| 8 | **Hotel Payment** | Did the guest's money actually move? | Parts 8, 9 |
| 9 | **Stay & Folio** | What is happening during the stay, and what is owed? | Parts 10, 11 |
| 10 | **Room Servicing** | Is this room fit to sell? | Parts 12, 13 |
| 11 | **Notification** | Who needs to be told, on which channel? | Part 14 |
| 12 | **Reporting** | How did the business perform? | Part 17 |
| 13 | **Audit** | Who changed what, when? | Part 18 |
| 14 | **Public Presence** | What does a stranger on the internet see? | Parts 15, 16 |

Fourteen contexts from ~30 functional modules. The compression is
deliberate and is justified per-merge in `02-service-boundaries.md`.

---

## 2. The language conflicts — the actual boundaries

This is the most valuable section in the document. Each row is a word that
means different things in different places. Every one of them would have
become a bug if we had gone straight to tables.

### 2.1 "Room"

| Context | What "room" means | Shape in that context |
|---|---|---|
| Property Inventory | a physical asset: number 203, floor 2, of type Deluxe | a row with an identity that lasts for years |
| Reservation | **a unit of sellable capacity for a date range** — usually *"one Deluxe on the 20th"*, not room 203 | a **count** per room type per date |
| Stay & Folio | where this guest physically is right now | a pointer on an occupied stay |
| Room Servicing | a thing to be cleaned and inspected | a task target with a cleanliness state |
| Reporting | a denominator — available room-nights | a number |

**The consequence, and it is a big one:** a customer does not book room 203.
They book *a Deluxe*. Assigning a specific room is a **late** decision —
at check-in, or shortly before arrival. A design that assigns room 203 at
booking time creates room-change churn for the front desk every time a
checkout runs late, and it makes availability far harder than it needs to
be, because you are solving a bin-packing problem instead of counting.

So: `property` owns `rooms`. `booking` owns *capacity counts per room type
per date*. There is no shared `rooms` table, and `booking` never needs one.

### 2.2 "Status" — five different concepts

`Goal/Saas.txt` §8 flagged this. Naming them separately, with their owners:

| Concept | Values | Owner | Changed by |
|---|---|---|---|
| **Operational status** | `ACTIVE`, `OUT_OF_ORDER`, `MAINTENANCE`, `BLOCKED` | Property Inventory | a manager, or a maintenance outcome |
| **Housekeeping status** | `VACANT_CLEAN`, `VACANT_DIRTY`, `INSPECTION`, `OCCUPIED` | Room Servicing | housekeeping workflow |
| **Sellability** | derived — never stored as a status | Reservation | recomputed, never set |
| **Booking state** | `HELD`, `CONFIRMED`, `CANCELLED`, `NO_SHOW`, `CHECKED_IN`, `CHECKED_OUT` | Reservation | the booking lifecycle |
| **Payment state** | `PENDING`, `CAPTURED`, `FAILED`, `REFUNDED` | Hotel Payment | the gateway |

Five columns in three different databases. Not one enum.

Sellability is the interesting one: it is **a function, not a field**. A
room type is sellable for a date when physical capacity exists, minus
confirmed bookings, minus live holds, minus blocked and out-of-order
rooms, and the pricing restrictions permit it. Store it and it is wrong the
moment anything else changes.

### 2.3 "Customer" vs "Guest"

`Goal/Domain.txt` Part 7 already separates these; it matters more than it
looks:

- **Customer** — the person who booked and paid. Platform-scoped: one
  account books at many businesses. May never enter the building.
- **Guest** — a person occupying a bed. Tenant-scoped. Has identity
  documents, nationality, preferences. May have no account at all.

The same human is often both, and they are still two records, because they
are governed differently: a customer authenticates; a guest is
photocopied at a front desk. Merging them means either giving login
credentials to a walk-in guest's ID scan or losing the booker.

### 2.4 "Payment"

Two completely unrelated flows that share one word:

| | Hotel Payment | SaaS Billing |
|---|---|---|
| Who pays whom | guest → the property | business → us |
| Gateway | Razorpay, per-tenant | our own merchant account |
| Amount driven by | rates, folio, taxes | plan price, seats, proration |
| Refund | governed by a cancellation policy | governed by our terms |
| Failure means | a booking must not confirm | a tenant loses paid features |

`Goal/Saas.txt` §29 says it outright: *"SaaS Subscription and Hotel Booking
Payment are completely different businesses despite both involving money."*
They live in Hotel Payment and Subscription & Entitlement respectively, and
they never share a table, a gateway integration, or an invoice numbering
sequence.

### 2.5 "Property"

| Context | What it is |
|---|---|
| Subscription & Entitlement | **a countable licensed unit** — "Starter allows 1" |
| Tenant | something a business owns and a user is granted access to |
| Property Inventory | a full aggregate: floors, room types, rooms, policies |
| Public Presence | a website with a domain, images and SEO metadata |
| Reporting | a comparison dimension |

Subscription needs only a *count*. Giving Subscription the property
aggregate would couple our billing rules to hotel inventory modelling for
no reason.

### 2.6 "Price" / "Rate"

| Context | What it is |
|---|---|
| Rate & Pricing | the **output of a rule** — recomputed on every request |
| Reservation | an **immutable snapshot** agreed at booking time |
| Stay & Folio | a charge line on a bill |
| Reporting | the ADR numerator |

`Goal/Domain.txt` Part 3 calls this out as *"Lock Price at Booking"*. A
confirmed booking keeps the price the guest agreed to, even after the rate
plan changes tomorrow. That makes the price snapshot **part of the booking
aggregate**, not a foreign key into pricing.

### 2.7 "Availability"

| Meaning | Tolerance for being wrong |
|---|---|
| *"show me what looks bookable"* (search) | high — may be cached, may be seconds stale |
| *"commit this capacity to this customer"* | **zero** — must be exact, serialised, and under a lock |

One word, two utterly different guarantees. Conflating them is precisely
how double bookings happen: a search result gets treated as a promise.
So search may be cached (Stage 13 puts it in Redis); the commit path
re-reads under a lock and may reject what search had just shown. The API
is designed so that rejection is normal, not exceptional.

### 2.8 "User"

Identity & Access: a principal with credentials. Tenant: a staff member of
a business. Property Inventory: someone granted access to this property.
Guest & Customer: not a user at all. Only the first has a password.

### 2.9 One word we are standardising, not splitting

The brief uses *booking* and *reservation* interchangeably. Rather than
invent a distinction, we use **booking** everywhere — code, tables, events,
URLs — because that is the word `Goal/Domain.txt` uses most and matching
the brief costs nothing. "Reservation" survives only as the name of the
*context*, never as a type name. One word, one meaning.

---

## 3. The context map

How contexts relate matters as much as what they contain. Using the
standard relationship patterns, because each one implies a different
amount of coupling we are agreeing to carry:

```
                      ┌──────────────────────┐
                      │  Identity & Access   │
                      │  (published language:│
                      │   the JWT claims)    │
                      └──────────┬───────────┘
                        every context conforms
                                 │
   ┌─────────────┬───────────────┼──────────────┬──────────────┐
   │             │               │              │              │
┌──▼───┐   ┌─────▼──────┐  ┌─────▼─────┐  ┌─────▼────┐  ┌──────▼─────┐
│Tenant│   │Subscription│  │ Property  │  │  Rate &  │  │  Guest &   │
│      │◄──┤& Entitlement│  │ Inventory │  │ Pricing  │  │  Customer  │
└──────┘   └─────▲──────┘  └─────┬─────┘  └─────┬────┘  └──────┬─────┘
                 │  customer/    │ events       │ sync         │
                 │  supplier     │ (+ACL)       │ quote        │
                 └───────────────┤              │              │
                                 │              │              │
                          ┌──────▼──────────────▼──────────────▼──────┐
                          │              RESERVATION                  │
                          │  availability · holds · bookings          │
                          └───┬───────────────────┬──────────────┬────┘
                     saga     │        events     │              │ events
                    ┌─────────▼──────┐   ┌────────▼───────┐  ┌───▼──────────┐
                    │ Hotel Payment  │   │ Stay & Folio   │  │Room Servicing│
                    └─────────┬──────┘   └────────┬───────┘  └───┬──────────┘
                              │                   │              │
                              └─────────┬─────────┴──────────────┘
                                        │ events, one-way
              ┌─────────────────┬───────┴────────┬─────────────────┐
              │                 │                │                 │
      ┌───────▼──────┐  ┌───────▼──────┐  ┌──────▼─────┐  ┌────────▼───────┐
      │ Notification │  │  Reporting   │  │   Audit    │  │Public Presence │
      └──────────────┘  └──────────────┘  └────────────┘  └────────────────┘
```

| Relationship | Pattern | What we are agreeing to |
|---|---|---|
| Identity → everyone | **published language** | The JWT claim set is a versioned contract. Change it and 13 services care, so it changes rarely and additively. |
| Property → Subscription | **customer / supplier** | Property must ask permission to exist. Subscription is upstream and may say no. Property wraps the call in an anticorruption layer and can fall back to a cached answer — see ADR-0005. |
| Property → Reservation | **published events + ACL** | Reservation does not read property rooms; it *translates* room events into its own capacity model. The translation layer is the point — it is what lets the two models differ. |
| Pricing → Reservation | **customer / supplier, synchronous** | Booking needs an exact quote at commit time, and a stale price is a financial error. This one call is allowed to be synchronous and to fail the request. |
| Reservation → Payment | **saga** | Neither owns the other. A long-running conversation with compensations, not a transaction. |
| Reservation → Stay / Servicing | **choreography** | Checkout happened; whoever cares reacts. No orchestrator. |
| Everything → Reporting / Audit | **one-way published events** | Downstream contexts never call back. They may be down for an hour and catch up. |

---

## 4. What this bought us

Before writing a single table, the boundaries are now decided by
*language* rather than by convenience:

- `booking` will not have a `rooms` table, because "room" means capacity
  there — and that single decision is what makes the availability problem
  tractable.
- No `RoomStatus` enum. Five concepts, three owners, separate columns.
- Price is snapshotted into a booking, not referenced from pricing.
- Sellability is computed, never stored.
- Customer and Guest stay separate records.
- Subscription counts properties; it does not model them.
- Search availability and committed availability are different operations
  with different guarantees — one may be cached, the other never.

Next: `02-service-boundaries.md` turns these 14 contexts into deployable
services, which is *not* automatically a 1:1 mapping.
