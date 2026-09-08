using HotelSaas.BuildingBlocks.Domain;
using HotelSaas.BuildingBlocks.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace HotelSaas.BuildingBlocks.Tests;

// ADR-0010 said to verify this once, at stage 3, rather than assume it.
//
// PostgreSQL compares uuid as 16 big-endian bytes. .NET Guid stores its
// first three fields little-endian, so if Npgsql wrote the raw in-memory
// layout, the timestamp prefix would be byte-reversed and postgres would
// sort v7 ids into effectively random order - silently destroying the only
// reason we chose v7 over v4. Index locality would be as bad as v4 while
// every benchmark and code review said otherwise.
//
// This is exactly the trap SQL Server users hit: uniqueidentifier sorts
// differently from the string form of the same value.
[Collection(PostgresCollection.Name)]
public sealed class Uuid7DatabaseOrderingTests(PostgresFixture postgres)
{
    [Fact]
    public async Task IdsFromDistinctMilliseconds_SortInCreationOrder_InTheDatabase()
    {
        // This is the byte-order proof. If Npgsql wrote .NET's in-memory
        // layout, the timestamp prefix would be reversed and this ordering
        // would be random.
        Guid tenantId = Uuid7.New();
        MutableTenantContext tenant = new(tenantId);

        List<Guid> insertionOrder = [];

        await using (TestDbContext write = postgres.CreateContext(tenant))
        {
            for (int i = 0; i < 60; i++)
            {
                Guid id = Uuid7.New();
                insertionOrder.Add(id);
                write.Widgets.Add(new Widget(id, $"ms-{i:D3}"));

                // At least one millisecond between ids. v7 orders by
                // millisecond, so this is the granularity at which ordering
                // is actually promised.
                await Task.Delay(2);
            }

            await write.SaveChangesAsync();
        }

        await using TestDbContext read = postgres.CreateContext(tenant);

        List<Guid> databaseOrder = await read.Widgets
            .Where(w => w.Name.StartsWith("ms-"))
            .OrderBy(w => w.Id)
            .Select(w => w.Id)
            .ToListAsync();

        databaseOrder.ShouldBe(insertionOrder);
    }

    [Fact]
    public void IdsFromTheSameMillisecond_ShareATimestampPrefix_ButAreNotOrdered()
    {
        // A limitation worth knowing, and the reason the first version of
        // this test failed.
        //
        // .NET fills v7 with a 48-bit millisecond timestamp plus RANDOM
        // bits - there is no monotonic counter (RFC 9562 permits one but
        // does not require it). So two ids minted in the same millisecond
        // have random relative order.
        //
        // That does NOT harm what we chose v7 for. Index locality depends
        // on the shared timestamp prefix, which puts them adjacent at the
        // right-hand edge of the B-tree. It does mean `order by id` is only
        // a millisecond-accurate proxy for creation order - fine for
        // paging, wrong for anything that needs an exact sequence.
        List<Guid> burst = [];
        for (int i = 0; i < 200; i++)
        {
            burst.Add(Uuid7.New());
        }

        // Group by the 48-bit timestamp prefix; a burst this tight lands in
        // very few distinct milliseconds.
        Dictionary<string, int> byPrefix = [];
        foreach (Guid id in burst)
        {
            string prefix = Convert.ToHexString(id.ToByteArray(bigEndian: true)[..6]);
            byPrefix[prefix] = byPrefix.GetValueOrDefault(prefix) + 1;
        }

        byPrefix.Count.ShouldBeLessThan(burst.Count);

        // At least one millisecond holds several ids - those are the ones
        // whose mutual order is not defined.
        byPrefix.Values.Max().ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task OrderingById_MatchesOrderingByCreatedAt()
    {
        // The practical payoff: `order by id` is `order by created_at` for
        // free, with no second index and no extra column in the sort.
        Guid tenantId = Uuid7.New();
        MutableTenantContext tenant = new(tenantId);

        await using (TestDbContext write = postgres.CreateContext(tenant))
        {
            for (int i = 0; i < 25; i++)
            {
                write.Widgets.Add(new Widget(Uuid7.New(), $"ordered-{i:D2}"));
                await write.SaveChangesAsync();
                await Task.Delay(2);
            }
        }

        await using TestDbContext read = postgres.CreateContext(tenant);

        List<string> byId = await read.Widgets
            .Where(w => w.Name.StartsWith("ordered-"))
            .OrderBy(w => w.Id).Select(w => w.Name).ToListAsync();

        List<string> byCreatedAt = await read.Widgets
            .Where(w => w.Name.StartsWith("ordered-"))
            .OrderBy(w => w.CreatedAt).ThenBy(w => w.Id).Select(w => w.Name).ToListAsync();

        byId.ShouldBe(byCreatedAt);
    }
}
