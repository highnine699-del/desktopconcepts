using Microsoft.Extensions.Logging;

namespace DesktopConcepts.Infrastructure.AI;

/// <summary>
/// Handles download-on-first-run for the local AI model binary.
///
/// Requirements (Build Brief §7 / Project Plan §12):
///   - Progress reported as 0–100 double
///   - Download is resumable: a partial file is preserved so a retry continues
///   - On failure: raises DownloadFailed with a plain-language message (no raw exceptions to UI)
///   - On success: raises DownloadCompleted
///   - Never hangs; always reaches a terminal state (success or failure)
/// </summary>
public sealed class ModelDownloadService
{
    private readonly ILogger<ModelDownloadService> _logger;
    private readonly HttpClient _http;

    /// <summary>Raised repeatedly during download. Value is 0–100.</summary>
    public event Action<double>? ProgressChanged;

    /// <summary>Raised when the download completes successfully.</summary>
    public event Action? DownloadCompleted;

    /// <summary>Raised with a plain-language message when the download fails.</summary>
    public event Action<string>? DownloadFailed;

    public ModelDownloadService(HttpClient http, ILogger<ModelDownloadService> logger)
    {
        _http   = http;
        _logger = logger;
    }

    /// <summary>
    /// Downloads <paramref name="modelUrl"/> to <paramref name="destinationPath"/>.
    /// Resumes an incomplete download if a partial file already exists.
    /// </summary>
    public async Task DownloadAsync(
        string modelUrl,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

            var partialPath = destinationPath + ".partial";
            long resumeFrom = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;

            _logger.LogInformation(
                "Starting model download from {Url}. Resuming from byte {Offset}.", modelUrl, resumeFrom);

            using var request = new HttpRequestMessage(HttpMethod.Get, modelUrl);
            if (resumeFrom > 0)
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = (response.Content.Headers.ContentLength ?? 0) + resumeFrom;

            await using var stream     = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(
                partialPath, FileMode.Append, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);

            var buffer     = new byte[81920];
            long downloaded = resumeFrom;
            int  read;

            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded += read;

                if (totalBytes > 0)
                {
                    var pct = Math.Round((double)downloaded / totalBytes * 100, 1);
                    ProgressChanged?.Invoke(pct);
                }
            }

            // Finalise — rename partial → destination
            await fileStream.FlushAsync(cancellationToken);
            fileStream.Close();

            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(partialPath, destinationPath);

            _logger.LogInformation("Model download complete: {Path}", destinationPath);
            ProgressChanged?.Invoke(100);
            DownloadCompleted?.Invoke();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Model download cancelled by user.");
            // Partial file preserved for resume — do not delete it.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model download failed.");
            DownloadFailed?.Invoke(
                "Download failed: " + ex.Message +
                "\nCheck your internet connection and click Retry.");
        }
    }

    /// <summary>
    /// Returns true if the model file is already present and non-empty.
    /// </summary>
    public static bool IsModelPresent(string destinationPath)
        => File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0;
}
