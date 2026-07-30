using DesktopConcepts.Domain;
using DesktopConcepts.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopConcepts.Tests.Infrastructure;

/// <summary>
/// History store: append, title extraction, and TakeLast behaviour.
/// </summary>
public sealed class MarkdownHistoryStoreTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(), $"dc_history_{Guid.NewGuid():N}.md");

    private static Concept MakeConcept(string title, string category = "Testing") =>
        new(title, $"Explanation of {title}", category, DateOnly.FromDateTime(DateTime.Today));

    private static DailyConceptSet MakeSet(DateOnly date, params string[] titles)
    {
        var concepts = titles.Select(t => MakeConcept(t)).ToList();
        return new DailyConceptSet(date, concepts.AsReadOnly());
    }

    [Fact]
    public async Task GetRecentTitles_ReturnsEmpty_WhenNoFile()
    {
        var store  = new MarkdownHistoryStore(NullLogger<MarkdownHistoryStore>.Instance, _tempPath);
        var titles = await store.GetRecentTitlesAsync(10, CancellationToken.None);
        Assert.Empty(titles);
    }

    [Fact]
    public async Task AppendSet_ThenGetTitles_ReturnsAllThree()
    {
        var store = new MarkdownHistoryStore(NullLogger<MarkdownHistoryStore>.Instance, _tempPath);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var set   = MakeSet(today, "TCP/IP Stack", "DNS Resolution", "TLS Handshake");

        await store.AppendSetAsync(set, CancellationToken.None);
        var titles = await store.GetRecentTitlesAsync(10, CancellationToken.None);

        Assert.Equal(3, titles.Count);
        Assert.Contains("TCP/IP Stack",   titles);
        Assert.Contains("DNS Resolution", titles);
        Assert.Contains("TLS Handshake",  titles);
    }

    [Fact]
    public async Task AppendMultipleSets_TakeLast_RespectsCount()
    {
        var store = new MarkdownHistoryStore(NullLogger<MarkdownHistoryStore>.Instance, _tempPath);
        var base_ = DateOnly.FromDateTime(DateTime.Today);

        // Append 3 sets = 9 concepts total
        for (var i = 0; i < 3; i++)
        {
            var set = MakeSet(base_.AddDays(i), $"A{i}", $"B{i}", $"C{i}");
            await store.AppendSetAsync(set, CancellationToken.None);
        }

        // Ask for last 5 — should get the 5 most recent
        var titles = await store.GetRecentTitlesAsync(5, CancellationToken.None);
        Assert.Equal(5, titles.Count);

        // The very oldest concepts (A0, B0) should NOT be in the last 5
        Assert.DoesNotContain("A0", titles);
        Assert.DoesNotContain("B0", titles);
    }

    [Fact]
    public async Task AppendSet_WritesSlotTags_InMarkdown()
    {
        var store = new MarkdownHistoryStore(NullLogger<MarkdownHistoryStore>.Instance, _tempPath);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var set   = MakeSet(today, "Alpha", "Beta", "Gamma");

        await store.AppendSetAsync(set, CancellationToken.None);

        var content = await File.ReadAllTextAsync(_tempPath);
        Assert.Contains("[1/3]", content);
        Assert.Contains("[2/3]", content);
        Assert.Contains("[3/3]", content);
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }
}
