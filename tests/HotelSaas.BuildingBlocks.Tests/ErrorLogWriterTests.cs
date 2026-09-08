using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.BuildingBlocks.Persistence.Errors;
using HotelSaas.BuildingBlocks.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HotelSaas.BuildingBlocks.Tests;

// End-to-end for ADR-0008.
//
// Two things are being checked that nothing else can catch:
//
//  1. The background writer builds its INSERT as raw SQL, by hand, while
//     the schema comes from EF's snake_case convention. Nothing makes those
//     agree at compile time - if a column is renamed, the insert breaks at
//     runtime, on the error path, where it is least likely to be noticed.
//
//  2. fault_line is populated. Without it the table records that something
//     broke but not where, which is most of the value gone.
[Collection(PostgresCollection.Name)]
public sealed class ErrorLogWriterTests(PostgresFixture postgres)
{
    [Fact]
    public async Task AnEnqueuedEntry_ReachesTheTable_WithItsFaultLocation()
    {
        Guid errorId = Uuid7.New();
        Guid correlationId = Uuid7.New();
        Guid tenantId = Uuid7.New();

        InvalidOperationException caught = CatchDeliberateFailure();
        FaultLocator.FaultLocation? fault = FaultLocator.Locate(caught);

        // The fault must be located in OUR assembly, not in the framework.
        fault.ShouldNotBeNull();
        fault.Assembly.ShouldStartWith("HotelSaas.");
        fault.Method.ShouldBe(nameof(ThrowDeliberately));
        fault.Line.ShouldNotBeNull();
        fault.Line!.Value.ShouldBeGreaterThan(0);

        ErrorLogEntry entry = new()
        {
            ErrorId = errorId,
            OccurredAt = DateTimeOffset.UtcNow,
            ServiceName = "test-service",
            Environment = "Test",
            MachineName = "test-machine",
            CorrelationId = correlationId,
            TenantId = tenantId,
            SourceKind = "http",
            HttpMethod = "POST",
            Path = "/api/v1/things",
            StatusCode = 500,
            DurationMs = 12,
            ExceptionType = caught.GetType().FullName!,
            Message = caught.Message,
            StackTrace = caught.StackTrace,
            InnerExceptions = FaultLocator.DescribeInnerExceptions(caught),
            FaultAssembly = fault.Assembly,
            FaultType = fault.Type,
            FaultMethod = fault.Method,
            FaultFile = fault.File,
            FaultLine = fault.Line,
            Fingerprint = FaultLocator.Fingerprint(caught, fault),
        };

        await RunWriterAsync(entry);

        // Read back through EF, against the schema EF itself generated.
        await using TestDbContext read = postgres.CreateContext(new MutableTenantContext());

        ErrorLogEntry stored = await read.ErrorLogs
            .IgnoreQueryFilters()
            .SingleAsync(e => e.ErrorId == errorId);

        stored.ServiceName.ShouldBe("test-service");
        stored.CorrelationId.ShouldBe(correlationId);
        stored.TenantId.ShouldBe(tenantId);
        stored.SourceKind.ShouldBe("http");
        stored.StatusCode.ShouldBe(500);
        stored.ExceptionType.ShouldBe("System.InvalidOperationException");
        stored.FaultMethod.ShouldBe(nameof(ThrowDeliberately));
        stored.FaultLine.ShouldBe(fault.Line);
        stored.InnerExceptions.ShouldNotBeNull();
        stored.Fingerprint.Length.ShouldBe(16);
    }

    [Fact]
    public void TheSameBugTwice_ProducesTheSameFingerprint()
    {
        // What makes "this fired 4,000 times" one row to read rather than
        // 4,000. Deliberately excludes the message, which usually carries
        // ids and would make every occurrence unique.
        InvalidOperationException first = CatchDeliberateFailure();
        InvalidOperationException second = CatchDeliberateFailure();

        string a = FaultLocator.Fingerprint(first, FaultLocator.Locate(first));
        string b = FaultLocator.Fingerprint(second, FaultLocator.Locate(second));

        a.ShouldBe(b);
    }

    [Fact]
    public void TheBufferDropsRatherThanBlocks_WhenFull()
    {
        // Under an error storm the request must not wait on the logger.
        // Losing rows is the intended outcome: stdout keeps the complete
        // record and the table is the convenient one, not the authoritative
        // one.
        ChannelErrorLogWriter writer = new(
            Options.Create(new ErrorLogWriterOptions
            {
                ConnectionString = "unused",
                ServiceName = "test",
                Capacity = 4,
            }),
            NullLogger<ChannelErrorLogWriter>.Instance);

        for (int i = 0; i < 50; i++)
        {
            // Never blocks, never throws, even with nothing draining it.
            writer.Enqueue(NewMinimalEntry()).ShouldBeTrue();
        }

        int buffered = 0;
        while (writer.Reader.TryRead(out _))
        {
            buffered++;
        }

        buffered.ShouldBe(4);
    }

    private async Task RunWriterAsync(ErrorLogEntry entry)
    {
        IOptions<ErrorLogWriterOptions> options = Options.Create(new ErrorLogWriterOptions
        {
            ConnectionString = postgres.ConnectionString,
            ServiceName = "test-service",
            Environment = "Test",
            BatchSize = 10,
            FlushInterval = TimeSpan.FromMilliseconds(100),
        });

        ChannelErrorLogWriter buffer = new(options, NullLogger<ChannelErrorLogWriter>.Instance);
        using ErrorLogBackgroundWriter background =
            new(buffer, options, NullLogger<ErrorLogBackgroundWriter>.Instance);

        buffer.Enqueue(entry).ShouldBeTrue();

        await background.StartAsync(CancellationToken.None);

        // StopAsync triggers the shutdown drain, so this is deterministic
        // rather than a sleep-and-hope.
        await Task.Delay(300);
        await background.StopAsync(CancellationToken.None);
    }

    private static ErrorLogEntry NewMinimalEntry() => new()
    {
        ErrorId = Uuid7.New(),
        OccurredAt = DateTimeOffset.UtcNow,
        ServiceName = "test",
        Environment = "Test",
        MachineName = "test",
        SourceKind = "http",
        ExceptionType = "System.Exception",
        Message = "boom",
        Fingerprint = "0000000000000000",
    };

    private static InvalidOperationException CatchDeliberateFailure()
    {
        try
        {
            ThrowDeliberately();
            throw new InvalidOperationException("unreachable");
        }
        catch (InvalidOperationException ex)
        {
            return ex;
        }
    }

    private static void ThrowDeliberately()
        => throw new InvalidOperationException("deliberate", new ArgumentException("inner cause"));
}
