# Stage 3 — Solution and BuildingBlocks

Six shared libraries, no business logic, no service yet.

**Result:** solution builds with warnings-as-errors and zero warnings;
**29 tests pass**, 12 of them against a real PostgreSQL via Testcontainers.

Resolved versions: .NET 10.0.400 · EF Core 10.0.11 · Npgsql EF 10.0.3 ·
FluentValidation 12.1.1 · Serilog.AspNetCore 10.0.0 ·
Testcontainers 4.15.0.

---

## The finding that mattered: uuid v7 is not monotonic

ADR-0010 said to verify once, at this stage, that PostgreSQL sorts v7 ids
in creation order — because PostgreSQL compares `uuid` as 16 big-endian
bytes while .NET stores a `Guid`'s first three fields little-endian. If
Npgsql wrote the raw in-memory layout, the timestamp prefix would be
byte-reversed, PostgreSQL would sort v7 ids randomly, and index locality
would be as bad as v4 while every review said otherwise.

The first version of the test inserted 500 ids in batches of 50 and
asserted the database returned them in creation order. **It failed.**

The cause was not byte order — a second test, which delayed 2ms between
ids, passed. So:

- **Byte order is correct.** Npgsql writes RFC big-endian, and ids minted
  in distinct milliseconds sort correctly in PostgreSQL. The v7 decision
  holds.
- **The test's premise was wrong.** `Guid.CreateVersion7()` is a 48-bit
  millisecond timestamp plus **random** bits. RFC 9562 permits a monotonic
  counter; .NET does not implement one. So **ids created in the same
  millisecond have random relative order**, and 50 rows inserted in a tight
  loop are effectively unordered among themselves.

What that does and does not cost us:

| | |
|---|---|
| Index locality | **unaffected.** Same-millisecond ids share the timestamp prefix, so they land adjacent at the right-hand edge of the B-tree. This is the property we chose v7 for |
| `order by id` as a proxy for creation time | **millisecond-accurate only.** Fine for paging and for "newest first". Wrong for anything needing an exact sequence |
| Anything needing a true sequence | must use a `bigint` — which is what the outbox already does, for exactly this reason (ADR-0010) |

Both tests were kept: one proves the byte order, the other documents the
limitation so nobody rediscovers it in a debugger.

---

## The tenant filter needed proving, not assuming

EF Core caches the compiled model **per context type**. A query filter is
part of that model. So if the tenant value were baked in when the model
was first built, every later `DbContext` instance would silently read the
*first* instance's tenant — a cross-tenant leak that no amount of correct
calling code could prevent, and one that would pass a casual test because
the first tenant always sees the right data.

`TenantIsolationTests.TheFilterFollowsTheContextInstance_NotTheCachedModel`
exists for that specific failure: two instances of the same context type,
different tenants, created in sequence. It passes — EF re-parameterises a
filter that reads a context property, so the value is evaluated per query.

Also settled here: **the filter is strict.**

```csharp
entity => entity.TenantId == CurrentTenantId    // no "|| CurrentTenantId == null"
```

The convenient version would let a platform-scoped request read every
tenant implicitly — and it is the exact expression ADR-0004 warns about,
one typo from applying to tenant requests too. Being strict means a
request with no tenant matches **nothing**, which is fail-closed, and
platform-wide reads must say `IgnoreQueryFilters()` out loud — greppable
and auditable. There is a test for each of those two behaviours.

---

## Warnings-as-errors earned its place four times

Not one of these was noise, and each forced a decision instead of a
default:

**1. A vulnerable transitive package.** `Testcontainers.PostgreSql`
dragged in `SSH.NET` 2024.2.0 with a known high-severity advisory
(GHSA-q939-rpr3-3284), and `NU1903` failed the restore. My guessed version
`4.8.0` was also badly out of date — actual latest is `4.15.0`, which
resolves a patched SSH.NET. Caught before a line of service code existed.

**2. `CA1848` — logging allocations.** `LogWarning("...{X}", x)` boxes
every argument and formats the string even when the level is disabled.
Replaced with the `[LoggerMessage]` source generator, which emits a cached
delegate. It matters *here* specifically: this is the error path, so it
runs when the system is already struggling.

**3. `CA1000` — `PagedResult<T>.Empty`.** The rule has a point:
`PagedResult<Booking>.Empty` and `PagedResult<Room>.Empty` read as if they
were the same member. Moved to a non-generic companion,
`PagedResult.Empty<T>()`.

**4. `CA1859` and `CS8417`** in the tests — a needlessly widened return
type and `await using` on a type that is only `IDisposable`.

Two suppressions were deliberate, with the reasoning recorded next to
them rather than in a commit message:

- **`CA1716`** objects to the type name `Error` because *VB* has an `Error`
  statement. This codebase is C#-only and `Error` is the conventional name
  in the Result pattern. Suppressed repository-wide.
- **`EnforceCodeStyleInBuild`** turned **off**. Real analyzer findings
  (`CAxxxx`) stay errors, but making a misplaced brace fail the build costs
  more than it saves — `.editorconfig` and `dotnet format` cover style.

---

## Test projects need their own analyzer profile

`CA1707` failed the build for underscores in test method names — while
`docs/00-conventions.md` §7 *requires* `Method_Scenario_ExpectedOutcome`,
because that is what makes a failing test readable in CI output.

Rather than break the convention or weaken the whole repository,
`tests/Directory.Build.props` imports the root file and relaxes four rules
that are about library design and conflict with good test code: `CA1707`
(underscores), `CA1711` (xunit *requires* a type ending in `Collection`
for `[CollectionDefinition]`), `CA2007` (no synchronisation context to
deadlock against in a test host), `CA1861` (inline data is clearer than a
hoisted field).

Warnings-as-errors stays on. Production and test code simply are not the
same kind of code.

---

## Two smaller decisions

**Lock files deferred.** `RestorePackagesWithLockFile` was switched off
until Stage 24. While projects and packages are being added every few
minutes it produces `NU1004 lock file out of date` on nearly every change.
Deterministic restore matters in CI, not during scaffolding.

**No MediatR.** It went to a commercial licence, which conflicts with
`Goal/TechStack.txt`'s free-only constraint. Handlers will be plain
classes registered in DI — one less dependency and nothing lost. Kept:
FluentValidation (Apache 2.0), Serilog (Apache 2.0), Shouldly (BSD).
Avoided: FluentAssertions, which now requires a paid licence for
commercial use.

---

## Left deliberately temporary

`TenantContextMiddleware.AllowHeaderFallback` reads the tenant from an
`X-Tenant-Id` header, because there is no authentication until Stage 5. It
is trivially forged.

It defaults to **false**, so a service that forgets to enable it simply
has no tenant rather than an insecure one, and it carries a loud comment
block. Stage 5 deletes the property and the branch behind it. The claims
path is already written and is tried first, so that deletion removes code
rather than changing behaviour.
