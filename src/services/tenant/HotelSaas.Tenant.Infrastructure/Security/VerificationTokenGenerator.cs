using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using HotelSaas.Tenant.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HotelSaas.Tenant.Infrastructure.Security;

internal sealed class VerificationTokenGenerator : IVerificationTokenGenerator
{
    // 32 bytes of CSPRNG output. Base64url so it survives being pasted into
    // a URL without escaping.
    private const int TokenBytes = 32;

    public (string Token, string Hash) Generate()
    {
        // RandomNumberGenerator, never Random or Guid: a verification token
        // is a bearer credential, and a predictable one is an account
        // takeover.
        byte[] bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        string token = Base64Url.EncodeToString(bytes);

        return (token, Hash(token));
    }

    // SHA-256, not a password hash.
    //
    // Argon2/bcrypt exist to slow down guessing a LOW-entropy secret. This
    // token has 256 bits of entropy, so brute force is not the threat -
    // there is nothing to slow down. What matters is that the stored value
    // cannot be turned back into a working link, and a fast hash does that
    // while keeping verification cheap.
    public string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(hash);
    }
}

// Stage 4 stand-in for sending the email.
//
// Stage 16 replaces this with a notification event and nothing above it
// changes - that is the entire reason the use cases depend on the interface
// rather than on an email client.
internal sealed class LoggingVerificationDispatcher(
    IHostEnvironment environment,
    ILogger<LoggingVerificationDispatcher> logger) : IVerificationDispatcher
{
    public bool ExposesTokens => environment.IsDevelopment();

    public Task DispatchAsync(
        Guid businessId,
        string ownerEmail,
        string token,
        CancellationToken cancellationToken)
    {
        if (environment.IsDevelopment())
        {
            VerificationLog.TokenIssued(logger, businessId, ownerEmail, token);
        }
        else
        {
            // Outside development the token is never written anywhere. A
            // token in a log file is a token an operator can use.
            VerificationLog.TokenIssuedRedacted(logger, businessId);
        }

        return Task.CompletedTask;
    }
}

internal static partial class VerificationLog
{
    [LoggerMessage(
        EventId = 3101,
        Level = LogLevel.Warning,
        Message = "DEV ONLY - verification token for business {BusinessId} ({OwnerEmail}): {Token}")]
    public static partial void TokenIssued(ILogger logger, Guid businessId, string ownerEmail, string token);

    [LoggerMessage(
        EventId = 3102,
        Level = LogLevel.Information,
        Message = "Verification token issued for business {BusinessId} (value not logged)")]
    public static partial void TokenIssuedRedacted(ILogger logger, Guid businessId);
}
