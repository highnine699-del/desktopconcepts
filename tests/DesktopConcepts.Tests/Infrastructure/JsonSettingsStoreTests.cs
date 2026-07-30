using DesktopConcepts.Domain;
using DesktopConcepts.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopConcepts.Tests.Infrastructure;

/// <summary>
/// Settings store: fall-back behaviour on missing / corrupted files.
/// </summary>
public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(), $"dc_test_{Guid.NewGuid():N}.json");

    [Fact]
    public async Task LoadAsync_ReturnDefaults_WhenFileMissing()
    {
        var store    = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _tempPath);
        var settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("local", settings.Mode);
        Assert.Equal("dark",  settings.Theme);
        Assert.Equal("phi-3-mini", settings.Provider.Model);
        Assert.Equal("claude-haiku-4-5", settings.CloudProvider.Model);
        Assert.True(settings.IsFirstRun, "Default settings must have IsFirstRun=true.");
    }

    [Fact]
    public async Task LoadAsync_ReturnDefaults_WhenFileCorrupted()
    {
        await File.WriteAllTextAsync(_tempPath, "{ this is not valid json {{{{");

        var store    = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _tempPath);
        var settings = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("local", settings.Mode); // fell back to default
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        var store    = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _tempPath);
        var original = AppSettings.Default() with { Theme = "light", IsFirstRun = false };

        await store.SaveAsync(original, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("light", loaded.Theme);
        Assert.Equal("local", loaded.Mode);
        Assert.Equal("phi-3-mini", loaded.Provider.Model);
        Assert.False(loaded.IsFirstRun, "IsFirstRun must round-trip correctly.");
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }
}
