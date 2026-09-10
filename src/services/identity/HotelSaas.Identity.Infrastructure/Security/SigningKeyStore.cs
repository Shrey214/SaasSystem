using System.Security.Cryptography;
using HotelSaas.BuildingBlocks.Application;
using HotelSaas.Identity.Application.Abstractions;
using HotelSaas.Identity.Domain.Access;
using HotelSaas.Identity.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HotelSaas.Identity.Infrastructure.Security;

// Holds the RS256 key pairs.
//
// RS256 and not HS256: with a symmetric key, every service that VERIFIES a
// token also holds the key that could FORGE one. Thirteen services holding
// the signing secret is thirteen places to leak it. With RS256 the private
// key never leaves this service and the others only ever get the public
// half, from JWKS.
internal sealed class SigningKeyStore(IdentityServiceDbContext db, IClock clock) : ISigningKeyStore
{
    private const int KeySizeBits = 2048;

    public async Task<SigningKey> GetOrCreateActiveAsync(CancellationToken cancellationToken)
    {
        SigningKey? active = await db.SigningKeys
            .Where(k => k.RetiredAt == null)
            .OrderByDescending(k => k.ActivatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (active is not null)
        {
            return active;
        }

        // Generated on first use rather than failing.
        //
        // A fresh deployment with no key can issue no tokens at all, so
        // refusing to start would be worse than self-provisioning. The
        // trade-off is real and worth naming: two instances booting at the
        // same second could both generate one. That is harmless - both are
        // valid, both get published in JWKS, and the newest wins for
        // signing. It is only a problem if it happens repeatedly, which the
        // unique index on key_id would surface.
        using RSA rsa = RSA.Create(KeySizeBits);

        SigningKey created = SigningKey.Create(
            keyId: Guid.CreateVersion7().ToString("N")[..16],
            publicKeyPem: rsa.ExportSubjectPublicKeyInfoPem(),
            privateKeyPem: rsa.ExportPkcs8PrivateKeyPem(),
            clock.UtcNow);

        db.SigningKeys.Add(created);
        await db.SaveChangesAsync(cancellationToken);

        return created;
    }

    // Everything still accepting verification, INCLUDING retired keys.
    //
    // A retired key has stopped signing but tokens it signed are valid
    // until they expire, so dropping it from JWKS would break every session
    // issued in the last 15 minutes. That overlap is the whole reason
    // signing_keys has three timestamps instead of a status column.
    public async Task<IReadOnlyList<SigningKey>> GetVerificationKeysAsync(CancellationToken cancellationToken)
        => await db.SigningKeys
            .Where(k => k.ExpiredAt == null)
            .OrderByDescending(k => k.ActivatedAt)
            .ToListAsync(cancellationToken);
}
