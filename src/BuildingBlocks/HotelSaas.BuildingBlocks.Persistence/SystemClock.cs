using HotelSaas.BuildingBlocks.Application;

namespace HotelSaas.BuildingBlocks.Persistence;

// The real clock. Tests substitute their own IClock.
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
