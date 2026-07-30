using System.Text;
using DesktopConcepts.Domain;
using Microsoft.Extensions.Logging;

namespace DesktopConcepts.Infrastructure.Storage;

/// <summary>
/// Append-only Markdown history store under %AppData%\DesktopConcepts\History.md.
/// All three concepts in a DailyConceptSet are written atomically in a single file-open.
/// Titles are extracted for the dedupe avoid-list fed back into the AI prompt.
/// </summary>
public sealed class MarkdownHistoryStore : IConceptHistoryStore
{
    private readonly string _path;
    private readonly ILogger<MarkdownHistoryStore> _logger;

    // Heading format: ## 2025-07-29 [2/3] — Some Concept Title
    private const string HeadingPrefix = "## ";
    private const char EmDash = '—';

    public MarkdownHistoryStore(ILogger<MarkdownHistoryStore> logger, string? overridePath = null)
    {
        _logger = logger;
        _path = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopConcepts", "History.md");
    }

    /// <inheritdoc/>
    public async Task AppendSetAsync(DailyConceptSet conceptSet, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var sb = new StringBuilder();
        for (var i = 0; i < conceptSet.Concepts.Count; i++)
        {
            var concept = conceptSet.Concepts[i];
            sb.AppendLine();
            sb.AppendLine($"{HeadingPrefix}{concept.GeneratedOn:yyyy-MM-dd} [{i + 1}/3] {EmDash} {concept.Title}");
            sb.AppendLine($"*Category: {concept.Category}*");
            sb.AppendLine();
            sb.AppendLine(concept.Explanation);
        }

        await File.AppendAllTextAsync(_path, sb.ToString(), Encoding.UTF8, cancellationToken);
        _logger.LogInformation(
            "Appended {Count} concepts for {Date} to history.", conceptSet.Count, conceptSet.Date);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetRecentTitlesAsync(
        int count, CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];

        var lines = await File.ReadAllLinesAsync(_path, Encoding.UTF8, cancellationToken);
        return lines
            .Where(l => l.StartsWith(HeadingPrefix, StringComparison.Ordinal))
            .Select(ExtractTitle)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .TakeLast(count)
            .ToList()!;
    }

    // "## 2025-07-29 [1/3] — TCP/IP Stack" → "TCP/IP Stack"
    private static string? ExtractTitle(string line)
    {
        var dashPos = line.IndexOf(EmDash);
        return dashPos < 0 ? null : line[(dashPos + 1)..].Trim();
    }
}
