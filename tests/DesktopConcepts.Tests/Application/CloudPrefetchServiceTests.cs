using DesktopConcepts.Application.Schedulers;
using DesktopConcepts.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopConcepts.Tests.Application;

/// <summary>
/// CloudPrefetchService acceptance tests:
///   (a) 7-day prefetch produces exactly 21 unique concepts, no duplicates against history or within batch
///   (b) Buffer refill triggers when remaining count drops below threshold (3)
///   (c) Exhausted buffer + no internet → GenerationFailed raised, no crash
///   (d) Both modes work end-to-end with fake providers
/// </summary>
public sealed class CloudPrefetchServiceTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class CountingProvider : IConceptProvider
    {
        private int _n;
        public List<string> Generated { get; } = [];
        public List<IReadOnlyCollection<string>> ReceivedAvoidLists { get; } = [];

        public Task<Concept> GenerateConceptAsync(
            string category,
            IReadOnlyCollection<string> avoid,
            CancellationToken ct)
        {
            ReceivedAvoidLists.Add(avoid.ToList());
            var title = $"Concept_{++_n}_{category}";
            Generated.Add(title);
            return Task.FromResult(new Concept(
                title, "Explanation", category,
                DateOnly.FromDateTime(DateTime.Today)));
        }
    }

    private sealed class InMemoryBufferStore : IConceptBufferStore
    {
        private readonly List<DailyConceptSet> _queue = [];
        public int AddCallCount { get; private set; }

        public Task AddRangeAsync(IReadOnlyList<DailyConceptSet> sets, CancellationToken ct)
        {
            _queue.AddRange(sets);
            AddCallCount++;
            return Task.CompletedTask;
        }

        public Task<DailyConceptSet?> TryTakeNextAsync(CancellationToken ct)
        {
            if (_queue.Count == 0) return Task.FromResult<DailyConceptSet?>(null);
            var set = _queue[0];
            _queue.RemoveAt(0);
            return Task.FromResult<DailyConceptSet?>(set);
        }

        public Task<int> CountAsync(CancellationToken ct)
            => Task.FromResult(_queue.Count);

        public Task<IReadOnlyList<DateOnly>> PeekDatesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DateOnly>>(_queue.Select(s => s.Date).ToList());

        public void Seed(int days)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            for (var i = 0; i < days; i++)
            {
                var date     = today.AddDays(i);
                var concepts = Enumerable.Range(0, 3)
                    .Select(j => new Concept($"Pre_{i}_{j}", "E", "Cat", date))
                    .ToList();
                _queue.Add(new DailyConceptSet(date, concepts.AsReadOnly()));
            }
        }
    }

    private sealed class EmptyHistory : IConceptHistoryStore
    {
        public Task AppendSetAsync(DailyConceptSet s, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetRecentTitlesAsync(int c, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class SeededHistory : IConceptHistoryStore
    {
        private readonly string[] _titles;
        public SeededHistory(params string[] titles) => _titles = titles;
        public Task AppendSetAsync(DailyConceptSet s, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetRecentTitlesAsync(int c, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(_titles.Take(c).ToList());
    }

    private sealed class CloudModeSettings : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct)
            => Task.FromResult(AppSettings.Default() with { Mode = "cloud", IsFirstRun = false });
        public Task SaveAsync(AppSettings s, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class LocalModeSettings : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct)
            => Task.FromResult(AppSettings.Default() with { Mode = "local", IsFirstRun = false });
        public Task SaveAsync(AppSettings s, CancellationToken ct) => Task.CompletedTask;
    }

    private static CloudPrefetchService MakeSvc(
        IConceptProvider? provider = null,
        IConceptBufferStore? buffer = null,
        IConceptHistoryStore? history = null,
        ISettingsStore? settings = null)
        => new(
            provider  ?? new CountingProvider(),
            buffer    ?? new InMemoryBufferStore(),
            history   ?? new EmptyHistory(),
            settings  ?? new CloudModeSettings(),
            NullLogger<CloudPrefetchService>.Instance);

    // ── (a) 7-day prefetch = 21 unique concepts ───────────────────────────────

    [Fact]
    public async Task FillToTarget_Produces21Concepts_AllUnique()
    {
        var provider = new CountingProvider();
        var svc      = MakeSvc(provider);

        await svc.FillToTargetAsync(CancellationToken.None);

        Assert.Equal(21, provider.Generated.Count);
        Assert.Equal(21, provider.Generated.Distinct().Count());
    }

    [Fact]
    public async Task FillToTarget_BufferContains7Sets_After_EmptyStart()
    {
        var buffer = new InMemoryBufferStore();
        var svc    = MakeSvc(buffer: buffer);

        await svc.FillToTargetAsync(CancellationToken.None);

        Assert.Equal(7, await buffer.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FillToTarget_AvoidListGrowsAcrossWholeBatch()
    {
        var provider = new CountingProvider();
        var svc      = MakeSvc(provider);

        await svc.FillToTargetAsync(CancellationToken.None);

        // First concept: no avoid list yet
        Assert.Empty(provider.ReceivedAvoidLists[0]);
        // Second concept: must avoid first title
        Assert.Single(provider.ReceivedAvoidLists[1]);
        Assert.Contains(provider.Generated[0], provider.ReceivedAvoidLists[1]);
        // Last concept (21st): must avoid all 20 preceding titles
        Assert.Equal(20, provider.ReceivedAvoidLists[20].Count);
    }

    [Fact]
    public async Task FillToTarget_DeduplicatesAgainstHistory()
    {
        var provider = new CountingProvider();
        var history  = new SeededHistory("OldTitle1", "OldTitle2", "OldTitle3");
        var svc      = MakeSvc(provider, history: history);

        await svc.FillToTargetAsync(CancellationToken.None);

        Assert.Contains("OldTitle1", provider.ReceivedAvoidLists[0]);
        Assert.Contains("OldTitle2", provider.ReceivedAvoidLists[0]);
        Assert.Contains("OldTitle3", provider.ReceivedAvoidLists[0]);
    }

    [Fact]
    public async Task FillToTarget_OnlyFetchesMissing_WhenBufferPartiallyFull()
    {
        var provider = new CountingProvider();
        var buffer   = new InMemoryBufferStore();
        buffer.Seed(4); // already have 4 days

        var svc = MakeSvc(provider, buffer);
        await svc.FillToTargetAsync(CancellationToken.None);

        // Only 3 more days needed (7 - 4 = 3 × 3 = 9 concepts)
        Assert.Equal(9, provider.Generated.Count);
        Assert.Equal(7, await buffer.CountAsync(CancellationToken.None));
    }

    // ── (b) Refill threshold ──────────────────────────────────────────────────

    [Fact]
    public async Task TryConsume_TriggersRefill_WhenRemainingDropsBelowThreshold()
    {
        var buffer       = new InMemoryBufferStore();
        var refillCalled = false;

        // Seed exactly at threshold so consuming one drops below it
        buffer.Seed(CloudPrefetchService.RefillThresholdDays);

        var svc = new SpyPrefetchService(
            new CountingProvider(), buffer, new EmptyHistory(), new CloudModeSettings(),
            () => refillCalled = true);

        await svc.TryConsumeAsync(CancellationToken.None);
        await Task.Delay(150); // let fire-and-forget task complete

        Assert.True(refillCalled);
    }

    [Fact]
    public async Task TryConsume_DoesNotTriggerRefill_WhenAboveThreshold()
    {
        var buffer       = new InMemoryBufferStore();
        var refillCalled = false;

        // Seed well above threshold
        buffer.Seed(CloudPrefetchService.RefillThresholdDays + 2);

        var svc = new SpyPrefetchService(
            new CountingProvider(), buffer, new EmptyHistory(), new CloudModeSettings(),
            () => refillCalled = true);

        await svc.TryConsumeAsync(CancellationToken.None);
        await Task.Delay(150);

        Assert.False(refillCalled);
    }

    // ── (c) Exhausted buffer + no internet → GenerationFailed, no crash ───────

    [Fact]
    public async Task TryConsume_ReturnsNull_WhenBufferEmpty()
    {
        var svc    = MakeSvc(buffer: new InMemoryBufferStore());
        var result = await svc.TryConsumeAsync(CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public async Task ConceptGenerationBackgroundService_ExhaustedBuffer_RaisesGenerationFailed()
    {
        var provider  = new CountingProvider();
        var buffer    = new InMemoryBufferStore(); // empty, never filled
        var history   = new EmptyHistory();
        var settings  = new CloudModeSettings();

        // Use a prefetch service whose FillToTargetAsync is a no-op (simulates no internet)
        var prefetch = new NoFillPrefetchService(provider, buffer, history, settings);

        var scheduler = new DailyConceptScheduler(
            provider, history, settings,
            NullLogger<DailyConceptScheduler>.Instance);

        var bgSvc = new TestableConceptGenerationBgService(
            scheduler, prefetch, settings,
            NullLogger<ConceptGenerationBackgroundService>.Instance);

        Exception? captured = null;
        bgSvc.GenerationFailed += ex => captured = ex;

        // Run with a short timeout — one iteration runs cloud-day path → buffer empty → fails
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await bgSvc.StartAsync(cts.Token);
            await Task.Delay(500, cts.Token);
        }
        catch (OperationCanceledException) { }
        await bgSvc.StopAsync(CancellationToken.None);

        Assert.NotNull(captured);
        Assert.IsType<InvalidOperationException>(captured);
    }

    /// <summary>Overrides ShouldSkipToday to always run, and FillToTargetAsync to no-op.</summary>
    private sealed class TestableConceptGenerationBgService : ConceptGenerationBackgroundService
    {
        public TestableConceptGenerationBgService(
            DailyConceptScheduler s, CloudPrefetchService p,
            ISettingsStore st,
            Microsoft.Extensions.Logging.ILogger<ConceptGenerationBackgroundService> l)
            : base(s, p, st, l) { }

        protected override bool ShouldSkipToday() => false;
    }

    /// <summary>CloudPrefetchService that never fills the buffer (simulates offline).</summary>
    private sealed class NoFillPrefetchService : CloudPrefetchService
    {
        public NoFillPrefetchService(
            IConceptProvider p, IConceptBufferStore b,
            IConceptHistoryStore h, ISettingsStore s)
            : base(p, b, h, s, NullLogger<CloudPrefetchService>.Instance) { }

        public override Task RefillIfConnectedAsync(CancellationToken ct)
            => Task.CompletedTask; // no-op — buffer stays empty

        public override async Task FillToTargetAsync(CancellationToken ct)
            => await Task.CompletedTask; // no-op on startup — buffer stays empty
    }

    // ── (d) Both modes end-to-end ─────────────────────────────────────────────

    [Fact]
    public async Task LocalMode_EndToEnd_GeneratesThreeConcepts()
    {
        var provider = new CountingProvider();
        var history  = new EmptyHistory();
        var settings = new LocalModeSettings();

        var scheduler = new DailyConceptScheduler(
            provider, history, settings,
            NullLogger<DailyConceptScheduler>.Instance);

        DailyConceptSet? received = null;
        scheduler.ConceptSetGenerated += s => received = s;

        await scheduler.RunIfDueAsync(DateOnly.FromDateTime(DateTime.Today), CancellationToken.None);

        Assert.NotNull(received);
        Assert.Equal(3, received!.Count);
    }

    [Fact]
    public async Task CloudMode_EndToEnd_ConsumesFromBuffer()
    {
        var buffer  = new InMemoryBufferStore();
        buffer.Seed(3);

        var svc = MakeSvc(buffer: buffer);
        var set = await svc.TryConsumeAsync(CancellationToken.None);

        Assert.NotNull(set);
        Assert.Equal(3, set!.Count);
        Assert.Equal(2, await buffer.CountAsync(CancellationToken.None));
    }

    // ── Spy subclass ──────────────────────────────────────────────────────────

    private sealed class SpyPrefetchService : CloudPrefetchService
    {
        private readonly Action _onRefill;

        public SpyPrefetchService(
            IConceptProvider provider,
            IConceptBufferStore buffer,
            IConceptHistoryStore history,
            ISettingsStore settings,
            Action onRefill)
            : base(provider, buffer, history, settings, NullLogger<CloudPrefetchService>.Instance)
        {
            _onRefill = onRefill;
        }

        public override Task RefillIfConnectedAsync(CancellationToken cancellationToken)
        {
            _onRefill();
            return Task.CompletedTask;
        }
    }
}
