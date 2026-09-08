using HotelSaas.BuildingBlocks.Domain;

namespace HotelSaas.BuildingBlocks.Tests;

public sealed class Uuid7Tests
{
    [Fact]
    public void New_ProducesUniqueValues()
    {
        HashSet<Guid> ids = [];

        for (int i = 0; i < 10_000; i++)
        {
            ids.Add(Uuid7.New()).ShouldBeTrue();
        }

        ids.Count.ShouldBe(10_000);
    }

    [Fact]
    public void New_IsTimeOrdered_WhenComparedAsStrings()
    {
        // The whole reason for choosing v7 over v4 (ADR-0010): values
        // generated later must sort after values generated earlier, so
        // inserts land at the right-hand edge of the index instead of
        // scattering across it.
        Guid earlier = Uuid7.New(DateTimeOffset.UtcNow.AddHours(-1));
        Guid later = Uuid7.New(DateTimeOffset.UtcNow);

        string.CompareOrdinal(earlier.ToString(), later.ToString()).ShouldBeLessThan(0);
    }

    [Fact]
    public void New_EmbedsTheSuppliedTimestamp()
    {
        DateTimeOffset timestamp = new(2026, 9, 8, 12, 30, 0, TimeSpan.Zero);

        Guid id = Uuid7.New(timestamp);

        // First 48 bits are milliseconds since the unix epoch, big-endian.
        byte[] bytes = id.ToByteArray(bigEndian: true);
        long milliseconds =
            ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24) |
            ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];

        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            .ShouldBe(timestamp, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void New_SetsVersion7()
    {
        byte[] bytes = Uuid7.New().ToByteArray(bigEndian: true);

        // High nibble of octet 6 is the version.
        (bytes[6] >> 4).ShouldBe(7);
    }
}
