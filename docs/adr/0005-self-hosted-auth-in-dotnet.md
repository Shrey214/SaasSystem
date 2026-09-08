# 0005. Authentication and authorization built in .NET, no external IdP

Date: 2026-09-08
Status: Accepted
Supersedes: the Keycloak entry originally in `Goal/TechStack.txt`

## Context

`Goal/TechStack.txt` originally specified Keycloak for authentication and
RBAC. The user decided against it and asked for auth to be built in
.NET 10 instead.

The requirement is more than "log a user in". `Goal/Saas.txt` §5 and
`Goal/Domain.txt` Part 1 require a user to be a member of a business, to
hold roles and permissions, **and** to have access to a specific subset
of that business's properties:

```
User + Tenant + Role + Permissions + Property Access
```

That last dimension is the hard one, and it is not something an
off-the-shelf identity provider models well anyway — it would have lived
in our own service regardless.

## Decision

The `identity` service owns both authentication and authorization, and is
the only token issuer.

- **ASP.NET Core Identity** for the user store: password hashing,
  lockout, email and phone confirmation, and 2FA later. Not written by
  hand — this part is solved, and hand-rolled password hashing is a
  liability.
- **Our own token endpoint**: `POST /auth/login`, `/auth/refresh`,
  `/auth/logout`.
- **RS256** signing. The private key exists only in `identity`; the
  public key is published at `/.well-known/jwks.json`.
- **Access token claims**: `sub`, `tenant_id`, `scope_type`
  (platform | tenant | property), `roles`, `perms`, `props` (authorized
  property ids), `perms_version`, `jti`, `exp`. Lifetime 10–15 minutes.
- **Rotating refresh tokens**, stored hashed, one row per device,
  individually revocable.
- **Every service validates the token itself** with `AddJwtBearer`
  against the cached JWKS. There is no per-request call to `identity`.
- **Kong** does routing, rate limiting and a coarse signature check. It is
  not the security boundary; a service must never trust a caller merely
  because the request arrived through the gateway.

## Consequences

- One fewer container, one fewer admin UI, one fewer thing to configure.
  Realm/client/mapper configuration is replaced by C# we can read.
- `identity` being down does not log anyone out: tokens validate locally
  against cached JWKS. Login and refresh stop working
  (`docs/03-communication.md` §10).
- **We now own the staleness problem.** Permissions and property access
  live inside a token, so a revocation does not take effect until the
  token expires. Mitigated by: 10–15 minute lifetimes; immediate refresh
  revocation, so the session dies at the next refresh; a `perms_version`
  claim checked against the current value; and an
  `identity.access.revoked.v1` event for services that cache decisions.
  An external IdP would have handed us the same problem — we now own both
  ends of it, which is better for the learning goal
  (`Goal/LearningGoal.txt`).
- We own key management: generation, storage outside the repository, and
  rotation with an overlap window during which both keys validate.
- Security-critical code is ours, so it needs tests: token tampering,
  expired token, wrong audience, revoked refresh token, cross-tenant
  claim, and property claim not covering the requested property.
- No OIDC flows means no third-party integrations (channel managers,
  social login) until we add them.

## Alternatives rejected

**Keycloak.** The user's decision, and the trade-off is real either way:
it would have given us standards-compliant OIDC, token exchange and an
admin UI for free, at the cost of a container, a configuration language
to learn, and property-level access still living in our own service. Not
rejected on technical grounds.

**Duende IdentityServer.** Free for development, licensed for commercial
use. Against `Goal/TechStack.txt`'s free-only constraint, and a licence
question we should not inherit.

**OpenIddict.** Free, MIT, a genuinely good full OIDC server that runs
inside our own .NET service. Rejected **for now** as more machinery than
a first-party frontend needs. This is the upgrade path the moment real
OIDC flows are required — same service, no migration of the user store.

**Cookie/session authentication with a shared session store.** Simpler,
and wrong for this shape: every service would need to hit the session
store on every request, making `identity` a synchronous dependency of
all fourteen services and undoing `docs/03-communication.md` §1.

**Long-lived tokens with no refresh.** Removes the refresh complexity and
makes revocation impossible. Unacceptable when "Remove Staff Access" is
an explicit requirement in `Goal/Domain.txt` Part 1.
