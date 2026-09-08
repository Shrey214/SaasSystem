# 0009. Clean Architecture, right-sized per service

Date: 2026-09-08
Status: Accepted
Amends: `docs/05-code-structure.md` §2, which originally said every
service is four projects

## Context

`docs/05-code-structure.md` first stated that **every** service is four
projects — `Domain`, `Application`, `Infrastructure`, `Api`. That was
convenient to write and wrong in one direction.

Clean Architecture is a single rule: dependencies point inward, and
business rules know nothing about infrastructure. Four assemblies are a
**mechanism for enforcing** that rule, not the rule itself. The rule can
be honoured inside one project with folders, and violated across six
projects by referencing EF Core from `Domain`.

The mechanism has a price: a larger build graph, DI registration spread
across files, interfaces that exist only to be crossed once, and mapping
between three representations of the same row.

For `booking` that price buys enormous protection — it guards capacity
against concurrent holds. For `audit`, which appends a row it was handed
and owns no invariant whatsoever, it buys nothing. Applying the full
ceremony everywhere is the same category of mistake as skipping it where
invariants exist; it just looks more diligent.

## Decision

Three tiers, assigned by one test: **does this service protect
invariants?**

1. Are there rules that must hold no matter which use case runs?
2. Is there a state machine with illegal transitions?
3. Does it handle money, capacity, or concurrent access to a scarce thing?
4. Or is it a pure projection of somebody else's events?

| Tier | Projects | Services |
|---|---|---|
| **Full** | Domain · Application · Infrastructure · Api | `identity`, `tenant`, `subscription`, `property`, `pricing`, `booking`, `payment`, `stay` |
| **Lean** | Core (domain + application) · Infrastructure · Api | `guest`, `operations`, `content`, `notification` |
| **Minimal** | one project, feature folders | `reporting`, `audit` |

The **dependency rule applies at every tier.** Minimal-tier services keep
the same folder structure and the same inward-pointing dependencies; they
simply stop paying for assembly separation to enforce it.

`tenant` is deliberately Full despite being small: it is built first
(Stage 4) and is the template every later service is copied from.
Establishing the pattern on four tables is far cheaper than retrofitting
it onto `booking`.

Additionally, within Full-tier services: **commands go through the
aggregate; queries do not.** Reads project straight from the `DbContext`
into DTOs in SQL. The domain model exists to protect changes and has no
job on a read path.

## Consequences

- The codebase is not uniform, so `docs/05-code-structure.md` §2.4 must
  state each service's tier explicitly. "Look at the neighbouring
  service" stops being reliable guidance.
- Tiers move **upward** cheaply. Promoting `Core` into `Domain` +
  `Application` is a mechanical split precisely because the dependency
  rule was never broken. Choosing the rule over the ceremony is what
  keeps that option open.
- Downgrading is not done. A service that was Full stays Full; churning
  structure to save an assembly is not worth a review cycle.
- `reporting` and `audit` reach shipping state much faster, which matters
  because they are two of fourteen and add no domain value.
- Reviewers need the tier table to judge a pull request. It lives with
  the structure document, not in someone's memory.
- A service crossing the invariant threshold without being promoted is a
  real risk. Signal to watch for: business rules appearing inside event
  handlers or endpoint code. That triggers a promotion, and an entry in
  `docs/learning/`.

## Alternatives rejected

**Four projects everywhere.** What the document originally said.
Consistent, easy to review, and it makes `audit` — a service whose entire
job is one `INSERT` — carry an empty `Domain` assembly, a repository
interface, and a mapper. Ceremony that reviewers learn to ignore, which
then trains them to ignore it where it matters.

**One project everywhere, folders only.** Honours the dependency rule on
paper and nothing stops a violation. In `booking` specifically, the
temptation to reach for the `DbContext` from inside a business rule under
deadline is exactly what the assembly boundary is there to refuse. The
enforcement is the value.

**Vertical slices only, no layers at all.** Genuinely good for CRUD-heavy
services, and it fails where several use cases must enforce the *same*
invariant — hold, booking, modify and cancel all touch capacity. Without
a domain model, that rule gets copied into four handlers and drifts.
Slices and layers are not competitors here: layers set the dependency
direction, slices organise the `Application` layer inside it.

**Decide per service later, when writing it.** Sounds pragmatic, produces
fourteen inconsistent judgement calls made under deadline. The test and
the tier table are written down now, before any of them exists, so the
answer is looked up rather than argued.
