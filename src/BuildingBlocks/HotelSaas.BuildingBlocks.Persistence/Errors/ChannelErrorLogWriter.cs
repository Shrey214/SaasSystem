using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HotelSaas.BuildingBlocks.Persistence.Errors;

// Buffers error entries for the background writer (ADR-0008).
//
// DropOldest rather than Wait: under an error storm the request must not be
// blocked by the logger. Losing some rows is acceptable - stdout keeps the
// complete record and the table is the convenient one, not the
// authoritative one.
public sealed class ChannelErrorLogWriter : IErrorLogWriter
{
    private readonly Channel<ErrorLogEntry> _channel;
    private readonly ILogger<ChannelErrorLogWriter> _logger;
    private int _droppedCount;

    public ChannelErrorLogWriter(
        IOptions<ErrorLogWriterOptions> options,
        ILogger<ChannelErrorLogWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;

        _channel = Channel.CreateBounded<ErrorLogEntry>(
            new BoundedChannelOptions(options.Value.Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    public ChannelReader<ErrorLogEntry> Reader => _channel.Reader;

    public int DroppedCount => _droppedCount;

    public bool Enqueue(ErrorLogEntry entry)
    {
        // TryWrite never blocks and never throws for a bounded DropOldest
        // channel, which is the whole reason this type exists.
        if (_channel.Writer.TryWrite(entry))
        {
            return true;
        }

        int dropped = Interlocked.Increment(ref _droppedCount);
        ErrorLogMessages.BufferFull(_logger, entry?.ErrorId ?? Guid.Empty, dropped);

        return false;
    }
}
