using System.Text;
using System.Text.RegularExpressions;
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
    private const char   EmDash        = '—';

    public MarkdownHistoryStore(ILogger<MarkdownHistoryStore> logger, string? overridePath = null)
    {
        _logger = logger;
        _path   = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopConcepts", "History.md");
    }

    // ── AppendSetAsync ────────────────────────────────────────────────────────

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

    // ── GetRecentTitlesAsync ──────────────────────────────────────────────────

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

    // ── GetMostRecentSetAsync ─────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<DailyConceptSet?> GetMostRecentSetAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return null;

        var lines = await File.ReadAllLinesAsync(_path, Encoding.UTF8, cancellationToken);

        // Build the full heading index with line positions
        var headingLines = lines
            .Select((line, index) => (Line: line, Index: index))
            .Where(x => x.Line.StartsWith(HeadingPrefix, StringComparison.Ordinal))
            .ToList();

        if (headingLines.Count == 0) return null;

        // Determine the most-recent date from the last heading in the file
        var lastHeading = headingLines.Last();
        var dateMatch   = Regex.Match(lastHeading.Line, @"## (\d{4}-\d{2}-\d{2})");
        if (!dateMatch.Success) return null;

        var date       = DateOnly.ParseExact(dateMatch.Groups[1].Value, "yyyy-MM-dd");
        var datePrefix = $"{HeadingPrefix}{date:yyyy-MM-dd}";

        // Collect only the headings that belong to this date (up to 3)
        var dateHeadings = headingLines
            .Where(h => h.Line.StartsWith(datePrefix, StringComparison.Ordinal))
            .ToList();

        if (dateHeadings.Count == 0) return null;

        var concepts = new List<Concept>(dateHeadings.Count);

        for (int i = 0; i < dateHeadings.Count; i++)
        {
            var headingIdx  = dateHeadings[i].Index;
            var headingLine = dateHeadings[i].Line;

            var title = ExtractTitle(headingLine);
            if (string.IsNullOrWhiteSpace(title)) continue;

            // Category line is always immediately after the heading
            var category = "General";
            if (headingIdx + 1 < lines.Length)
            {
                var catMatch = Regex.Match(lines[headingIdx + 1], @"\*Category: (.+)\*");
                if (catMatch.Success) category = catMatch.Groups[1].Value;
            }

            // Explanation starts at headingIdx + 3 (heading, category, blank line, then text).
            // It ends at the next heading in the file (any heading, not just this date's).
            var explanationStart = headingIdx + 3;

            // Find the line index of the next heading strictly after this concept's heading.
            int nextHeadingLine = lines.Length; // default: end of file
            foreach (var h in headingLines)
            {
                if (h.Index > headingIdx)
                {
                    nextHeadingLine = h.Index;
                    break;
                }
            }

            // Gather explanation lines — preserve internal content, trim leading/trailing blanks
            var explanationLines = new List<string>();
            for (int j = explanationStart; j < nextHeadingLine && j < lines.Length; j++)
            {
                if (lines[j].StartsWith(HeadingPrefix, StringComparison.Ordinal)) break;
                explanationLines.Add(lines[j]);
            }

            var explanation = string.Join("\n", explanationLines).Trim();
            if (string.IsNullOrWhiteSpace(explanation))
                explanation = "(no explanation recorded)";

            concepts.Add(new Concept(title, explanation, category, date));
        }

        if (concepts.Count == 0) return null;

        if (concepts.Count < 3)
            _logger.LogWarning(
                "GetMostRecentSetAsync: expected 3 concepts for {Date}, found {Count}.",
                date, concepts.Count);

        return new DailyConceptSet(date, concepts.AsReadOnly());
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the concept title from a heading line.
    /// Format: "## 2025-07-29 [1/3] — TCP/IP Stack"  → "TCP/IP Stack"
    ///
    /// Handles both the canonical em-dash (—) and a plain hyphen-minus (-) for
    /// resilience against hand-edited history files or very old entries.
    /// </summary>
    private static string? ExtractTitle(string line)
    {
        // Prefer canonical em-dash separator first
        var dashPos = line.IndexOf(EmDash);

        // Fall back to the first " - " (space-hyphen-space) for hand-edited or legacy entries
        if (dashPos < 0)
            dashPos = line.IndexOf(" - ", StringComparison.Ordinal);

        if (dashPos < 0) return null;

        // Advance past the separator character itself
        var afterSep = dashPos + 1;
        if (afterSep >= line.Length) return null;

        return line[afterSep..].Trim();
    }
}
