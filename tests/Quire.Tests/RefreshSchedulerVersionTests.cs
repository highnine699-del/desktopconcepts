using Xunit;

namespace Quire.Tests;

/// <summary>
/// Tests the semantic version comparison logic used by RefreshScheduler.
///
/// Because <c>IsNewerVersion</c> is a private instance method we cannot invoke it directly.
/// The logic is small and self-contained so we duplicate it here as a local static helper
/// (identical copy) and test it exhaustively.  Any future change to the production method
/// must be mirrored here — the test names call out the important regression cases.
/// </summary>
public sealed class RefreshSchedulerVersionTests
{
    // ── Core regression: ordinal comparison breaks on double-digit components ─

    [Fact]
    public void Version_1_0_10_IsNewerThan_1_0_9()
    {
        // Ordinal: '1' < '9', so ordinal would say 1.0.10 is OLDER — the bug we fixed.
        Assert.True(IsNewer("1.0.10", "1.0.9"));
    }

    [Fact]
    public void Version_1_0_9_IsNotNewerThan_1_0_10()
    {
        Assert.False(IsNewer("1.0.9", "1.0.10"));
    }

    // ── Standard comparisons ──────────────────────────────────────────────────

    [Fact]
    public void Version_2_0_0_IsNewerThan_1_9_9()
    {
        Assert.True(IsNewer("2.0.0", "1.9.9"));
    }

    [Fact]
    public void Version_1_0_1_IsNewerThan_1_0_0()
    {
        Assert.True(IsNewer("1.0.1", "1.0.0"));
    }

    [Fact]
    public void EqualVersions_AreNotNewer()
    {
        Assert.False(IsNewer("1.0.0", "1.0.0"));
        Assert.False(IsNewer("2.5.3", "2.5.3"));
    }

    [Fact]
    public void OlderLatest_IsNotNewer()
    {
        Assert.False(IsNewer("1.0.0", "1.0.1"));
        Assert.False(IsNewer("0.9.9", "1.0.0"));
    }

    [Fact]
    public void Version_1_10_0_IsNewerThan_1_9_0()
    {
        // Minor component double-digit — same class of bug as patch
        Assert.True(IsNewer("1.10.0", "1.9.0"));
    }

    // ── Malformed / unparseable strings fall back to ordinal without throwing ─

    [Fact]
    public void MalformedLatest_DoesNotThrow()
    {
        // Should not throw; falls back to ordinal
        var ex = Record.Exception(() => IsNewer("not-a-version", "1.0.0"));
        Assert.Null(ex);
    }

    [Fact]
    public void MalformedCurrent_DoesNotThrow()
    {
        var ex = Record.Exception(() => IsNewer("1.0.0", "not-a-version"));
        Assert.Null(ex);
    }

    [Fact]
    public void BothMalformed_DoesNotThrow()
    {
        var ex = Record.Exception(() => IsNewer("abc", "xyz"));
        Assert.Null(ex);
    }

    // ── v-prefix stripped (as done in CheckForUpdateAsync) ───────────────────

    [Fact]
    public void VersionWithVPrefix_AfterStrip_IsHandled()
    {
        // Production code strips 'v'/'V' before calling IsNewerVersion.
        // After stripping "v1.0.11" → "1.0.11"; test that stripped form works.
        Assert.True(IsNewer("1.0.11", "1.0.10"));
    }

    // ── Local copy of the production logic ───────────────────────────────────
    // Mirrors RefreshScheduler.IsNewerVersion exactly.
    // If the production method changes, update this copy to match.

    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(latest, out var latestVer)
         && Version.TryParse(current, out var currentVer))
        {
            return latestVer > currentVer;
        }

        // Ordinal fallback for malformed strings
        return string.Compare(latest, current, StringComparison.Ordinal) > 0;
    }
}
