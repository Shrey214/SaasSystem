using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace HotelSaas.BuildingBlocks.Persistence.Errors;

// Drains the buffer into error_logs in batches.
//
// Uses a RAW Npgsql connection, not the request DbContext. After an
// exception that DbContext may hold a rolled-back transaction or tracked
// entities in a broken state, and writing through it throws again - a 500
// inside a 500 with the original error lost (ADR-0008).
public sealed class ErrorLogBackgroundWriter(
    ChannelErrorLogWriter buffer,
    IOptions<ErrorLogWriterOptions> options,
    ILogger<ErrorLogBackgroundWriter> logger) : BackgroundService
{
    private const string InsertSql = """
        insert into error_logs (
            error_id, occurred_at, service_name, environment, machine_name, version,
            correlation_id, causation_id, tenant_id, user_id,
            source_kind, http_method, path, query_string, status_code, duration_ms, message_type,
            exception_type, message, stack_trace, inner_exceptions,
            fault_assembly, fault_type, fault_method, fault_file, fault_line,
            request_headers, fingerprint)
        values (
            @error_id, @occurred_at, @service_name, @environment, @machine_name, @version,
            @correlation_id, @causation_id, @tenant_id, @user_id,
            @source_kind, @http_method, @path, @query_string, @status_code, @duration_ms, @message_type,
            @exception_type, @message, @stack_trace, @inner_exceptions,
            @fault_assembly, @fault_type, @fault_method, @fault_file, @fault_line,
            @request_headers, @fingerprint);
        """;

    private readonly ErrorLogWriterOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        List<ErrorLogEntry> batch = new(_options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Blocks until something arrives, so an idle service costs
                // nothing.
                if (!await buffer.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }

                batch.Clear();
                while (batch.Count < _options.BatchSize && buffer.Reader.TryRead(out ErrorLogEntry? entry))
                {
                    batch.Add(entry);
                }

                if (batch.Count > 0)
                {
                    await WriteBatchAsync(batch, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // this loop must survive absolutely anything
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // The error logger failing is itself only a log line. If it
                // threw, error handling would break the responses it exists
                // to describe.
                ErrorLogMessages.PersistFailed(logger, ex, batch.Count);
                await Task.Delay(_options.FlushInterval, stoppingToken).ConfigureAwait(false);
            }
        }

        // Best effort on shutdown: drain what is still buffered.
        await DrainRemainingAsync().ConfigureAwait(false);
    }

    private async Task WriteBatchAsync(List<ErrorLogEntry> batch, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (ErrorLogEntry entry in batch)
        {
            await using NpgsqlCommand command = new(InsertSql, connection, transaction)
            {
                CommandTimeout = (int)_options.CommandTimeout.TotalSeconds,
            };

            AddParameters(command, entry);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainRemainingAsync()
    {
        List<ErrorLogEntry> remaining = [];
        while (buffer.Reader.TryRead(out ErrorLogEntry? entry))
        {
            remaining.Add(entry);
        }

        if (remaining.Count == 0)
        {
            return;
        }

        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
            await WriteBatchAsync(remaining, cts.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // shutdown path: nothing useful left to do
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorLogMessages.DroppedOnShutdown(logger, ex, remaining.Count);
        }
    }

    private static void AddParameters(NpgsqlCommand command, ErrorLogEntry e)
    {
        void Add(string name, object? value, NpgsqlDbType type)
            => command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });

        Add("error_id", e.ErrorId, NpgsqlDbType.Uuid);
        Add("occurred_at", e.OccurredAt, NpgsqlDbType.TimestampTz);
        Add("service_name", e.ServiceName, NpgsqlDbType.Text);
        Add("environment", e.Environment, NpgsqlDbType.Text);
        Add("machine_name", e.MachineName, NpgsqlDbType.Text);
        Add("version", e.Version, NpgsqlDbType.Text);
        Add("correlation_id", e.CorrelationId, NpgsqlDbType.Uuid);
        Add("causation_id", e.CausationId, NpgsqlDbType.Uuid);
        Add("tenant_id", e.TenantId, NpgsqlDbType.Uuid);
        Add("user_id", e.UserId, NpgsqlDbType.Uuid);
        Add("source_kind", e.SourceKind, NpgsqlDbType.Text);
        Add("http_method", e.HttpMethod, NpgsqlDbType.Text);
        Add("path", e.Path, NpgsqlDbType.Text);
        Add("query_string", e.QueryString, NpgsqlDbType.Text);
        Add("status_code", e.StatusCode, NpgsqlDbType.Integer);
        Add("duration_ms", e.DurationMs, NpgsqlDbType.Integer);
        Add("message_type", e.MessageType, NpgsqlDbType.Text);
        Add("exception_type", e.ExceptionType, NpgsqlDbType.Text);
        Add("message", e.Message, NpgsqlDbType.Text);
        Add("stack_trace", e.StackTrace, NpgsqlDbType.Text);
        Add("inner_exceptions", e.InnerExceptions, NpgsqlDbType.Jsonb);
        Add("fault_assembly", e.FaultAssembly, NpgsqlDbType.Text);
        Add("fault_type", e.FaultType, NpgsqlDbType.Text);
        Add("fault_method", e.FaultMethod, NpgsqlDbType.Text);
        Add("fault_file", e.FaultFile, NpgsqlDbType.Text);
        Add("fault_line", e.FaultLine, NpgsqlDbType.Integer);
        Add("request_headers", e.RequestHeaders, NpgsqlDbType.Jsonb);
        Add("fingerprint", e.Fingerprint, NpgsqlDbType.Text);
    }
}
