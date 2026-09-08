using HotelSaas.BuildingBlocks.Persistence.Errors;
using HotelSaas.BuildingBlocks.Persistence.Idempotency;
using HotelSaas.BuildingBlocks.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HotelSaas.BuildingBlocks.Persistence.Configurations;

// Column names come from the snake_case naming convention, so these
// configurations only declare keys, lengths, indexes and column types.

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityAlwaysColumn();

        builder.Property(x => x.AggregateType).HasMaxLength(200).IsRequired();
        builder.Property(x => x.EventType).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();

        // The message id is the consumer idempotency key; a duplicate row
        // would let the same event be published twice with two ids.
        builder.HasIndex(x => x.MessageId).IsUnique().HasDatabaseName("ux_outbox_messages_message_id");

        // Partial index: the publisher only ever reads unpublished rows, and
        // this table is mostly published rows. A full index would grow
        // forever and be almost entirely dead weight.
        builder.HasIndex(x => x.OccurredAt)
            .HasFilter("published_at is null")
            .HasDatabaseName("ix_outbox_messages_unpublished");
    }
}

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_keys");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityAlwaysColumn();

        builder.Property(x => x.Key).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Endpoint).HasMaxLength(200).IsRequired();
        builder.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ResponseBody).HasColumnType("jsonb");

        // This uniqueness IS the lock. The insert is what makes a concurrent
        // retry of the same request fail rather than execute twice.
        builder.HasIndex(x => new { x.TenantId, x.Endpoint, x.Key })
            .IsUnique()
            .HasDatabaseName("ux_idempotency_keys_tenant_endpoint_key");
    }
}

internal sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("processed_messages");

        // Composite key, not a surrogate: the pair IS the identity, and the
        // primary key doubles as the duplicate check.
        builder.HasKey(x => new { x.MessageId, x.Consumer });
        builder.Property(x => x.Consumer).HasMaxLength(200).IsRequired();
    }
}

internal sealed class ErrorLogEntryConfiguration : IEntityTypeConfiguration<ErrorLogEntry>
{
    public void Configure(EntityTypeBuilder<ErrorLogEntry> builder)
    {
        builder.ToTable("error_logs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityAlwaysColumn();

        builder.Property(x => x.ServiceName).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Environment).HasMaxLength(50).IsRequired();
        builder.Property(x => x.MachineName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Version).HasMaxLength(50);
        builder.Property(x => x.SourceKind).HasMaxLength(20).IsRequired();
        builder.Property(x => x.HttpMethod).HasMaxLength(10);
        builder.Property(x => x.Path).HasMaxLength(2000);
        builder.Property(x => x.QueryString).HasMaxLength(4000);
        builder.Property(x => x.MessageType).HasMaxLength(200);
        builder.Property(x => x.ExceptionType).HasMaxLength(500).IsRequired();
        builder.Property(x => x.FaultAssembly).HasMaxLength(200);
        builder.Property(x => x.FaultType).HasMaxLength(500);
        builder.Property(x => x.FaultMethod).HasMaxLength(200);
        builder.Property(x => x.FaultFile).HasMaxLength(1000);
        builder.Property(x => x.Fingerprint).HasMaxLength(32).IsRequired();
        builder.Property(x => x.InnerExceptions).HasColumnType("jsonb");
        builder.Property(x => x.RequestHeaders).HasColumnType("jsonb");

        builder.HasIndex(x => x.OccurredAt).IsDescending().HasDatabaseName("ix_error_logs_occurred_at");
        builder.HasIndex(x => x.CorrelationId).HasDatabaseName("ix_error_logs_correlation_id");
        builder.HasIndex(x => new { x.TenantId, x.OccurredAt }).HasDatabaseName("ix_error_logs_tenant_occurred");

        // Grouping index: "this bug fired 4,000 times" is one query.
        builder.HasIndex(x => new { x.Fingerprint, x.OccurredAt }).HasDatabaseName("ix_error_logs_fingerprint");
    }
}
