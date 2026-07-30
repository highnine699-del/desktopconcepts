namespace DesktopConcepts.Domain;

/// <summary>The three legal visual states of the widget.</summary>
public enum WidgetState
{
    Compact,
    Expanded,
    Pinned
}

/// <summary>
/// All triggers that can drive a state transition.
/// Any trigger not valid for the current state is silently ignored —
/// never thrown (see WidgetStateManager).
/// </summary>
public enum WidgetTrigger
{
    Click,
    OutsideClick,
    Timeout,
    Pin,
    Unpin
}
