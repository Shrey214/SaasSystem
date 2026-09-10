using System.Security.Claims;
using System.Security.Cryptography;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.Identity.Application.Abstractions;
using HotelSaas.Identity.Domain.Access;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace HotelSaas.Identity.Infrastructure.Security;

// Builds and signs the access token.
//
// The claim set here IS the contract every other service reads
// (docs/01-bounded-contexts.md 3 calls it a published language). Adding a
// claim is safe; renaming or removing one breaks thirteen services, so it
// changes additively or not at all.
internal sealed class JwtAccessTokenIssuer(
    ISigningKeyStore keys,
    IOptions<JwtOptions> options,
    IClock clock) : IAccessTokenIssuer
{
    private readonly JwtOptions _options = options.Value;

    public async Task<AccessToken> IssueAsync(
        UserRecord user,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        SigningKey key = await keys.GetOrCreateActiveAsync(cancellationToken);

        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(key.PrivateKeyPem);

        // KeyId becomes the `kid` header. Without it a verifier holding two
        // published keys has to try each one, and rotation stops being
        // transparent.
        RsaSecurityKey securityKey = new(rsa.ExportParameters(includePrivateParameters: true))
        {
            KeyId = key.KeyId,
        };

        DateTimeOffset now = clock.UtcNow;
        DateTimeOffset expiresAt = now.AddMinutes(_options.AccessTokenMinutes);

        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Uuid7.New().ToString()),
            new("email", user.Email),
            new("name", user.FullName),

            // platform | tenant. Read by every service to decide whether
            // the request is tenant-scoped at all.
            new("scope_type", user.ScopeType),

            // Compared against users.permissions_version. A token carrying
            // an older value is known-stale, which is the only lever that
            // beats waiting for expiry (ADR-0005).
            new("perms_version", user.PermissionsVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ];

        // Absent for a platform admin, and absent means platform scope -
        // never "all tenants". ITenantContext returns null and the query
        // filter then matches nothing, so the failure direction is closed.
        if (tenantId is { } tenant)
        {
            claims.Add(new Claim("tenant_id", tenant.ToString()));
        }

        // roles, perms and props are deliberately NOT here yet. Stage 5b
        // adds the authorization model that fills them. Until then every
        // service sees no roles and no property access, and fails closed -
        // which is why /platform has no [Authorize] policy on it either.

        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256),
        };

        string token = new JsonWebTokenHandler().CreateToken(descriptor);

        return new AccessToken(token, expiresAt, key.KeyId);
    }
}
