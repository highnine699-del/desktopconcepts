using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopConcepts.Application.Schedulers;

/// <summary>
/// Silent automatic updater: checks for newer releases and installs them without user interaction.
/// Checks once at startup, then every 24 hours.
///
/// Uses the GitHub Releases API to find the latest release, downloads the Setup.exe asset,
/// and launches it with silent InnoSetup flags. Never crashes the app — all failures are
/// logged and silently retried on the next check.
/// </summary>
public sealed class RefreshScheduler : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    private readonly ILogger<RefreshScheduler> _logger;
    private readonly IHttpClientFactory        _httpFactory;

    public RefreshScheduler(IHttpClientFactory httpFactory, ILogger<RefreshScheduler> logger)
    {
        _httpFactory = httpFactory;
        _logger      = logger;
    }

    /// <summary>
    /// Gets the current app version from the assembly's informational version.
    /// Falls back to "1.0.0" if not available.
    /// </summary>
    private static string GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        if (assembly is null) return "1.0.0";

        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(version))
        {
            version = assembly.GetName().Version?.ToString();
        }

        return version ?? "1.0.0";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check immediately at startup, then on the 24h cadence
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckForUpdateAsync(stoppingToken);
            await Task.Delay(CheckInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var currentVersion = GetCurrentVersion();
            _logger.LogDebug("Checking for updates (current version: {Version})", currentVersion);

            using var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopConcepts/" + currentVersion);

            // GitHub API: latest release for the correct repo
            const string apiUrl =
                "https://api.github.com/repos/highnine699-del/desktopconcepts/releases/latest";

            var response = await http.GetAsync(apiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Update check returned {Status}.", response.StatusCode);
                return;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);

            if (release?.TagName is null) return;

            var latest = release.TagName.TrimStart('v', 'V');

            // Log both versions for debugging
            _logger.LogDebug("Version comparison: current='{Current}', latest='{Latest}' (raw tag='{Tag}')",
                currentVersion, latest, release.TagName);

            if (string.Compare(latest, currentVersion, StringComparison.Ordinal) > 0)
            {
                _logger.LogInformation(
                    "Update available: v{Latest} (current: v{Current}). Initiating silent download.",
                    latest, currentVersion);

                await DownloadAndInstallUpdateAsync(release, cancellationToken);
            }
            else
            {
                _logger.LogDebug("App is up to date (v{Current}).", currentVersion);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            // Never let an update check crash or surface to the user
            _logger.LogDebug(ex, "Update check failed silently.");
        }
    }

    private async Task DownloadAndInstallUpdateAsync(GitHubRelease release, CancellationToken cancellationToken)
    {
        try
        {
            // Find the Setup.exe asset
            var setupAsset = release.Assets?.FirstOrDefault(a =>
                a.Name?.Equals("Setup.exe", StringComparison.OrdinalIgnoreCase) == true);

            if (setupAsset is null || setupAsset.DownloadUrl is null)
            {
                _logger.LogWarning("Update available but no Setup.exe asset found in release.");
                return;
            }

            // Download to temp folder
            var tempPath = Path.Combine(Path.GetTempPath(), $"DesktopConcepts-Update-{Guid.NewGuid()}.exe");

            _logger.LogDebug("Downloading update to {TempPath}", tempPath);

            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(10); // Allow time for larger downloads

            var response = await http.GetAsync(setupAsset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to download Setup.exe: {Status}", response.StatusCode);
                return;
            }

            // Verify file size if available
            var expectedSize = setupAsset.Size;
            var contentLength = response.Content.Headers.ContentLength;

            if (expectedSize > 0 && contentLength.HasValue)
            {
                _logger.LogDebug("Download size: {Downloaded} bytes (expected: {Expected} bytes)",
                    contentLength.Value, expectedSize);
            }

            // Stream to file
            await using var fileStream = File.Create(tempPath);
            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await contentStream.CopyToAsync(fileStream, cancellationToken);

            var downloadedSize = new FileInfo(tempPath).Length;
            _logger.LogInformation("Downloaded {Bytes} bytes to {TempPath}", downloadedSize, tempPath);

            // Basic sanity check: file should not be empty
            if (downloadedSize == 0)
            {
                _logger.LogWarning("Downloaded file is empty, aborting update.");
                File.Delete(tempPath);
                return;
            }

            // Launch silent install
            await LaunchSilentInstallAsync(tempPath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Silent update download/install failed. Will retry on next check.");
        }
    }

    private Task LaunchSilentInstallAsync(string setupPath, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Launching silent installer: {Path}", setupPath);

            // InnoSetup silent flags:
            // /VERYSILENT - no UI at all
            // /SUPPRESSMSGBOXES - suppress any message boxes
            // /NORESTART - don't restart the system
            // /CLOSEAPPLICATIONS - allow installer to close running instances
            // /RESTARTAPPLICATIONS - restart the app after install
            var arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS";

            var startInfo = new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = arguments,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(startInfo);

            _logger.LogInformation("Silent installer launched successfully. App may close and restart.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to launch silent installer.");
        }

        return Task.CompletedTask;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]  public string? TagName  { get; init; }
        [JsonPropertyName("html_url")]  public string? HtmlUrl  { get; init; }
        [JsonPropertyName("assets")]   public GitHubAsset[]? Assets { get; init; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]         public string? Name         { get; init; }
        [JsonPropertyName("browser_download_url")] public string? DownloadUrl { get; init; }
        [JsonPropertyName("size")]        public long   Size         { get; init; }
    }
}
