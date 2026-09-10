using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace HotelSaas.BuildingBlocks.Domain;

// Single-use secrets that travel in a link: email verification, staff
// invitations, refresh tokens.
//
// Lives here because tenant and identity both need exactly this and the
// rules are easy to get subtly wrong. Duplicating it would mean two
// implementations drifting apart on the parts that matter.
public static class SecureToken
{
    private const int TokenBytes = 32;

    // 32 bytes of CSPRNG output, base64url encoded (43 chars, URL-safe with
    // no escaping needed).
    //
    // RandomNumberGenerator, never Random or Guid. These are bearer
    // credentials - whoever holds one can act - and Random is seeded and
    // predictable while Guid is unique but not unguessable. Uniqueness is
    // not the property we need.
    public static (string Token, string Hash) Create()
    {
        string token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        return (token, HashOf(token));
    }

    // SHA-256, deliberately NOT bcrypt or Argon2.
    //
    // Password hashers exist to slow down guessing a LOW-entropy secret.
    // This has 256 bits, so brute force was never on the table and there is
    // nothing to slow down. What matters is that the stored value cannot be
    // turned back into a working token, and a fast hash does that while
    // keeping verification cheap.
    public static string HashOf(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    // Constant time, so the comparison itself does not leak how much of a
    // guess was right.
    public static bool Matches(string token, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(HashOf(token)),
            Encoding.UTF8.GetBytes(storedHash));
    }
}
