using HotelSaas.BuildingBlocks.Application;

namespace HotelSaas.BuildingBlocks.Tests.TestSupport;

// A tenant context whose tenant can be changed between operations, so a
// test can act as two different tenants against the same database.
internal sealed class MutableTenantContext(Guid? tenantId = null) : ITenantContext
{
    public Guid? TenantId { get; set; } = tenantId;
}

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}
