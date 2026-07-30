using DesktopConcepts.Application;
using DesktopConcepts.Domain;

namespace DesktopConcepts.Tests.Domain;

/// <summary>
/// Exercises all 5 legal transitions from the state machine table (Build Brief §0).
/// Also verifies that illegal triggers are silently ignored.
/// </summary>
public sealed class WidgetStateManagerTests
{
    private readonly WidgetStateManager _sm = new();

    // ── Legal transitions ────────────────────────────────────────────────────

    [Fact]
    public void Compact_Click_GoesTo_Expanded()
    {
        _sm.Fire(WidgetTrigger.Click);
        Assert.Equal(WidgetState.Expanded, _sm.Current);
    }

    [Fact]
    public void Expanded_OutsideClick_GoesTo_Compact()
    {
        _sm.Fire(WidgetTrigger.Click);           // → Expanded
        _sm.Fire(WidgetTrigger.OutsideClick);    // → Compact
        Assert.Equal(WidgetState.Compact, _sm.Current);
    }

    [Fact]
    public void Expanded_Timeout_GoesTo_Compact()
    {
        _sm.Fire(WidgetTrigger.Click);
        _sm.Fire(WidgetTrigger.Timeout);
        Assert.Equal(WidgetState.Compact, _sm.Current);
    }

    [Fact]
    public void Expanded_Pin_GoesTo_Pinned()
    {
        _sm.Fire(WidgetTrigger.Click);
        _sm.Fire(WidgetTrigger.Pin);
        Assert.Equal(WidgetState.Pinned, _sm.Current);
    }

    [Fact]
    public void Pinned_Unpin_GoesTo_Compact()
    {
        _sm.Fire(WidgetTrigger.Click);
        _sm.Fire(WidgetTrigger.Pin);
        _sm.Fire(WidgetTrigger.Unpin);
        Assert.Equal(WidgetState.Compact, _sm.Current);
    }

    // ── Illegal triggers ignored ─────────────────────────────────────────────

    [Fact]
    public void Compact_Unpin_IsIgnored()
    {
        _sm.Fire(WidgetTrigger.Unpin);
        Assert.Equal(WidgetState.Compact, _sm.Current);
    }

    [Fact]
    public void Compact_Timeout_IsIgnored()
    {
        _sm.Fire(WidgetTrigger.Timeout);
        Assert.Equal(WidgetState.Compact, _sm.Current);
    }

    [Fact]
    public void Pinned_Click_IsIgnored()
    {
        _sm.Fire(WidgetTrigger.Click);
        _sm.Fire(WidgetTrigger.Pin);
        _sm.Fire(WidgetTrigger.Click);           // illegal from Pinned
        Assert.Equal(WidgetState.Pinned, _sm.Current);
    }

    // ── StateChanged event ───────────────────────────────────────────────────

    [Fact]
    public void StateChanged_RaisedOnTransition()
    {
        var raised = new List<WidgetState>();
        _sm.StateChanged += s => raised.Add(s);

        _sm.Fire(WidgetTrigger.Click);           // Compact→Expanded
        _sm.Fire(WidgetTrigger.Pin);             // Expanded→Pinned
        _sm.Fire(WidgetTrigger.Unpin);           // Pinned→Compact

        Assert.Equal([WidgetState.Expanded, WidgetState.Pinned, WidgetState.Compact], raised);
    }

    [Fact]
    public void StateChanged_NotRaisedOnIllegalTrigger()
    {
        var raised = 0;
        _sm.StateChanged += _ => raised++;

        _sm.Fire(WidgetTrigger.Unpin);  // illegal from Compact
        Assert.Equal(0, raised);
    }
}
