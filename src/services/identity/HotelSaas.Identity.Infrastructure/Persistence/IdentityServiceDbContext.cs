using HotelSaas.BuildingBlocks.Application;
using HotelSaas.BuildingBlocks.Persistence;
using HotelSaas.Identity.Domain.Access;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HotelSaas.Identity.Infrastructure.Persistence;

// The user record.
//
// Lives in Infrastructure, not Domain, because IdentityUser is a framework
// type and Domain has zero package references. That boundary is the same
// reasoning as EF mappings not being attributes on entities: the credential
// store is machinery, and the rules that matter (memberships, invitations,
// refresh tokens) are Domain aggregates that reference this by Guid.
//
// Deriving from IdentityUser<Guid> is what gets us a reviewed password
// hasher, lockout, security stamps and confirmation flags. Hand-rolling
// password hashing is the single worst idea available in this project.
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string FullName { get; set; } = string.Empty;

    // platform | tenant. A platform admin has no tenant membership at all,
    // so this cannot be inferred from the memberships table.
    public string ScopeType { get; set; } = "tenant";

    // Bumped whenever this user's roles, permissions or property access
    // change. It goes into the access token as a claim, and a service can
    // compare it to decide a token is carrying stale authorization
    // (ADR-0005). This is the lever that makes revocation faster than
    // token expiry.
    public int PermissionsVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    // Null until an invitation is accepted. A user with no password hash
    // exists but cannot sign in - which is exactly the state between
    // "invited" and "set up".
    public DateTimeOffset? PasswordSetAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}

// Derives from OUR base, not from Microsoft's IdentityDbContext.
//
// C# has single inheritance and we cannot have both. Taking
// IdentityDbContext would mean re-implementing the audit stamping, the
// tenant filter and the outbox drain that HotelSaasDbContext already gives
// all 14 services - duplicating exactly what BuildingBlocks exists to
// prevent.
//
// So the base stays ours and Identity's entities are configured by hand
// below. AddEntityFrameworkStores only needs Context.Set<TUser>() to
// resolve, which it does as long as the model has the entity configured -
// it does not require Microsoft's context type.
public sealed class IdentityServiceDbContext(
    DbContextOptions<IdentityServiceDbContext> options,
    ITenantContext tenantContext,
    IClock clock,
    ICorrelationContext correlationContext)
    : HotelSaasDbContext(options, tenantContext, clock, correlationContext)
{
    public DbSet<ApplicationUser> Users => Set<ApplicationUser>();

    public DbSet<UserTenantMembership> UserTenantMemberships => Set<UserTenantMembership>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // Base first: outbox_messages, idempotency_keys, processed_messages,
        // error_logs, plus the tenant filter for anything ITenantScoped.
        base.OnModelCreating(modelBuilder);

        ConfigureIdentityUser(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IdentityServiceDbContext).Assembly);
    }

    // The parts of Microsoft's IdentityDbContext.OnModelCreating that we
    // actually use. Roles, claims, external logins and user tokens are
    // deliberately absent - stage 5b adds our OWN roles and permissions
    // model, which has to carry property-level access and so cannot be
    // Identity's flat role table anyway.
    private static void ConfigureIdentityUser(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApplicationUser>(builder =>
        {
            builder.ToTable("users");
            builder.HasKey(u => u.Id);

            builder.Property(u => u.Id).ValueGeneratedNever();

            builder.Property(u => u.UserName).HasMaxLength(320);
            builder.Property(u => u.Email).HasMaxLength(320);
            builder.Property(u => u.FullName).HasMaxLength(200).IsRequired();
            builder.Property(u => u.ScopeType).HasMaxLength(20).IsRequired();
            builder.Property(u => u.PhoneNumber).HasMaxLength(30);

            // Identity looks users up by the NORMALIZED column, never by
            // Email, and UserManager fills it in. A unique index on the
            // wrong one of the two would let Owner@x.com and owner@x.com
            // both register.
            builder.Property(u => u.NormalizedUserName).HasMaxLength(320);
            builder.Property(u => u.NormalizedEmail).HasMaxLength(320);

            builder.HasIndex(u => u.NormalizedEmail)
                .IsUnique()
                .HasDatabaseName("ux_users_normalized_email");

            builder.HasIndex(u => u.NormalizedUserName)
                .IsUnique()
                .HasDatabaseName("ux_users_normalized_user_name");

            // Identity's own concurrency token. Left as it is rather than
            // swapped for our `version` integer, because UserManager writes
            // it itself and expects a string.
            builder.Property(u => u.ConcurrencyStamp).IsConcurrencyToken();
        });
    }
}
