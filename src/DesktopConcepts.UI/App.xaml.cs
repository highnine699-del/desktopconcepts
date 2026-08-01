using DesktopConcepts.Application;
using DesktopConcepts.Application.Schedulers;
using DesktopConcepts.Domain;
using DesktopConcepts.Infrastructure.AI;
using DesktopConcepts.Infrastructure.Storage;
using DesktopConcepts.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace DesktopConcepts.UI;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var processStart = Stopwatch.GetTimestamp();

        // ── Serilog rolling file logger ───────────────────────────────────────
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopConcepts", "Logs", "log-.txt");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                logPath,
                rollingInterval:        RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                encoding:               System.Text.Encoding.UTF8)
            .CreateLogger();

        // ── Load settings before DI graph build ───────────────────────────────
        var settingsStore = new JsonSettingsStore(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonSettingsStore>.Instance);

        // ── Build DI host ─────────────────────────────────────────────────────
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((_, services) =>
            {
                // ── Infrastructure ────────────────────────────────────────────
                services.AddSingleton<ISettingsStore>(_ => settingsStore);
                services.AddSingleton<IConceptHistoryStore, MarkdownHistoryStore>();

                // Cloud prefetch buffer (cloud mode only — safe to register always,
                // CloudPrefetchService and ConceptGenerationBackgroundService check Mode)
                services.AddSingleton<IConceptBufferStore, JsonConceptBufferStore>();

                // AI provider — generic OpenAI-compatible endpoint
                // Provider now resolves settings dynamically via ISettingsStore
                services.AddHttpClient<IConceptProvider, OpenAiCompatibleProvider>();

                // Model download service (local first-run)
                services.AddHttpClient<ModelDownloadService>();

                // Update checker with proper User-Agent for GitHub API
                services.AddHttpClient("GitHubUpdate", client =>
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopConcepts-UpdateChecker/1.0");
                });

                // ── Application ───────────────────────────────────────────────
                services.AddSingleton<WidgetStateManager>();
                services.AddSingleton<DailyConceptScheduler>();
                services.AddSingleton<CloudPrefetchService>();
                services.AddSingleton<RotationScheduler>();

                // BackgroundService: drives daily delivery for both modes
                services.AddHostedService<ConceptGenerationBackgroundService>();

                // Auto-update check (24 h cadence)
                services.AddHostedService<RefreshScheduler>();

                // ── UI ────────────────────────────────────────────────────────
                services.AddSingleton<WidgetWindow>();
                services.AddTransient<SettingsWindow>();  // transient: new instance each open
            })
            .Build();

        await _host.StartAsync();

        var elapsedMs = Stopwatch.GetElapsedTime(processStart).TotalMilliseconds;
        Log.Information("Host started in {ElapsedMs:F1} ms.", elapsedMs);

        // Wire ConceptGenerationBackgroundService events → WidgetWindow
        // The BackgroundService is the single source of truth for concept delivery
        // in both modes; WidgetWindow subscribes through the registered singleton.
        var bgService = _host.Services
            .GetServices<IHostedService>()
            .OfType<ConceptGenerationBackgroundService>()
            .First();

        var window = _host.Services.GetRequiredService<WidgetWindow>();
        bgService.ConceptSetReady   += set => window.OnConceptSetReady(set);
        bgService.GenerationFailed  += ex  => window.OnGenerationFailed(ex);
        bgService.QuotaExceeded     += ()  => window.OnQuotaExceeded();

        MainWindow = window;
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
