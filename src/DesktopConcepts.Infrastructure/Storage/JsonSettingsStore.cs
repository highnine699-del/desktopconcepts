using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopConcepts.Domain;
using Microsoft.Extensions.Logging;

namespace DesktopConcepts.Infrastructure.Storage;

/// <summary>
/// Loads and saves AppSettings as JSON under %AppData%\DesktopConcepts\Settings.json.
/// A missing, corrupted, or hand-edited file never crashes the app — falls back to AppSettings.Default().
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly ILogger<JsonSettingsStore> _logger;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Converters = { new WeekdayTopicMapConverter() }
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new WeekdayTopicMapConverter() }
    };

    public JsonSettingsStore(ILogger<JsonSettingsStore> logger, string? overridePath = null)
    {
        _logger = logger;
        _path = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopConcepts", "Settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_path))
            {
                _logger.LogInformation("Settings file not found at {Path}; using defaults.", _path);
                return AppSettings.Default();
            }

            await using var stream = File.OpenRead(_path);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                stream, ReadOptions, cancellationToken);

            return settings ?? AppSettings.Default();
        }
        catch (Exception ex)
        {
            // Corrupted / hand-edited config must never crash the app.
            _logger.LogWarning(ex, "Failed to load settings from {Path}; falling back to defaults.", _path);
            return AppSettings.Default();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, settings, WriteOptions, cancellationToken);
        _logger.LogInformation("Settings saved to {Path}.", _path);
    }
}

/// <summary>
/// Converts WeekdayTopicMap to/from a plain { "Monday": "Programming", ... } JSON object.
/// </summary>
internal sealed class WeekdayTopicMapConverter : JsonConverter<WeekdayTopicMap>
{
    public override WeekdayTopicMap Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(ref reader, options)
            ?? new Dictionary<string, string>();

        var typed = dict
            .Where(kvp => Enum.TryParse<DayOfWeek>(kvp.Key, ignoreCase: true, out _))
            .ToDictionary(
                kvp => Enum.Parse<DayOfWeek>(kvp.Key, ignoreCase: true),
                kvp => kvp.Value);

        return new WeekdayTopicMap(typed);
    }

    public override void Write(
        Utf8JsonWriter writer, WeekdayTopicMap value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var kvp in value.Categories)
            writer.WriteString(kvp.Key.ToString(), kvp.Value);
        writer.WriteEndObject();
    }
}
