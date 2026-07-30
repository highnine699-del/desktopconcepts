using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopConcepts.Domain;
using Microsoft.Extensions.Logging;

namespace DesktopConcepts.Infrastructure.Storage;

/// <summary>
/// Cloud-mode prefetch buffer backed by a single JSON file at
/// %AppData%\DesktopConcepts\buffer.json.
///
/// Completely separate from History.md — this is a queue, not a permanent log.
/// Sets are stored as an ordered list; TryTakeNextAsync pops the first entry.
///
/// Thread-safety: all public methods are serialised through a SemaphoreSlim so
/// background refills and foreground reads never race on the file.
/// </summary>
public sealed class JsonConceptBufferStore : IConceptBufferStore
{
    private readonly string _path;
    private readonly ILogger<JsonConceptBufferStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented         = true,
        PropertyNameCaseInsensitive = true,
        Converters            = { new DateOnlyConverter() }
    };

    public JsonConceptBufferStore(
        ILogger<JsonConceptBufferStore> logger,
        string? overridePath = null)
    {
        _logger = logger;
        _path   = overridePath ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopConcepts", "buffer.json");
    }

    // ── IConceptBufferStore ───────────────────────────────────────────────────

    public async Task AddRangeAsync(
        IReadOnlyList<DailyConceptSet> sets, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var buffer = await LoadBufferAsync(cancellationToken);
            foreach (var set in sets)
                buffer.Entries.Add(BufferEntry.From(set));
            await SaveBufferAsync(buffer, cancellationToken);
            _logger.LogInformation(
                "Buffer: added {Count} sets. Total now {Total}.",
                sets.Count, buffer.Entries.Count);
        }
        finally { _lock.Release(); }
    }

    public async Task<DailyConceptSet?> TryTakeNextAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var buffer = await LoadBufferAsync(cancellationToken);
            if (buffer.Entries.Count == 0)
            {
                _logger.LogWarning("Buffer empty — no set to consume.");
                return null;
            }

            var entry = buffer.Entries[0];
            buffer.Entries.RemoveAt(0);
            await SaveBufferAsync(buffer, cancellationToken);

            _logger.LogInformation(
                "Buffer: consumed set for {Date}. {Remaining} remaining.",
                entry.Date, buffer.Entries.Count);

            return entry.ToDomain();
        }
        finally { _lock.Release(); }
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var buffer = await LoadBufferAsync(cancellationToken);
            return buffer.Entries.Count;
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<DateOnly>> PeekDatesAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var buffer = await LoadBufferAsync(cancellationToken);
            return buffer.Entries.Select(e => e.Date).ToList().AsReadOnly();
        }
        finally { _lock.Release(); }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<BufferFile> LoadBufferAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return new BufferFile();

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<BufferFile>(
                       stream, JsonOpts, cancellationToken)
                   ?? new BufferFile();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Buffer file corrupt or unreadable; resetting.");
            return new BufferFile();
        }
    }

    private async Task SaveBufferAsync(BufferFile buffer, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, buffer, JsonOpts, cancellationToken);
    }

    // ── JSON DTOs (internal to this file) ────────────────────────────────────

    private sealed class BufferFile
    {
        [JsonPropertyName("entries")]
        public List<BufferEntry> Entries { get; init; } = [];
    }

    private sealed record BufferEntry(
        [property: JsonPropertyName("date")]     DateOnly          Date,
        [property: JsonPropertyName("concepts")] List<ConceptDto>  Concepts)
    {
        public static BufferEntry From(DailyConceptSet set) => new(
            set.Date,
            set.Concepts.Select(ConceptDto.From).ToList());

        public DailyConceptSet ToDomain() => new(
            Date,
            Concepts.Select(c => c.ToDomain()).ToList().AsReadOnly());
    }

    private sealed record ConceptDto(
        [property: JsonPropertyName("title")]       string  Title,
        [property: JsonPropertyName("explanation")] string  Explanation,
        [property: JsonPropertyName("category")]    string  Category,
        [property: JsonPropertyName("generatedOn")] DateOnly GeneratedOn)
    {
        public static ConceptDto From(Concept c) =>
            new(c.Title, c.Explanation, c.Category, c.GeneratedOn);

        public Concept ToDomain() =>
            new(Title, Explanation, Category, GeneratedOn);
    }
}

/// <summary>
/// System.Text.Json converter for DateOnly (not natively supported before .NET 7 AOT).
/// </summary>
internal sealed class DateOnlyConverter : JsonConverter<DateOnly>
{
    public override DateOnly Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => DateOnly.ParseExact(reader.GetString()!, "yyyy-MM-dd");

    public override void Write(
        Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("yyyy-MM-dd"));
}
