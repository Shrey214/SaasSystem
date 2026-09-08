# Stage 2 — Local infrastructure

PostgreSQL 17 + pgAdmin in Docker Compose, with 13 databases and one
login role each.

**Result:** 13 databases, 13 roles, and **156 of 156 cross-service
connection attempts refused.**

---

## What the stage was actually for

Creating 13 databases is trivial. The stage only mattered because of one
line in `02-create-databases.sh`:

```sql
revoke connect on database hs_booking from public;
```

PostgreSQL grants `CONNECT` on every new database to `PUBLIC`. Without
that revoke, `hs_booking_user` could connect to `hs_property` and read
every row — and everything would still *look* correct: 13 databases, 13
roles, containers healthy, pgAdmin showing a tidy tree.

That is the trap worth remembering. A setup with no isolation at all is
visually indistinguishable from a correct one. It is only distinguishable
by **trying the thing that should fail**, which is why
`verify-isolation.ps1` tests all 156 forbidden pairs rather than just
confirming the 13 allowed ones. Check 2 alone would have passed on a
completely open cluster.

---

## Four things that broke

### 1. Docker CLI works while the engine is down

`docker --version` answered happily at Stage 0, so the environment check
recorded Docker as available. `docker compose up` then failed with
`failed to connect to the docker API at npipe:...dockerDesktopLinuxEngine`.

The CLI is a separate binary from the daemon. **`docker info` is the
check that means anything** — it round-trips to the engine. `--version`
proves nothing.

### 2. Compose created a container before its image finished pulling

```
Container hs-pgadmin  Error response from daemon: No such image: dpage/pgadmin4:latest
```

`docker pull dpage/pgadmin4:latest` on its own then worked immediately, so
the image was fine. Compose pulled `postgres:17` (114MB) and pgAdmin in
parallel and began creating containers before the second pull completed.

Not worth engineering around locally — the retry succeeded. Worth knowing
because it looks exactly like a missing or misnamed image, which sends you
hunting for the wrong bug. In CI the fix is `docker compose pull` as an
explicit step before `up`.

### 3. `.local` is a reserved TLD, and pgAdmin enforces it

`PGADMIN_DEFAULT_EMAIL=admin@hotelsaas.local` put the container in a
crash loop:

```
'admin@hotelsaas.local' does not appear to be a valid email address.
The part after the @-sign is a special-use or reserved name that cannot be used with email.
```

`.local` is reserved for mDNS by RFC 6762, and pgAdmin's validator
rejects it even with `CHECK_EMAIL_DELIVERABILITY: False` — a separate
`GLOBALLY_DELIVERABLE` check catches it. Changed to `.dev`. `.test`,
`.example`, `.invalid` and `.localhost` (RFC 2606) are likely to be
refused for the same reason, so `.local` is a poor default for any
containerised dev tool.

The container was in `restarting` state while `postgres` reported
`healthy` — so `docker compose ps` looked *mostly* fine, and only
`docker logs` explained anything.

### 4. PowerShell 5.1 treats native stderr as a terminating error

The most costly one, and it has nothing to do with Docker.

```powershell
$ErrorActionPreference = 'Stop'
docker compose up -d          # <- aborts the script
```

`docker compose` writes its progress (`Container hs-postgres Running`) to
**stderr**, not stdout. Under `'Stop'`, PowerShell 5.1 wraps each native
stderr line in an `ErrorRecord` and terminates the script — *even when the
command exited 0*. The script died reporting `NativeCommandError` on a
command that had actually succeeded.

Two fixes, both needed:

```powershell
# 1. do not let native stderr be fatal; check exit codes explicitly
$ErrorActionPreference = 'Continue'

# 2. route output through the pipeline so progress is not rendered as red errors
docker compose up -d 2>&1 | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw "exit $LASTEXITCODE" }
```

This will apply to every script in `infra/scripts/` for the rest of the
project, and to `dotnet` calls from Stage 3 onward.

---

## One bug that would have surfaced much later

`verify-isolation.ps1` printed every check as passing and then exited
with code **2**.

Check 3 deliberately runs `psql` connections that *must* fail. The last
one sets `$LASTEXITCODE = 2`, and a PowerShell script's exit code is the
last one set. So a fully passing verification reported failure — which is
invisible when you read the output, and fatal the moment this runs in CI
at Stage 24.

Fix: an explicit `exit 0` after the success message. Worth generalising —
**any script whose normal operation includes an expected command failure
must set its own exit code.**

---

## Deliberate local shortcuts

Written down so they are not mistaken for a production design:

| Shortcut | Real deployment |
|---|---|
| One shared `HS_SERVICE_PASSWORD` for all 13 roles | one secret per service, from a secret manager |
| Password interpolated into SQL by the init script | provisioned outside the container image |
| One PostgreSQL instance holding 13 databases | separate instances where isolation or scaling demands it |
| `pgadmin4:latest` | pinned by digest |
| `PGADMIN_CONFIG_SERVER_MODE: False` (no login) | pgAdmin not deployed at all |

---

## Two things to remember later

**Init scripts run exactly once**, on first start of an *empty* volume.
Editing `postgres/init/*.sh` afterwards does nothing. `up.ps1 -Fresh`
wipes the volume so they re-run. This will be forgotten at least once.

**RLS will not apply to the service's own role.** ADR-0004 layer 3 puts
row-level security on `bookings`, `guests`, `payments`. But
`hs_booking_user` **owns** those tables, and a table owner bypasses RLS
unless the table is declared:

```sql
alter table bookings force row level security;
```

Without `force`, layer 3 will appear to be enabled and protect nothing —
the same failure mode as this stage's `revoke connect`, and equally
invisible. The migration that enables RLS must include `force`, and the
test must run as the service role, not as `postgres`.
