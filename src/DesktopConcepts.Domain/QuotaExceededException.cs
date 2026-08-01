namespace DesktopConcepts.Domain;

/// <summary>
/// Thrown by IConceptProvider when the upstream provider returns HTTP 429
/// (rate limit / shared quota reached).
///
/// This is NOT a generic failure — the app surfaces a friendly "try again tomorrow"
/// message rather than the generic error view, and logs it distinctly so developers
/// can easily tell quota events apart from real failures in the log file.
/// </summary>
public sealed class QuotaExceededException : Exception
{
    public QuotaExceededException()
        : base("The shared cloud quota has been reached for today.") { }

    public QuotaExceededException(string message)
        : base(message) { }

    public QuotaExceededException(string message, Exception inner)
        : base(message, inner) { }
}
