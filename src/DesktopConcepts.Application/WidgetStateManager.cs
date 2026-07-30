using DesktopConcepts.Domain;

namespace DesktopConcepts.Application;

/// <summary>
/// Enforces the widget state machine. Only the five legal transitions are allowed;
/// any other trigger/state combination is silently ignored — never thrown.
///
/// Legal transitions:
///   Compact  + Click        → Expanded
///   Expanded + OutsideClick → Compact
///   Expanded + Timeout      → Compact
///   Expanded + Pin          → Pinned
///   Pinned   + Unpin        → Compact
/// </summary>
public sealed class WidgetStateManager
{
    public WidgetState Current { get; private set; } = WidgetState.Compact;

    /// <summary>Raised after every successful state change, with the new state.</summary>
    public event Action<WidgetState>? StateChanged;

    public void Fire(WidgetTrigger trigger)
    {
        var next = (Current, trigger) switch
        {
            (WidgetState.Compact,  WidgetTrigger.Click)        => WidgetState.Expanded,
            (WidgetState.Expanded, WidgetTrigger.OutsideClick) => WidgetState.Compact,
            (WidgetState.Expanded, WidgetTrigger.Timeout)      => WidgetState.Compact,
            (WidgetState.Expanded, WidgetTrigger.Pin)          => WidgetState.Pinned,
            (WidgetState.Pinned,   WidgetTrigger.Unpin)        => WidgetState.Compact,
            _                                                  => Current  // illegal — ignored
        };

        if (next == Current) return;
        Current = next;
        StateChanged?.Invoke(Current);
    }
}
