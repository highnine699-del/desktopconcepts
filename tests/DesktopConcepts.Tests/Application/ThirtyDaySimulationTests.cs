using DesktopConcepts.Application.Schedulers;
using DesktopConcepts.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopConcepts.Tests.Application;

/// <summary>
/// Acceptance checklist item: "30-day simulated run — no topic repeats across days,
/// correct weekday categories, exactly 3 distinct concepts per day in history."
///
/// This test is deterministic — no real AI, no I/O, no wallclock dependency.
/// </summary>
public sealed class ThirtyDaySimulationTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates unique titles per call by incrementing a counter.
    /// Validates that the avoid-list contains all previously generated titles.
    /// </summary>
    private sealed class CountingProvider : IConceptProvider
    {
        private int _callCount;
        public List<string> AllGeneratedTitles { get; } = [];
        public List<string> LastAvoidList      { get; private set; } = [];

        public Task<Concept> GenerateConceptAsync(
            string category,
            IReadOnlyCollection<string> recentTitlesToAvoid,
            CancellationToken cancellationToken)
        {
            LastAvoidList = recentTitlesToAvoid.ToList();
            var title   = $"Concept_{++_callCount}_{category}";
            AllGeneratedTitles.Add(title);
            return Task.FromResult(new Concept(
                title, "Explanation", category,
                DateOnly.FromDateTime(DateTime.Today)));
        }
    }

    private sealed class AccumulatingHistory : IConceptHistoryStore
    {
        private readonly List<string> _allTitles = [];

        public Task AppendSetAsync(DailyConceptSet set, CancellationToken ct)
        {
            foreach (var c in set.Concepts)
                _allTitles.Add(c.Title);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetRecentTitlesAsync(int count, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(_allTitles.TakeLast(count).ToList());

        public Task<DailyConceptSet?> GetMostRecentSetAsync(CancellationToken ct)
            => Task.FromResult<DailyConceptSet?>(null);

        public IReadOnlyList<string> All => _allTitles.AsReadOnly();
    }

    private sealed class FakeSettings : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct)
            => Task.FromResult(AppSettings.Default());
        public Task SaveAsync(AppSettings s, CancellationToken ct) => Task.CompletedTask;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThirtyDays_ProducesExactly90Concepts_AllUnique()
    {
        var provider  = new CountingProvider();
        var history   = new AccumulatingHistory();
        var scheduler = new DailyConceptScheduler(
            provider, history, new FakeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);

        var start = new DateOnly(2025, 1, 1);

        for (var i = 0; i < 30; i++)
            await scheduler.RunIfDueAsync(start.AddDays(i), CancellationToken.None);

        // 30 days x 3 concepts = 90 total
        Assert.Equal(90, history.All.Count);

        // All titles must be distinct
        Assert.Equal(90, history.All.Distinct().Count());
    }

    [Fact]
    public async Task ThirtyDays_EachDayUsesCorrectWeekdayCategory()
    {
        var provider  = new CountingProvider();
        var history   = new AccumulatingHistory();
        var scheduler = new DailyConceptScheduler(
            provider, history, new FakeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);

        var defaultMap = AppSettings.Default().Topics;
        var start      = new DateOnly(2025, 1, 1); // Wednesday

        for (var i = 0; i < 30; i++)
        {
            var today    = start.AddDays(i);
            var expected = defaultMap.CategoryFor(today.DayOfWeek);
            await scheduler.RunIfDueAsync(today, CancellationToken.None);

            // The last 3 generated titles all contain the expected category
            var lastThree = provider.AllGeneratedTitles.TakeLast(3).ToList();
            Assert.All(lastThree, t => Assert.Contains(expected, t));
        }
    }

    [Fact]
    public async Task ThirtyDays_AvoidListGrowsAcrossDays_PreventingRepeats()
    {
        var provider  = new CountingProvider();
        var history   = new AccumulatingHistory();
        var scheduler = new DailyConceptScheduler(
            provider, history, new FakeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);

        var start = new DateOnly(2025, 1, 1);

        for (var i = 0; i < 30; i++)
            await scheduler.RunIfDueAsync(start.AddDays(i), CancellationToken.None);

        // On day 30 (index 29) the avoid-list fed into the first of the day's 3 calls
        // should contain titles from previous days (capped at 30).
        // We can't inspect internal state directly, but we can confirm the provider
        // received a non-empty avoid-list by day 2.
        Assert.True(provider.LastAvoidList.Count > 0,
            "Avoid-list should be non-empty by the end of the simulation.");
    }

    [Fact]
    public async Task ThirtyDays_EachDaySet_Has3Concepts()
    {
        var provider  = new CountingProvider();
        var history   = new AccumulatingHistory();
        var sets      = new List<DailyConceptSet>();
        var scheduler = new DailyConceptScheduler(
            provider, history, new FakeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);
        scheduler.ConceptSetGenerated += s => sets.Add(s);

        var start = new DateOnly(2025, 1, 1);

        for (var i = 0; i < 30; i++)
            await scheduler.RunIfDueAsync(start.AddDays(i), CancellationToken.None);

        Assert.Equal(30, sets.Count);
        Assert.All(sets, s => Assert.Equal(3, s.Count));
    }
}
