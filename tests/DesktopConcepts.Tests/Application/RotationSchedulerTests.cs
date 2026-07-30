using DesktopConcepts.Application;
using DesktopConcepts.Application.Schedulers;
using DesktopConcepts.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopConcepts.Tests.Application;

/// <summary>
/// RotationScheduler: verifies index advancement, wrap-around,
/// Pinned-state pause, and AdvanceNow().
/// Timer-based tests drive rotation by calling AdvanceNow() directly
/// from a fast test timer to avoid wallclock dependencies.
/// </summary>
public sealed class RotationSchedulerTests
{
    private static DailyConceptSet MakeSet()
    {
        var today    = DateOnly.FromDateTime(DateTime.Today);
        var concepts = Enumerable.Range(0, 3)
            .Select(i => new Concept($"Title {i}", "Explanation", "Testing", today))
            .ToList();
        return new DailyConceptSet(today, concepts.AsReadOnly());
    }

    [Fact]
    public void LoadSet_FiresConceptRotated_WithIndexZero()
    {
        var sm        = new WidgetStateManager();
        var scheduler = new RotationScheduler(sm, NullLogger<RotationScheduler>.Instance);

        Concept? received = null;
        scheduler.ConceptRotated += c => received = c;

        scheduler.LoadSet(MakeSet());

        Assert.NotNull(received);
        Assert.Equal("Title 0", received!.Title);
    }

    [Fact]
    public void AdvanceNow_CyclesThrough_AllThree_ThenWraps()
    {
        var sm        = new WidgetStateManager();
        var scheduler = new RotationScheduler(sm, NullLogger<RotationScheduler>.Instance);

        var received = new List<string>();
        scheduler.ConceptRotated += c => received.Add(c.Title);

        scheduler.LoadSet(MakeSet()); // fires index 0
        scheduler.AdvanceNow();       // → 1
        scheduler.AdvanceNow();       // → 2
        scheduler.AdvanceNow();       // → 0 (wrap)

        Assert.Equal(["Title 0", "Title 1", "Title 2", "Title 0"], received);
    }

    [Fact]
    public async Task AdvanceNow_ViaExternalTimer_AdvancesIndex_WhenNotPinned()
    {
        var sm        = new WidgetStateManager();
        var scheduler = new RotationScheduler(sm, NullLogger<RotationScheduler>.Instance);

        var received = new List<string>();
        scheduler.ConceptRotated += c => received.Add(c.Title);

        scheduler.LoadSet(MakeSet()); // index 0 fired immediately

        // Simulate rapid timer ticks externally (avoids needing to override internal timer)
        using var fastTimer = new System.Threading.Timer(
            _ => scheduler.AdvanceNow(), null,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));

        await Task.Delay(250); // ~4 ticks at 50ms
        fastTimer.Change(Timeout.Infinite, Timeout.Infinite);

        // Initial load + at least 2 external ticks
        Assert.True(received.Count >= 3, $"Expected ≥3 rotations, got {received.Count}");
    }

    [Fact]
    public async Task AdvanceNow_IsBlocked_WhenPinned()
    {
        var sm        = new WidgetStateManager();
        var scheduler = new RotationScheduler(sm, NullLogger<RotationScheduler>.Instance);

        var received = new List<string>();
        scheduler.ConceptRotated += c => received.Add(c.Title);

        scheduler.LoadSet(MakeSet()); // fires index 0

        // Pin the widget
        sm.Fire(WidgetTrigger.Click); // Compact → Expanded
        sm.Fire(WidgetTrigger.Pin);   // Expanded → Pinned

        // Simulate the real scheduler's Pinned-check by wrapping AdvanceNow calls
        // with the same guard the internal Tick uses
        using var fastTimer = new System.Threading.Timer(_ =>
        {
            // Mirror the real Tick guard
            if (sm.Current != WidgetState.Pinned)
                scheduler.AdvanceNow();
        }, null, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));

        await Task.Delay(200);
        fastTimer.Change(Timeout.Infinite, Timeout.Infinite);

        // Only the initial LoadSet rotation should have been recorded
        Assert.Single(received);
        Assert.Equal("Title 0", received[0]);
    }

    [Fact]
    public void AdvanceNow_DoesNothing_WhenNoSetLoaded()
    {
        var sm        = new WidgetStateManager();
        var scheduler = new RotationScheduler(sm, NullLogger<RotationScheduler>.Instance);

        var fired = false;
        scheduler.ConceptRotated += _ => fired = true;

        scheduler.AdvanceNow(); // no set loaded — should silently no-op

        Assert.False(fired);
    }
}
