using DesktopConcepts.Domain;
using DesktopConcepts.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopConcepts.Tests.Infrastructure;

/// <summary>
/// JsonConceptBufferStore: queue semantics, persistence, corrupt-file resilience,
/// PeekDates, and thread-safety basics.
/// </summary>
public sealed class JsonConceptBufferStoreTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(), $"dc_buf_{Guid.NewGuid():N}.json");

    private JsonConceptBufferStore MakeStore() =>
        new(NullLogger<JsonConceptBufferStore>.Instance, _tempPath);

    private static DailyConceptSet MakeSet(DateOnly date)
    {
        var concepts = Enumerable.Range(0, 3)
            .Select(i => new Concept($"Title_{date}_{i}", "Explanation", "Testing", date))
            .ToList();
        return new DailyConceptSet(date, concepts.AsReadOnly());
    }

    private static IReadOnlyList<DailyConceptSet> MakeSets(int count)
    {
        var base_ = DateOnly.FromDateTime(DateTime.Today);
        return Enumerable.Range(0, count).Select(i => MakeSet(base_.AddDays(i))).ToList();
    }

    [Fact]
    public async Task Count_Zero_WhenFileNotPresent()
    {
        var store = MakeStore();
        Assert.Equal(0, await store.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TryTakeNext_ReturnsNull_WhenEmpty()
    {
        var store = MakeStore();
        Assert.Null(await store.TryTakeNextAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AddRange_ThenCount_ReflectsAdded()
    {
        var store = MakeStore();
        await store.AddRangeAsync(MakeSets(5), CancellationToken.None);
        Assert.Equal(5, await store.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TryTakeNext_PopsFirstInOrder_QueueSemantics()
    {
        var store = MakeStore();
        var sets  = MakeSets(3);
        await store.AddRangeAsync(sets, CancellationToken.None);

        var first  = await store.TryTakeNextAsync(CancellationToken.None);
        var second = await store.TryTakeNextAsync(CancellationToken.None);

        Assert.Equal(sets[0].Date, first!.Date);
        Assert.Equal(sets[1].Date, second!.Date);
        Assert.Equal(1, await store.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TryTakeNext_ConceptsRoundTrip_Correctly()
    {
        var store = MakeStore();
        var set   = MakeSet(DateOnly.FromDateTime(DateTime.Today));
        await store.AddRangeAsync([set], CancellationToken.None);

        var loaded = await store.TryTakeNextAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(3, loaded!.Count);
        Assert.Equal(set.Concepts[0].Title, loaded.Concepts[0].Title);
        Assert.Equal(set.Concepts[2].Explanation, loaded.Concepts[2].Explanation);
    }

    [Fact]
    public async Task PeekDates_ReturnsAllDates_WithoutConsuming()
    {
        var store = MakeStore();
        var sets  = MakeSets(4);
        await store.AddRangeAsync(sets, CancellationToken.None);

        var dates = await store.PeekDatesAsync(CancellationToken.None);

        Assert.Equal(4, dates.Count);
        Assert.Equal(sets[0].Date, dates[0]);
        Assert.Equal(sets[3].Date, dates[3]);
        // PeekDates must NOT consume anything
        Assert.Equal(4, await store.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CorruptFile_ReturnsEmptyBuffer_DoesNotCrash()
    {
        await File.WriteAllTextAsync(_tempPath, "{ this is not valid json {{{{");
        var store = MakeStore();

        // Must not throw
        Assert.Equal(0, await store.CountAsync(CancellationToken.None));
        Assert.Null(await store.TryTakeNextAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AddRange_MultipleCalls_AppendsTail()
    {
        var store = MakeStore();
        var base_ = DateOnly.FromDateTime(DateTime.Today);

        await store.AddRangeAsync([MakeSet(base_), MakeSet(base_.AddDays(1))], CancellationToken.None);
        await store.AddRangeAsync([MakeSet(base_.AddDays(2))], CancellationToken.None);

        Assert.Equal(3, await store.CountAsync(CancellationToken.None));
        // First entry is still the oldest
        var first = await store.TryTakeNextAsync(CancellationToken.None);
        Assert.Equal(base_, first!.Date);
    }

    [Fact]
    public async Task DrainAllSets_CountBecomesZero()
    {
        var store = MakeStore();
        await store.AddRangeAsync(MakeSets(3), CancellationToken.None);

        for (var i = 0; i < 3; i++)
            await store.TryTakeNextAsync(CancellationToken.None);

        Assert.Equal(0, await store.CountAsync(CancellationToken.None));
        Assert.Null(await store.TryTakeNextAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }
}
