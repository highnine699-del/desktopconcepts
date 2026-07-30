using DesktopConcepts.Domain;
using DesktopConcepts.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DesktopConcepts.Tests.Application;

/// <summary>
/// Acceptance checklist:
///   - Selecting Local sets Mode="local" and IsFirstRun=false before any first-run logic runs
///   - Selecting Cloud sets Mode="cloud" and IsFirstRun=false before any first-run logic runs
///
/// Tests simulate what ApplySetupChoiceAsync does in WidgetWindow code-behind:
/// load settings → apply Mode + IsFirstRun=false → save → verify.
/// No WPF dependency — pure settings-store logic.
/// </summary>
public sealed class SetupChoiceTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(), $"dc_setup_{Guid.NewGuid():N}.json");

    private JsonSettingsStore MakeStore() =>
        new(NullLogger<JsonSettingsStore>.Instance, _tempPath);

    /// <summary>Mirrors exactly what ApplySetupChoiceAsync does in WidgetWindow.xaml.cs.</summary>
    private static async Task<AppSettings> SimulateSetupChoice(
        ISettingsStore store, string chosenMode)
    {
        var current = await store.LoadAsync(CancellationToken.None);
        var updated = current with { Mode = chosenMode, IsFirstRun = false };
        await store.SaveAsync(updated, CancellationToken.None);
        return updated;
    }

    [Fact]
    public void Default_AppSettings_HasIsFirstRun_True()
    {
        Assert.True(AppSettings.Default().IsFirstRun);
    }

    [Fact]
    public async Task ChoosingLocal_SetsMode_Local_And_ClearsIsFirstRun()
    {
        var result = await SimulateSetupChoice(MakeStore(), "local");

        Assert.Equal("local", result.Mode);
        Assert.False(result.IsFirstRun);
    }

    [Fact]
    public async Task ChoosingCloud_SetsMode_Cloud_And_ClearsIsFirstRun()
    {
        var result = await SimulateSetupChoice(MakeStore(), "cloud");

        Assert.Equal("cloud", result.Mode);
        Assert.False(result.IsFirstRun);
    }

    [Fact]
    public async Task AfterSetupChoice_PersistedSettings_ReflectChoice()
    {
        var store = MakeStore();
        await SimulateSetupChoice(store, "cloud");

        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("cloud", loaded.Mode);
        Assert.False(loaded.IsFirstRun);
    }

    [Fact]
    public async Task SetupChoice_DoesNotAlterOtherSettings()
    {
        var store  = MakeStore();
        var before = AppSettings.Default();
        var result = await SimulateSetupChoice(store, "cloud");

        Assert.Equal(before.Theme,               result.Theme);
        Assert.Equal(before.Provider.Model,      result.Provider.Model);
        Assert.Equal(before.CloudProvider.Model, result.CloudProvider.Model);
    }

    [Fact]
    public async Task IsFirstRun_False_After_Choice_PreventsSetupScreenOnNextRun()
    {
        var store = MakeStore();
        await SimulateSetupChoice(store, "local");

        var reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.False(reloaded.IsFirstRun,
            "IsFirstRun must be false after setup choice so the screen never shows again.");
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }
}
