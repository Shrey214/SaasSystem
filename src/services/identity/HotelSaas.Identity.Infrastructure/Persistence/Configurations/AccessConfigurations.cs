using HotelSaas.Identity.Domain.Access;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HotelSaas.Identity.Infrastructure.Persistence.Configurations;

// Column names come from the snake_case naming convention, so these only
// declare keys, lengths, relationships, indexes and constraints.

internal sealed class UserTenantMembershipConfiguration : IEntityTypeConfiguration<UserTenantMembership>
{
    public void Configure(EntityTypeBuilder<UserTenantMembership> builder)
    {
        builder.ToTable("user_tenant_memberships");
        builder.HasKey(x => x.Id);

        // tenant_id FIRST, per ADR-0004. Not for correctness - for the
        // query plan. An index on (user_id) alone would scan every tenant's
        // memberships to answer a question about one of them.
        builder.HasIndex(x => new { x.TenantId, x.UserId })
            .IsUnique()
            .HasDatabaseName("ux_user_tenant_memberships_tenant_user");

        builder.HasIndex(x => x.UserId).HasDatabaseName("ix_user_tenant_memberships_user_id");

        // No FK to users on purpose. ITenantScoped puts a query filter on
        // this table, and a required FK plus a filter makes cascade
        // behaviour and Include() interact in ways that are hard to reason
        // about. The lookup is always by explicit user id anyway.
    }
}

internal sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.ToTable("invitations");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Email).HasMaxLength(320).IsRequired();
        builder.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();

        // Lookup is always BY HASH, and it must be unique: two invitations
        // sharing a hash would let one link join the wrong business.
        builder.HasIndex(x => x.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_invitations_token_hash");

        builder.HasIndex(x => new { x.TenantId, x.Email })
            .HasDatabaseName("ix_invitations_tenant_email");

        // Partial index: an admin screen only ever lists OPEN invitations,
        // and accepted ones accumulate forever.
        builder.HasIndex(x => new { x.TenantId, x.ExpiresAt })
            .HasFilter("accepted_at is null and revoked_at is null")
            .HasDatabaseName("ix_invitations_open");
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Device).HasMaxLength(200);
        builder.Property(x => x.IpAddress).HasMaxLength(45);
        builder.Property(x => x.RevokedReason).HasMaxLength(100);

        builder.HasIndex(x => x.TokenHash)
            .IsUnique()
            .HasDatabaseName("ux_refresh_tokens_token_hash");

        // "Sign me out everywhere" and the replay revocation both walk a
        // user's tokens, so this is the hot path.
        builder.HasIndex(x => new { x.UserId, x.ExpiresAt })
            .HasDatabaseName("ix_refresh_tokens_user_expires");

        // NOT ITenantScoped, and that is deliberate: a platform admin has
        // no tenant, and a session must be revocable regardless. The user
        // id is the scope here.
    }
}

internal sealed class SigningKeyConfiguration : IEntityTypeConfiguration<SigningKey>
{
    public void Configure(EntityTypeBuilder<SigningKey> builder)
    {
        builder.ToTable("signing_keys");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.KeyId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.PublicKeyPem).HasColumnType("text").IsRequired();
        builder.Property(x => x.PrivateKeyPem).HasColumnType("text").IsRequired();

        // The kid in a JWT header resolves through this.
        builder.HasIndex(x => x.KeyId)
            .IsUnique()
            .HasDatabaseName("ux_signing_keys_key_id");

        // Partial index over the one or two rows that are actually live,
        // out of however many have accumulated through rotation.
        builder.HasIndex(x => x.ActivatedAt)
            .HasFilter("expired_at is null")
            .HasDatabaseName("ix_signing_keys_active");
    }
}
