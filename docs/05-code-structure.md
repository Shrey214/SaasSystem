# 05 — Code Structure

Where code goes, and why the folders are shaped this way.

---

## 1. The mental model shift

In a classic project there is **one backend and one frontend**:

```
MyApp.Api/          <- "the backend"
MyApp.Web/          <- "the frontend"
```

Here there is **no such thing as "the backend"**. There are fourteen
backends. Each one is an independently deployable application with its
own database, its own connection string, its own migrations, its own
Docker image, and its own lifecycle. One can be redeployed, scaled or
broken without the others noticing.

```
src/services/
├── identity/       an application
├── tenant/         an application
├── property/       an application
├── booking/        an application
└── ...             (14 total)
```

`src/services/<name>/` therefore means: **everything needed to build and
run this one service.** Nothing in `booking/` may reference anything in
`property/` — not a class, not a `DbContext`, not a project reference.
If it needs something from `property`, it gets it over HTTP or from an
event, exactly as if `property` were written in another language by
another company.

The frontend, when it arrives at Stage 23, is a single Next.js
application at `src/web/` that talks to all of them through the gateway.

---

## 2. Anatomy of one service

Clean Architecture, **right-sized per service** — see §2.3 and
[ADR-0009](adr/0009-right-size-clean-architecture-per-service.md). Most
services get four projects; a few genuinely do not need them.

The reference shape, used by 8 of the 14:

```
src/services/tenant/
├── HotelSaas.Tenant.Domain/           the business rules
├── HotelSaas.Tenant.Application/      the use cases
├── HotelSaas.Tenant.Infrastructure/   the technology
├── HotelSaas.Tenant.Api/              the entry point
└── Dockerfile
```

### 2.1 The dependency rule

Arrows point **inward only**:

```
Api ──────► Application ──────► Domain
 │                                ▲
 └──────► Infrastructure ─────────┘
```

| Project | References | Contains |
|---|---|---|
| **Domain** | **nothing** — not EF Core, not ASP.NET, not even a logger | entities, value objects, enums, domain events, business rules, invariants |
| **Application** | Domain | use cases (one folder each), commands, queries, handlers, validators, DTOs, and **interfaces** for what it needs from the outside |
| **Infrastructure** | Application, Domain | EF Core `DbContext`, entity configurations, migrations, repository implementations, HTTP clients, message consumers, outbox wiring |
| **Api** | Application, Infrastructure | endpoints, DI registration, middleware pipeline, `Program.cs`, `appsettings.json` |

### 2.2 Why separate projects at all

Because the compiler then enforces the boundary that a folder cannot.

`Domain` has **no package references**. That single fact means a business
rule *physically cannot* call the database, make an HTTP request, or read
the current time from `DateTime.Now`. If a rule seems to need a database
to express, it is not a domain rule — it is a use case, and it belongs in
`Application`.

`Application` depends on **interfaces**, not implementations:

```csharp
// Application/Abstractions/IBusinessRepository.cs   <- the interface
public interface IBusinessRepository
{
    Task<Business?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<bool> EmailExistsAsync(string email, CancellationToken ct);
    void Add(Business business);
}

// Infrastructure/Persistence/BusinessRepository.cs  <- the implementation
internal sealed class BusinessRepository(TenantDbContext db) : IBusinessRepository
```

That inversion is what makes the use case testable without a database,
and what makes swapping PostgreSQL for something else a change in one
project.

### 2.3 Clean Architecture is the dependency rule, not the project count

This is the part that gets misread. Clean Architecture is **one rule**:

> Dependencies point inward. Business rules know nothing about
> infrastructure.

Four projects are a **mechanism for enforcing** that rule, not the rule
itself. You can honour it perfectly inside a single project with folders,
and you can violate it badly across six projects by referencing EF Core
from `Domain`.

So the projects are priced, not assumed. Four assemblies cost real
things: a build graph, a DI registration spread across files, interfaces
that exist only to be crossed once, and a mapping layer between three
representations of the same row. For `booking` that price buys enormous
protection. For `audit` — which does nothing but append a row it was
handed — it buys nothing at all and is pure ceremony.

**The test.** Does this service *protect invariants*?

1. Are there rules that must hold no matter which use case runs?
2. Is there a state machine with illegal transitions?
3. Does it handle money, capacity, or concurrent access to a scarce thing?
4. Or is it a pure projection of somebody else's events?

Yes to 1–3 → it needs a domain model. Yes to 4 → it does not.

### 2.4 The three tiers

| Tier | Projects | Services |
|---|---|---|
| **Full** | `Domain` · `Application` · `Infrastructure` · `Api` | `identity` · `tenant` · `subscription` · `property` · `pricing` · **`booking`** · `payment` · `stay` |
| **Lean** | `Core` (domain + application) · `Infrastructure` · `Api` | `guest` · `operations` · `content` · `notification` |
| **Minimal** | one project, feature folders | `reporting` · `audit` |

**Full (8 services)** — every one of these protects something that can be
made invalid. `booking` guards capacity against concurrent holds.
`payment` guards money against duplicate captures. `subscription` guards
plan limits. `identity` guards single-use refresh tokens. `property` and
`tenant` guard lifecycle state machines. `pricing` guards rule precedence
and deterministic quotes. `stay` guards folio arithmetic.

**Lean (4 services)** — real use cases and real validation, but few
invariants that survive being expressed in a handler. `guest` is mostly
CRUD plus deduplication and field encryption. `content` is CRUD plus a
publish step. `operations` is a task lifecycle. `notification` has retry
and channel logic but no business state anyone can corrupt. Domain types
live in `Core` alongside the use cases; the dependency rule still holds,
we just stop paying for one assembly boundary.

**Minimal (2 services)** — `reporting` and `audit` own no invariants at
all ([ADR-0006](adr/0006-outbox-at-least-once-idempotent-consumers.md),
[ADR-0008](adr/0008-error-logs-in-each-services-own-database.md)). They
consume events and write rows. One project with `Features/` folders,
event handlers going straight to the `DbContext`. A domain model here
would be an empty ceremony wrapped around an `INSERT`.

**`tenant` is Full despite being small** — four tables and a state
machine would justify Lean, but it is built first (Stage 4) and every
later service is copied from it. Establishing the full pattern on
something small is much cheaper than retrofitting it onto `booking`.

Tiers move **upward** freely: if `guest` grows real invariants, promoting
`Core` into `Domain` + `Application` is a mechanical split, because the
dependency rule was never broken. That is the point of choosing the rule
over the ceremony.

### 2.4b Controllers, not minimal APIs

The HTTP surface is **MVC controllers** with attribute routing, not minimal
API endpoint groups. `[ApiController]` + `[HttpPost("{id:guid}/verify-email")]`,
returning `ActionResult<T>`.

The reason is filters. Validation, idempotency, authorization and audit are
all cross-cutting, and MVC gives them a first-class place to live - an
`IAsyncActionFilter` registered once, applying to every action in all 14
services. In minimal APIs the equivalent is an endpoint filter attached
per-endpoint or per-group, which is the version that gets forgotten on the
fortieth endpoint.

Two consequences worth knowing, both discovered the hard way:

- `[ApiController]` emits its **own** `ValidationProblemDetails` on a
  model-state failure - a second error shape in the same service. It must be
  **replaced** via `ApiBehaviorOptions.InvalidModelStateResponseFactory`, not
  suppressed. Suppressing it stops the short-circuit, so the action then runs
  with a `null` argument and dies of an `ArgumentNullException` - a 500 for
  the caller's typo.
- `SuppressMapClientErrors = true`, so a bare `NotFound()` is not silently
  reshaped into the framework's `ProblemDetails`. Every problem response in
  the system comes from `ProblemDetailsFactory`.

### 2.5 Commands go through the domain, queries do not

Within a Full-tier service, reads and writes are treated differently —
CQRS in the light sense, no separate databases, no event sourcing:

- **Commands** load an aggregate, call a method on it, and save. Rules
  are enforced in one place, and no handler may set state directly.
- **Queries** read straight from the `DbContext` into a DTO, projected in
  SQL. They do **not** load aggregates.

```csharp
// query - no aggregate, no repository, no domain model
var rows = await db.Bookings
    .Where(b => b.PropertyId == propertyId && b.Arrival == date)
    .Select(b => new ArrivalRow(b.Reference, b.GuestName, b.RoomTypeCode))
    .ToListAsync(ct);
```

Forcing a "today's arrivals" list through aggregates means hydrating
hundreds of objects with their children to read three fields each. The
domain model exists to protect *changes*; it has no job on a read path.

---

## 3. Inside `Application` — vertical slices, not layered folders

This is the part that most differs from a typical layered project.

**What most projects do (and we do not):**

```
Controllers/
    BusinessController.cs        <- 900 lines, 14 endpoints
Services/
    BusinessService.cs           <- 1,400 lines, everything
Repositories/
    BusinessRepository.cs
DTOs/
    BusinessDtos.cs              <- 30 classes
```

Grouping by *technical kind*. Adding one feature means touching four
folders, and every file grows forever. Two unrelated features share a
`BusinessService`, so a change to one risks the other.

**What we do — group by feature:**

```
Application/
├── Businesses/
│   ├── RegisterBusiness/
│   │   ├── RegisterBusinessCommand.cs
│   │   ├── RegisterBusinessHandler.cs
│   │   ├── RegisterBusinessValidator.cs
│   │   └── RegisterBusinessResponse.cs
│   ├── VerifyBusinessEmail/
│   ├── SuspendBusiness/
│   └── GetBusinessProfile/
├── PlatformAdmin/
│   └── SearchBusinesses/
└── Abstractions/
    ├── IBusinessRepository.cs
    └── ITenantEventPublisher.cs
```

One feature = one folder = everything that feature needs. To understand
"register business" you open one folder and read four small files. To
delete the feature you delete the folder. Nothing else in the codebase
knows it existed.

This is what people mean by **vertical slice architecture**, and it is
the current mainstream default for exactly this reason: features change
together, technical kinds do not.

---

## 4. Worked example — where does the code for one feature actually go?

Feature: *a business owner registers their business*
(`Goal/Domain.txt` Part 1, first row). Stage 4 builds this.

| File | Project | Job |
|---|---|---|
| `Domain/Businesses/Business.cs` | Domain | the aggregate. Holds `Register()`, `Activate()`, `Suspend()`, and enforces the state machine — a suspended business cannot be suspended twice |
| `Domain/Businesses/BusinessStatus.cs` | Domain | the enum |
| `Domain/Businesses/BusinessRegistered.cs` | Domain | domain event raised inside the aggregate |
| `Domain/Businesses/Errors/BusinessErrors.cs` | Domain | `BusinessErrors.EmailAlreadyUsed` — stable error codes, not thrown strings |
| `Application/Businesses/RegisterBusiness/RegisterBusinessCommand.cs` | Application | the input: `record RegisterBusinessCommand(string LegalName, string OwnerEmail, ...)` |
| `.../RegisterBusinessValidator.cs` | Application | shape validation — email is an email, name is 2–200 chars. Not business rules |
| `.../RegisterBusinessHandler.cs` | Application | **the use case.** Checks the email is unused, builds the aggregate, adds it, commits, writes the outbox row. Returns `Result<RegisterBusinessResponse>` |
| `.../RegisterBusinessResponse.cs` | Application | the output DTO |
| `Infrastructure/Persistence/TenantDbContext.cs` | Infrastructure | the `DbContext` |
| `Infrastructure/Persistence/Configurations/BusinessConfiguration.cs` | Infrastructure | table name, columns, indexes, constraints — EF mapping lives here, never as attributes on the domain entity |
| `Infrastructure/Persistence/Repositories/BusinessRepository.cs` | Infrastructure | implements the interface |
| `Infrastructure/Migrations/*` | Infrastructure | generated. Never hand-edited |
| `Api/Controllers/BusinessesController.cs` | Api | `POST /api/v1/businesses` → sends the command → maps `Result` to `ActionResult` |
| `Api/Program.cs` | Api | wires it all up |
| `tests/.../RegisterBusinessTests.cs` | tests | the rules, no database |
| `tests/.../RegisterBusinessEndpointTests.cs` | tests | real HTTP, real PostgreSQL |

Notice the domain entity has **no attributes, no EF references, no JSON
attributes**. It is plain C# expressing hotel-business rules. That is the
whole objective of the arrangement.

---

## 5. `src/BuildingBlocks/` — shared plumbing, never business logic

Fourteen services need the same outbox, the same tenant filter, the same
error format. Writing that fourteen times is absurd; putting it in one
`Common.dll` is worse, because then every service redeploys whenever
anything changes.

So: **several small, focused libraries.**

```
src/BuildingBlocks/
├── HotelSaas.BuildingBlocks.Domain/          Entity, AggregateRoot,
│                                             ValueObject, IDomainEvent,
│                                             ITenantScoped, IAuditable
├── HotelSaas.BuildingBlocks.Application/     Result<T>, Error, ErrorType,
│                                             PagedResult, ICurrentUser,
│                                             ITenantContext, IClock
├── HotelSaas.BuildingBlocks.Persistence/     EF conventions, tenant query
│                                             filter, audit stamping,
│                                             uuid v7, outbox tables +
│                                             publisher, idempotency store
├── HotelSaas.BuildingBlocks.Messaging/       envelope, publisher,
│                                             idempotent consumer base,
│                                             retry + DLQ policy
├── HotelSaas.BuildingBlocks.Web/             exception middleware,
│                                             ProblemDetails mapping,
│                                             correlation + tenant
│                                             middleware, health checks,
│                                             Swagger
└── HotelSaas.BuildingBlocks.Observability/   log enrichment; OTel at
                                              stage 22
```

Why split rather than one library: a background worker needs `Messaging`
and `Persistence` but not `Web`. `Domain` must reference **nothing**, so
it cannot live in a package that drags in EF Core.

**The hard rule:** if a class in `BuildingBlocks` mentions a booking, a
room, a rate or a tenant's business rules, it is in the wrong place. A
shared library containing a business rule is a shared database with extra
steps — change it and all fourteen services must ship together, which
is the one thing this architecture exists to prevent.

---

## 6. `tests/` — mirrors `src/`

```
tests/
├── HotelSaas.Tenant.UnitTests/          domain rules, no I/O, milliseconds
├── HotelSaas.Tenant.IntegrationTests/   real PostgreSQL via Testcontainers,
│                                        real migrations, real HTTP
└── HotelSaas.BuildingBlocks.Tests/      outbox, tenant filter, idempotency
```

Integration tests use **Testcontainers**, which starts a genuine
PostgreSQL container per test class and throws it away after. Not the EF
in-memory provider — it does not enforce unique constraints, check
constraints or transactions, and constraints are precisely where booking
bugs are caught.

Every service's integration suite contains the tenant-isolation test from
[ADR-0004](adr/0004-tenant-isolation-in-three-layers.md). No exceptions.

---

## 7. The response contract

Two different shapes for two different audiences, and they are kept
separate on purpose.

### Inside the application — `Result<T>`, not exceptions

Exceptions are for *unexpected* failures. "No capacity for those dates"
is not unexpected — [ADR-0002](adr/0002-availability-owned-by-booking.md)
says it is a **normal outcome**. Using exceptions for expected outcomes
makes normal operation expensive and hides real faults in the noise.

```csharp
public sealed record Error(string Code, string Message, ErrorType Type);

public enum ErrorType
{
    Validation,    // 400  malformed input
    Unauthorized,  // 401  not authenticated
    Forbidden,     // 403  authenticated, not permitted
    NotFound,      // 404  absent, or belongs to another tenant
    Conflict,      // 409  state clash - no capacity, duplicate email
    RuleViolation, // 422  understood, but a business rule refuses
    External       // 502  a gateway or upstream service failed
}
```

Error codes are **stable, machine-readable strings** the frontend can
switch on and translate:

```csharp
public static class BookingErrors
{
    public static readonly Error NoCapacity =
        new("booking.no_capacity", "No rooms of this type remain for the selected dates.", ErrorType.Conflict);

    public static readonly Error HoldExpired =
        new("booking.hold_expired", "This hold has expired. Please search again.", ErrorType.Conflict);
}
```

Never a bare `throw new Exception("no rooms")`. A string in a `catch`
block is not an API.

### At the HTTP boundary — RFC 9457 `ProblemDetails`

Success returns **the resource**, with the status code carrying the
meaning: `200` with the body, `201` + `Location`, `204` for no content.

Failure returns `application/problem+json`:

```json
{
  "type": "https://hotelsaas.dev/errors/booking/no_capacity",
  "title": "Conflict",
  "status": 409,
  "detail": "No rooms of this type remain for the selected dates.",
  "instance": "/api/v1/bookings/holds",
  "code": "booking.no_capacity",
  "errorId": "01931f2e-8c4a-7d31-b8f2-4e9a1c7d0b55",
  "correlationId": "01931f2e-7a11-7c02-9d3e-2b6f8a1e4c90",
  "traceId": "00-4bf92f...-01"
}
```

Validation failures add a per-field map:

```json
{
  "status": 400, "code": "validation_failed",
  "errors": { "ownerEmail": ["Not a valid email address."],
              "arrival":    ["Arrival must be before departure."] }
}
```

Lists get a pagination envelope, because pagination genuinely is metadata
about the response rather than part of the resource:

```json
{ "items": [ ... ], "nextCursor": "eyJpZCI6...", "hasMore": true }
```

### Why not `{ "success": true, "data": ..., "message": "" }`

It is common, and it fights HTTP. The status code already says whether
the call succeeded, so clients end up checking two places and eventually
trust the wrong one. It breaks HTTP caching and conditional requests,
makes generated OpenAPI clients return `Response<Response<T>>`, and turns
every error into a `200` that monitoring reports as healthy. Standard
status codes plus `ProblemDetails` is what ASP.NET Core, Azure, Stripe
and GitHub all do.

`Result<T>` internally, `ProblemDetails` at the edge. One mapping, in one
place:

```csharp
// Api/Extensions/ResultExtensions.cs
public static IResult ToHttpResult<T>(this Result<T> result) =>
    result.IsSuccess
        ? Results.Ok(result.Value)
        : ProblemDetailsFactory.From(result.Error);
```

---

## 8. Global exception handling and the `error_logs` table

Anything that reaches the top of the pipeline is, by definition, a bug or
an outage — a `Result` would have carried an expected failure. So it is
caught in exactly one place, recorded, and returned as a safe response.

See [ADR-0008](adr/0008-error-logs-in-each-services-own-database.md) for
why the table lives in each service's own database rather than a shared
one.

### The table

Created by `BuildingBlocks.Persistence` migrations in **every** service's
own database:

```sql
create table error_logs (
    id                  uuid        primary key,          -- uuid v7
    error_id            uuid        not null,             -- shown to the user
    occurred_at         timestamptz not null default now(),

    service_name        text        not null,             -- 'booking'
    environment         text        not null,             -- 'Development'
    machine_name        text        not null,             -- pod / container
    version             text,                             -- assembly version

    correlation_id      uuid,                             -- ties to the request chain
    causation_id        uuid,                             -- if triggered by a message
    tenant_id           uuid,                             -- null for platform scope
    user_id             uuid,

    source_kind         text        not null,             -- 'http' | 'consumer' | 'job'
    http_method         text,
    path                text,
    query_string        text,
    status_code         integer,
    duration_ms         integer,
    message_type        text,                             -- for consumers

    exception_type      text        not null,             -- fully qualified
    message             text        not null,
    stack_trace         text,
    inner_exceptions    jsonb,                            -- the full chain

    -- where it actually happened, in OUR code
    fault_assembly      text,
    fault_type          text,
    fault_method        text,
    fault_file          text,
    fault_line          integer,

    request_headers     jsonb,                            -- redacted
    fingerprint         text        not null              -- for grouping
);

create index ix_error_logs_occurred_at   on error_logs (occurred_at desc);
create index ix_error_logs_correlation   on error_logs (correlation_id);
create index ix_error_logs_tenant        on error_logs (tenant_id, occurred_at desc);
create index ix_error_logs_fingerprint   on error_logs (fingerprint, occurred_at desc);
```

**`fault_*` is the field that answers "where did it break".** The top
stack frame is usually framework code (`Npgsql.NpgsqlConnector...`),
which tells you nothing. So we walk the stack and take the first frame
belonging to a `HotelSaas.*` assembly:

```csharp
static FaultLocation? LocateFault(Exception ex)
{
    var trace = new StackTrace(ex, fNeedFileInfo: true);
    foreach (var frame in trace.GetFrames())
    {
        var method = frame.GetMethod();
        var assembly = method?.DeclaringType?.Assembly.GetName().Name;
        if (assembly?.StartsWith("HotelSaas.", StringComparison.Ordinal) != true)
            continue;

        return new FaultLocation(
            Assembly: assembly,
            Type:     method!.DeclaringType!.FullName!,
            Method:   method.Name,
            File:     frame.GetFileName(),          // needs the .pdb
            Line:     frame.GetFileLineNumber());
    }
    return null;
}
```

`fingerprint` is a hash of `exception_type + fault_type + fault_method`,
so *"this same bug fired 4,000 times"* is one query rather than 4,000
rows to read. Grouping is what makes an error table usable instead of a
landfill.

### The middleware

```csharp
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger,
    IErrorLogWriter errorLog,
    IHostEnvironment env)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var errorId = Uuid7.NewGuid();

            // 1. structured log to stdout. ALWAYS works, even if the database is the problem.
            logger.LogError(ex,
                "Unhandled exception {ErrorId} on {Method} {Path} (correlation {CorrelationId})",
                errorId, context.Request.Method, context.Request.Path,
                context.GetCorrelationId());

            // 2. persist. Never throws, never blocks for long.
            errorLog.Enqueue(ErrorLogEntry.From(context, ex, errorId));

            // 3. respond safely.
            await WriteProblemDetailsAsync(context, ex, errorId, env);
        }
    }
}
```

Four details that matter, and each is a real bug avoided:

**It must never use the failed request's `DbContext`.** After an
exception, the `DbContext` may hold a rolled-back transaction or tracked
entities in a broken state, and writing through it throws again — a `500`
inside a `500`, and the original error is lost. The writer opens its
**own connection**, from its own scope.

**It must never throw.** An error logger that fails during error handling
takes down the response. `Enqueue` cannot fail; the background writer
catches everything and falls back to stdout.

**It must not block the response.** A sick database is often *why* we are
here, and awaiting an insert against it would hang every failing request
until timeout. So entries go into a **bounded `Channel<ErrorLogEntry>`**
drained by a background writer that batches inserts, with a short command
timeout. If the channel is full — an error storm — the oldest entries are
dropped and a counter is incremented. Losing some error rows is
acceptable; falling over because we could not log is not.

**It must not log expected outcomes.** Only unhandled exceptions and
`5xx` reach this table. A `409 booking.no_capacity` is a normal result of
a race and travels as a `Result`, never an exception — otherwise the
table fills with successful business behaviour and the real faults become
invisible.

Consumers and background jobs share the same writer via a
`ConsumerExceptionFilter` and `JobExceptionFilter`, with
`source_kind = 'consumer' | 'job'` — otherwise every failure outside an
HTTP request would be invisible, which is most of them in an
event-driven system.

### Why a table at all

Structured stdout logs are the primary record, and at Stage 22 they get
proper tooling. But `Goal/Saas.txt` §2 lists **Support** and **System
Monitoring** among platform-admin capabilities — so "show me this
tenant's recent errors" is a **product feature**, not just plumbing, and
a product feature needs a queryable table with a tenant column.

Retention: pruned by a background job, 30 days locally. This table only
ever grows, and an unpruned error table becomes the largest in the
database.

### Middleware order

Order is behaviour, not preference:

```csharp
app.UseMiddleware<CorrelationIdMiddleware>();     // 1. so everything below can log it
app.UseMiddleware<ExceptionHandlingMiddleware>(); // 2. wraps everything after it
app.UseAuthentication();                          // 3. who
app.UseAuthorization();                           // 4. may they
app.UseMiddleware<TenantContextMiddleware>();     // 5. needs claims from 3
app.MapControllers();
```

Correlation is first so an exception already has an id to log. Exception
handling is second so it catches auth failures too. Tenant context comes
after authentication because it reads validated claims — and never a
header ([ADR-0004](adr/0004-tenant-isolation-in-three-layers.md)).

---

## 9. `infra/docker/` — running the system

The services are code. **What they run on is also code**, versioned in the
same repository, so that a checkout of tag `stage-07` starts exactly the
infrastructure stage 7 expected. That is the whole reason this folder
exists rather than instructions in a README.

```
infra/
├── docker/
│   ├── docker-compose.yml               base: postgres, pgadmin
│   ├── docker-compose.infra.yml         kong, rabbitmq, redis (added by stage)
│   ├── docker-compose.services.yml      the 14 services
│   ├── docker-compose.override.yml      local-only: hot reload, exposed ports
│   ├── .env.example                     every variable, no real values
│   ├── postgres/
│   │   └── init/
│   │       ├── 01-create-databases.sh   creates hs_* databases
│   │       └── 02-create-roles.sh       one role per service, no cross grants
│   ├── kong/kong.yml                    declarative routes (stage 5)
│   ├── rabbitmq/definitions.json        exchanges, queues, DLQs (stage 7)
│   ├── prometheus/prometheus.yml        (stage 22)
│   └── grafana/dashboards/              (stage 22)
└── scripts/
    ├── up.ps1                           start a profile
    ├── down.ps1                         stop, optionally wipe volumes
    ├── migrate-all.ps1                  run every service's migrations
    └── reset-db.ps1                     drop and recreate from scratch
```

### Why compose is split across files

Docker Compose merges files, so the split maps to intent:

- `docker-compose.yml` — what every stage needs
- `.infra.yml` — the middleware, added at the stage that needs it
- `.services.yml` — our own applications
- `.override.yml` — loaded automatically, holds local-only conveniences
  and never ships anywhere

### Profiles solve "fourteen services on a laptop"

Each service declares a profile, so a stage starts only what it needs:

```yaml
services:
  booking-api:
    profiles: ["booking", "full"]
```

```powershell
./infra/scripts/up.ps1 -Profile core      # postgres + identity + tenant
./infra/scripts/up.ps1 -Profile booking   # + property, pricing, booking, rabbitmq
./infra/scripts/up.ps1 -Profile full      # everything
```

Without profiles, `docker compose up` at Stage 16 would try to start
fourteen .NET processes, PostgreSQL, RabbitMQ, Redis, Kong, Prometheus
and Grafana at once. With them, Stage 4 starts two containers.

### `postgres/init/` is what makes ADR-0003 real

Scripts placed in a PostgreSQL image's init directory run once, on first
start of an empty volume. Ours create the fourteen databases **and a
separate role per service with no grants on the others** — so a
cross-service join is a permission error rather than a code-review
argument.

### Why each `Dockerfile` lives with its service, not here

`src/services/booking/Dockerfile` describes **how to build that one
service**, changes when that service's projects change, and is that
service's business. `infra/docker/` describes **how things run together**.
Keeping the two apart means adding a service does not touch shared infra
files, and it is the layout every CI system expects.

Each Dockerfile is multi-stage — SDK image restores and publishes, then a
runtime-only image receives the output — so the shipped image has no
compiler in it.

---

## 10. Namespaces and naming

| Thing | Pattern | Example |
|---|---|---|
| project | `HotelSaas.<Service>.<Layer>` | `HotelSaas.Booking.Application` |
| namespace | matches folders | `HotelSaas.Booking.Application.Holds.CreateHold` |
| controller | `<Plural>Controller` | `BusinessesController`, `PlatformBusinessesController` |
| shared library | `HotelSaas.BuildingBlocks.<Concern>` | `HotelSaas.BuildingBlocks.Web` |
| database | `hs_<service>` | `hs_booking` |
| container | `hs-<service>-api` | `hs-booking-api` |
| exchange | `<service>.events` | `booking.events` |
| queue | `<consumer>.<publisher>-events` | `reporting.booking-events` |

---

## 11. Summary

| Folder | Purpose |
|---|---|
| `src/services/<name>/Domain` | business rules, zero dependencies (Full tier only — §2.4) |
| `src/services/<name>/Application` | use cases, one folder per feature |
| `src/services/<name>/Infrastructure` | EF Core, migrations, clients, consumers |
| `src/services/<name>/Api` | endpoints, DI, middleware |
| `src/BuildingBlocks/*` | plumbing shared by all fourteen, never business logic |
| `tests/` | unit tests, plus integration tests on real PostgreSQL |
| `infra/docker/` | how the system runs, versioned with the code |
| `infra/scripts/` | the commands, so nobody memorises them |
