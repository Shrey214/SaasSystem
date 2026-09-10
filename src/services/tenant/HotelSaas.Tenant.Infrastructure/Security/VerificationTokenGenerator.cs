using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.Tenant.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HotelSaas.Tenant.Infrastructure.Security;

internal sealed class VerificationTokenGenerator : IVerificationTokenGenerator
{
    // The rules live in BuildingBlocks.SecureToken now - identity needs the
    // same ones for staff invitations and refresh tokens, and two copies
    // would drift on exactly the details that matter.
    public (string Token, string Hash) Generate() => SecureToken.Create();

    public string Hash(string token) => SecureToken.HashOf(token);
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
