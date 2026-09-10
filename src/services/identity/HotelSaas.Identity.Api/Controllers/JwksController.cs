using System.Security.Cryptography;
using HotelSaas.Identity.Application.Abstractions;
using HotelSaas.Identity.Domain.Access;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;

namespace HotelSaas.Identity.Api.Controllers;

// The public half of every key still accepting verification.
//
// This endpoint is what lets the other thirteen services validate a token
// WITHOUT calling identity on every request. They fetch it once, cache it,
// and check signatures locally - which is why identity being down does not
// log anyone out, and why Kong is not the security boundary.
[ApiController]
[AllowAnonymous]
[Produces("application/json")]
public sealed class JwksController(ISigningKeyStore keys) : ControllerBase
{
    // The well-known path. Not under /api/v1 - it is a standard location
    // (RFC 8615) and every JWT library looks for it here.
    [HttpGet("/.well-known/jwks.json")]
    [EndpointSummary("Public keys for verifying access tokens")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> Get(CancellationToken cancellationToken)
    {
        IReadOnlyList<SigningKey> verification = await keys.GetVerificationKeysAsync(cancellationToken);

        List<object> jwks = [];

        foreach (SigningKey key in verification)
        {
            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(key.PublicKeyPem);

            RSAParameters parameters = rsa.ExportParameters(includePrivateParameters: false);

            jwks.Add(new
            {
                kty = "RSA",
                use = "sig",
                alg = "RS256",
                kid = key.KeyId,

                // Modulus and exponent, base64url. This IS the public key -
                // there is nothing secret here, which is the whole point.
                n = Base64UrlEncoder.Encode(parameters.Modulus!),
                e = Base64UrlEncoder.Encode(parameters.Exponent!),
            });
        }

        // Cached for five minutes. Long enough that thirteen services are
        // not hammering this, short enough that a newly rotated key is
        // picked up without a restart.
        Response.Headers.CacheControl = "public, max-age=300";

        return Ok(new { keys = jwks });
    }
}
