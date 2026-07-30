using DesktopConcepts.Application.Schedulers;
using DesktopConcepts.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopConcepts.Tests.Application;

/// <summary>
/// Acceptance checklist items:
///   - Airplane mode + local mode  → generation fails, GenerationFailed raised, nothing crashes
///   - Airplane mode + cloud mode  → same — GenerationFailed raised, no crash
///   - Config hand-corrupted       → LoadAsync falls back to defaults (covered in JsonSettingsStoreTests)
///
/// These tests use a provider that simulates network failure (HttpRequestException),
/// verifying the Application layer never propagates the exception to the caller.
/// </summary>
public sealed class OfflineGracefulFailureTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class OfflineProvider : IConceptProvider
    {
        public Task<Concept> GenerateConceptAsync(
            string category,
            IReadOnlyCollection<string> avoid,
            CancellationToken ct)
            => throw new HttpRequestException("Network unreachable (simulated airplane mode).");
    }

    private sealed class EmptyHistory : IConceptHistoryStore
    {
        public Task AppendSetAsync(DailyConceptSet s, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> GetRecentTitlesAsync(int c, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class LocalModeSettings : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct)
            => Task.FromResult(AppSettings.Default()); // mode = "local"
        public Task SaveAsync(AppSettings s, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CloudModeSettings : ISettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct)
            => Task.FromResult(AppSettings.Default() with { Mode = "cloud" });
        public Task SaveAsync(AppSettings s, CancellationToken ct) => Task.CompletedTask;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LocalMode_ProviderOffline_RaisesGenerationFailed_DoesNotThrow()
    {
        Exception? captured = null;
        var scheduler = new DailyConceptScheduler(
            new OfflineProvider(), new EmptyHistory(), new LocalModeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);
        scheduler.GenerationFailed += ex => captured = ex;

        // Must complete without throwing — Application layer catches internally
        var exception = await Record.ExceptionAsync(() =>
            scheduler.RunIfDueAsync(DateOnly.FromDateTime(DateTime.Today), CancellationToken.None));

        Assert.Null(exception);                  // no unhandled exception
        Assert.NotNull(captured);                // GenerationFailed was raised
        Assert.IsType<HttpRequestException>(captured); // correct exception type propagated to UI layer
    }

    [Fact]
    public async Task CloudMode_ProviderOffline_RaisesGenerationFailed_DoesNotThrow()
    {
        Exception? captured = null;
        var scheduler = new DailyConceptScheduler(
            new OfflineProvider(), new EmptyHistory(), new CloudModeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);
        scheduler.GenerationFailed += ex => captured = ex;

        var exception = await Record.ExceptionAsync(() =>
            scheduler.RunIfDueAsync(DateOnly.FromDateTime(DateTime.Today), CancellationToken.None));

        Assert.Null(exception);
        Assert.NotNull(captured);
    }

    [Fact]
    public async Task ProviderOffline_NothingAppendedToHistory()
    {
        var history   = new TrackingHistory();
        var scheduler = new DailyConceptScheduler(
            new OfflineProvider(), history, new LocalModeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);

        await scheduler.RunIfDueAsync(DateOnly.FromDateTime(DateTime.Today), CancellationToken.None);

        Assert.Equal(0, history.AppendCallCount); // nothing persisted on failure
    }

    [Fact]
    public async Task ProviderOffline_ConceptSetGenerated_NotRaised()
    {
        var raised    = false;
        var scheduler = new DailyConceptScheduler(
            new OfflineProvider(), new EmptyHistory(), new LocalModeSettings(),
            NullLogger<DailyConceptScheduler>.Instance);
        scheduler.ConceptSetGenerated += _ => raised = true;

        await scheduler.RunIfDueAsync(DateOnly.FromDateTime(DateTime.Today), CancellationToken.None);

        Assert.False(raised);
    }

    private sealed class TrackingHistory : IConceptHistoryStore
    {
        public int AppendCallCount { get; private set; }
        public Task AppendSetAsync(DailyConceptSet s, CancellationToken ct)
        {
            AppendCallCount++;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> GetRecentTitlesAsync(int c, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
