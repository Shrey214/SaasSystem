using HotelSaas.Tenant.Domain.Businesses;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HotelSaas.Tenant.Infrastructure.Persistence.Configurations;

// EF mapping lives here, never as attributes on the domain entity. That is
// what keeps Domain free of any reference to EF Core.
//
// Column names come from the snake_case naming convention, so these only
// declare keys, lengths, relationships, indexes and constraints.

internal sealed class BusinessConfiguration : IEntityTypeConfiguration<Business>
{
    public void Configure(EntityTypeBuilder<Business> builder)
    {
        builder.ToTable("businesses");
        builder.HasKey(x => x.Id);

        // No tenant_id column. This row defines a tenant rather than
        // belonging to one, and its id is what the other 13 services store.

        builder.Property(x => x.LegalName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.OwnerEmail).HasMaxLength(320).IsRequired();
        builder.Property(x => x.OwnerName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Country).HasMaxLength(2).IsFixedLength().IsRequired();
        builder.Property(x => x.SuspensionReason).HasMaxLength(500);

        // Stored as text, not an int (docs/00-conventions.md 3): readable in
        // psql, and adding a state later needs no data migration.
        builder.Property(x => x.Status)
            .HasConversion<string>()
            .HasMaxLength(30)
            .IsRequired();

        builder.Property(x => x.Version).IsConcurrencyToken();

        // THE uniqueness guard.
        //
        // The column is stored lowercased (see Business.OwnerEmail), so a
        // plain unique index enforces case-insensitive uniqueness without a
        // functional index. This is the only ATOMIC check - the SELECT in
        // RegisterBusinessHandler is a courtesy that two concurrent
        // requests both pass.
        builder.HasIndex(x => x.OwnerEmail)
            .IsUnique()
            .HasDatabaseName("ux_businesses_owner_email");

        builder.HasIndex(x => x.Status).HasDatabaseName("ix_businesses_status");
        builder.HasIndex(x => x.RegisteredAt).IsDescending().HasDatabaseName("ix_businesses_registered_at");

        builder.HasOne(x => x.Profile)
            .WithOne()
            .HasForeignKey<BusinessProfile>(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.StatusHistory)
            .WithOne()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.EmailVerifications)
            .WithOne()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        // Backing fields, so the collections stay encapsulated: callers get
        // IReadOnlyList and cannot add a status-history row without going
        // through a transition.
        builder.Navigation(x => x.StatusHistory).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.EmailVerifications).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(x => x.Profile).UsePropertyAccessMode(PropertyAccessMode.Property);
    }
}

internal sealed class BusinessProfileConfiguration : IEntityTypeConfiguration<BusinessProfile>
{
    public void Configure(EntityTypeBuilder<BusinessProfile> builder)
    {
        builder.ToTable("business_profiles");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.LegalAddress).HasMaxLength(500);
        builder.Property(x => x.City).HasMaxLength(120);
        builder.Property(x => x.State).HasMaxLength(120);
        builder.Property(x => x.PostalCode).HasMaxLength(20);
        builder.Property(x => x.ContactPhone).HasMaxLength(30);
        builder.Property(x => x.TaxIdentifier).HasMaxLength(50);
        builder.Property(x => x.WebsiteUrl).HasMaxLength(500);
        builder.Property(x => x.LogoUrl).HasMaxLength(500);

        builder.HasIndex(x => x.BusinessId).IsUnique().HasDatabaseName("ux_business_profiles_business_id");
    }
}

internal sealed class BusinessStatusChangeConfiguration : IEntityTypeConfiguration<BusinessStatusChange>
{
    public void Configure(EntityTypeBuilder<BusinessStatusChange> builder)
    {
        builder.ToTable("business_status_history");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(500).IsRequired();

        // Newest first, which is how it is always read.
        builder.HasIndex(x => new { x.BusinessId, x.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_business_status_history_business_created");
    }
}

internal sealed class EmailVerificationConfiguration : IEntityTypeConfiguration<EmailVerification>
{
    public void Configure(EntityTypeBuilder<EmailVerification> builder)
    {
        builder.ToTable("email_verifications");
        builder.HasKey(x => x.Id);

        // SHA-256 hex. Never the token itself.
        builder.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();

        // Lookup is always by hash, and it must be unique: two businesses
        // sharing a token hash would make one link verify the wrong account.
        builder.HasIndex(x => x.TokenHash).IsUnique().HasDatabaseName("ux_email_verifications_token_hash");

        builder.HasIndex(x => x.BusinessId).HasDatabaseName("ix_email_verifications_business_id");
    }
}
