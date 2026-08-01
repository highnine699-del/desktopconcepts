using DesktopConcepts.Application;
using DesktopConcepts.Application.Schedulers;
using DesktopConcepts.Domain;
using DesktopConcepts.Infrastructure.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SysPath = System.IO.Path;
using WpfApp = System.Windows.Application;

namespace DesktopConcepts.UI.Views;

/// <summary>
/// The single widget window. All views (Compact / Expanded / Pinned / FirstRun / Error)
/// are visibility-toggled panels — no navigation, no secondary windows.
///
/// Window is always-on-top, hidden from Alt-Tab and taskbar (WS_EX_TOOLWINDOW).
/// Deactivated event drives Compact←Expanded so no global mouse hook is needed.
/// </summary>
public partial class WidgetWindow : Window
{
    // ── Win32 ────────────────────────────────────────────────────────────────
    private const int GWL_EXSTYLE      = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW  = 0x00040000;
    private const int WM_COMMAND       = 0x0111;
    private const int WM_USER          = 0x0400;
    private const int SPAWN_WORKER     = 0x052C;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, SendMessageTimeoutFlags fuFlags, uint uTimeout, out IntPtr lpdwResult);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDesktopWindow();

    [Flags]
    private enum SendMessageTimeoutFlags : uint
    {
        SMTO_NORMAL = 0x0,
        SMTO_BLOCK = 0x1,
        SMTO_ABORTIFHUNG = 0x2,
        SMTO_NOTIMEOUTIFNOTHUNG = 0x8
    }

    // ── Tunable constants ────────────────────────────────────────────────────
    /// <summary>
    /// How long the Expanded view stays open before auto-collapsing to Compact.
    /// Satisfies the §1.3 "Collapsed: returns to Compact after timeout" requirement.
    /// </summary>
    private static readonly TimeSpan ExpandedTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// RAM threshold below which local inference is considered impractical.
    /// User is shown a "consider cloud mode" nudge on first run.
    /// </summary>
    private const long LowRamThresholdBytes = 4L * 1024 * 1024 * 1024; // 4 GB

    // ── Dependencies ─────────────────────────────────────────────────────────
    private readonly WidgetStateManager     _stateManager;
    private readonly DailyConceptScheduler  _dailyScheduler;
    private readonly RotationScheduler      _rotationScheduler;
    private readonly ModelDownloadService   _downloadService;
    private readonly CloudPrefetchService   _prefetchService;
    private readonly ISettingsStore         _settingsStore;
    private readonly IServiceProvider       _services;
    private readonly ILogger<WidgetWindow>  _logger;

    // ── Runtime state ────────────────────────────────────────────────────────
    private Concept?        _currentConcept;
    private int             _currentIndex;      // mirrors RotationScheduler index (0-2)
    private DispatcherTimer? _expandedTimer;    // fires Timeout trigger after 30 s
    private CancellationTokenSource? _downloadCts;
    private TrayIcon?       _trayIcon;
    private Point           _dragStartPoint;    // for distinguishing click vs drag
    private DispatcherTimer? _positionSaveTimer; // debounced position save
    private DispatcherTimer? _workerWWatchdog;  // checks WorkerW parent every 30s
    private IntPtr?         _originalParent;    // saved for restoring normal behavior

    public WidgetWindow(
        WidgetStateManager    stateManager,
        DailyConceptScheduler dailyScheduler,
        RotationScheduler     rotationScheduler,
        ModelDownloadService  downloadService,
        CloudPrefetchService  prefetchService,
        ISettingsStore        settingsStore,
        IServiceProvider      services,
        ILogger<WidgetWindow> logger)
    {
        _stateManager      = stateManager;
        _dailyScheduler    = dailyScheduler;
        _rotationScheduler = rotationScheduler;
        _downloadService   = downloadService;
        _prefetchService   = prefetchService;
        _settingsStore     = settingsStore;
        _services          = services;
        _logger            = logger;

        InitializeComponent();

        _stateManager.StateChanged        += OnStateChanged;
        _rotationScheduler.ConceptRotated += OnConceptRotated;

        // Download service events
        _downloadService.ProgressChanged   += OnDownloadProgress;
        _downloadService.DownloadCompleted += OnDownloadCompleted;
        _downloadService.DownloadFailed    += OnDownloadFailed;

        // NOTE: ConceptSetReady and GenerationFailed are wired from App.xaml.cs
        // via the public OnConceptSetReady / OnGenerationFailed methods so both
        // local (DailyConceptScheduler) and cloud (CloudPrefetchService) flows
        // share a single delivery path.

        // Load saved position or set default top-right
        _ = ApplyPositionAndOpacityAsync();
    }

    // ── Window lifetime ───────────────────────────────────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyToolWindowStyle();
        _rotationScheduler.Start();

        // Wire system-tray icon (Task 1) — must come after handle is created
        _trayIcon = new TrayIcon(this);
        _trayIcon.ToggleRequested       += TrayToggle;
        _trayIcon.OpenSettingsRequested += () => OpenSettings_Click(this, new RoutedEventArgs());
        _trayIcon.QuitRequested         += () => WpfApp.Current.Shutdown();

        // Wire position save timer (debounced)
        LocationChanged += OnLocationChanged;

        // Apply WorkerW pinning if enabled
        _ = ApplyWorkerWModeAsync();

        // Log startup timing so the <300 ms gate can be verified
        var sw = Stopwatch.GetTimestamp();
        _ = RunStartupChecksAsync().ContinueWith(_ =>
            _logger.LogInformation("Startup checks completed in {Ms:F1} ms.",
                Stopwatch.GetElapsedTime(sw).TotalMilliseconds));
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayIcon?.Dispose();
        _expandedTimer?.Stop();
        _rotationScheduler.Stop();
        _rotationScheduler.Dispose();
        _downloadCts?.Cancel();
        _downloadCts?.Dispose();
        _positionSaveTimer?.Stop();
        _workerWWatchdog?.Stop();
        base.OnClosed(e);
    }

    // ── Tray icon callbacks ───────────────────────────────────────────────────

    private void TrayToggle()
    {
        if (IsVisible)
        {
            Hide();
            _logger.LogDebug("Widget hidden via tray.");
        }
        else
        {
            Show();
            Activate();
            _logger.LogDebug("Widget shown via tray.");
        }
    }

    // ── Startup checks (setup choice → first-run → normal) ───────────────────

    private async Task RunStartupChecksAsync()
    {
        var settings = await _settingsStore.LoadAsync(CancellationToken.None);

        // SETUP CHOICE — must happen before any other first-run logic.
        // IsFirstRun is true until the user picks local or cloud on the setup screen.
        if (settings.IsFirstRun)
        {
            _logger.LogInformation("First run detected — showing setup choice screen.");
            Dispatcher.Invoke(ShowSetupChoiceView);
            // Execution resumes inside SetupChooseLocal_Click / SetupChooseCloud_Click
            // via ContinueAfterSetupChoiceAsync, so we return here.
            return;
        }

        await ContinueAfterSetupChoiceAsync(settings);
    }

    /// <summary>
    /// Called after the user dismisses the setup choice (or on all subsequent runs).
    /// Runs the local-model download check (local mode) or cloud prefetch (cloud mode).
    /// </summary>
    private async Task ContinueAfterSetupChoiceAsync(AppSettings settings)
    {
        if (settings.Mode == "local")
        {
            var modelPath = GetModelPath(settings);

            if (!IsRamSufficient())
            {
                _logger.LogWarning("Low RAM detected (<4 GB). Recommending cloud mode.");
                Dispatcher.Invoke(ShowLowRamNudge);
                return;
            }

            if (!ModelDownloadService.IsModelPresent(modelPath))
            {
                _logger.LogInformation(
                    "Model not found at {Path}. Showing first-run download view.", modelPath);
                Dispatcher.Invoke(ShowFirstRunView);
                await StartModelDownloadAsync(settings.Provider.BaseUrl, modelPath);
            }
        }
        // Cloud mode: prefill is handled by ConceptGenerationBackgroundService on startup.
        // Nothing to do here — the widget goes straight to Compact.
    }

    private static string GetModelPath(AppSettings settings)
    {
        // Store the model under %AppData%\DesktopConcepts\Models\<model-name>.gguf
        return SysPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DesktopConcepts", "Models",
            $"{settings.Provider.Model}.gguf");
    }

    private static bool IsRamSufficient()
    {
        // MEMORYSTATUSEX is the Win32 struct for GlobalMemoryStatusEx
        var mem = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref mem) && mem.ullTotalPhys >= LowRamThresholdBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private void ShowLowRamNudge()
    {
        CompactView.Visibility  = Visibility.Collapsed;
        FirstRunView.Visibility = Visibility.Visible;
        DownloadStatusText.Text =
            "Your system has less than 4 GB RAM. Local AI inference may be slow or unstable. " +
            "Consider switching to Cloud mode via AI Settings.";
        DownloadProgress.Visibility = Visibility.Collapsed;
    }

    // ── Model download wiring ─────────────────────────────────────────────────

    private async Task StartModelDownloadAsync(string baseUrl, string modelPath)
    {
        // Derive a model download URL from the provider base URL.
        // For local servers this is a no-op (model is already served by the server).
        // For a real first-run download we would have a known CDN URL per model.
        // For now: if the base URL is localhost, skip the download (server already has it).
        if (baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            baseUrl.Contains("127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Local endpoint detected — skipping model download (model served by local server).");
            Dispatcher.Invoke(() =>
            {
                FirstRunView.Visibility = Visibility.Collapsed;
                CompactView.Visibility  = Visibility.Visible;
            });
            return;
        }

        _downloadCts = new CancellationTokenSource();
        var modelUrl = $"{baseUrl.TrimEnd('/')}/models/download"; // placeholder CDN pattern
        await _downloadService.DownloadAsync(modelUrl, modelPath, _downloadCts.Token);
    }

    // ── Download event handlers ───────────────────────────────────────────────

    private void OnDownloadProgress(double percent)
    {
        Dispatcher.Invoke(() =>
        {
            DownloadProgress.Value  = percent;
            DownloadStatusText.Text = $"Downloading… {percent:F0}%";
        });
    }

    private void OnDownloadCompleted()
    {
        _logger.LogInformation("Model download completed.");
        Dispatcher.Invoke(() =>
        {
            FirstRunView.Visibility = Visibility.Collapsed;
            CompactView.Visibility  = Visibility.Visible;
            _logger.LogInformation("Transitioned to Compact view after successful download.");
        });
    }

    private void OnDownloadFailed(string message)
    {
        _logger.LogError("Download failed: {Message}", message);
        Dispatcher.Invoke(() =>
        {
            DownloadErrorText.Text        = message;
            DownloadErrorPanel.Visibility = Visibility.Visible;
            DownloadStatusText.Text       = "Download failed.";
        });
    }

    // ── Win32: hide from Alt-Tab / taskbar ────────────────────────────────────

    private void ApplyToolWindowStyle()
    {
        var hwnd    = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        exStyle = (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    // ── Positioning ───────────────────────────────────────────────────────────

    private async Task ApplyPositionAndOpacityAsync()
    {
        var settings = await _settingsStore.LoadAsync(CancellationToken.None);

        // Apply opacity
        Opacity = Math.Clamp(settings.WidgetOpacity, 0.4, 1.0);

        // Apply position (saved or default top-right)
        if (settings.WidgetPosition is not null)
        {
            Left = settings.WidgetPosition.Left;
            Top = settings.WidgetPosition.Top;
            _logger.LogInformation("Restored saved position: Left={Left}, Top={Top}", Left, Top);
        }
        else
        {
            PositionTopRightDefault();
        }
    }

    private void PositionTopRightDefault()
    {
        var screen = SystemParameters.WorkArea;
        const double margin = 20;
        Left = screen.Right - Width - margin;
        Top = margin;
        _logger.LogInformation("Set default top-right position: Left={Left}, Top={Top}", Left, Top);
    }

    private void OnLocationChanged(object? sender, EventArgs e)
    {
        // Debounce position save to avoid excessive writes during drag
        _positionSaveTimer?.Stop();
        _positionSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _positionSaveTimer.Tick += async (_, _) =>
        {
            _positionSaveTimer.Stop();
            await SavePositionAsync();
        };
        _positionSaveTimer.Start();
    }

    private async Task SavePositionAsync()
    {
        var current = await _settingsStore.LoadAsync(CancellationToken.None);
        var updated = current with
        {
            WidgetPosition = new WindowPosition(Left, Top)
        };
        await _settingsStore.SaveAsync(updated, CancellationToken.None);
        _logger.LogDebug("Saved position: Left={Left}, Top={Top}", Left, Top);
    }

    private void Window_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
        {
            _dragStartPoint = e.GetPosition(this);
            MouseMove += OnWindowMouseMove;
            MouseUp += OnWindowMouseUp;
        }
    }

    private void OnWindowMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var currentPosition = e.GetPosition(this);
        var diff = currentPosition - _dragStartPoint;

        // If moved more than 3 pixels in any direction, treat as drag
        if (Math.Abs(diff.X) > 3 || Math.Abs(diff.Y) > 3)
        {
            MouseMove -= OnWindowMouseMove;
            MouseUp -= OnWindowMouseUp;
            DragMove();
        }
    }

    private void OnWindowMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        MouseMove -= OnWindowMouseMove;
        MouseUp -= OnWindowMouseUp;
    }

    // ── WorkerW (pin behind desktop icons) ─────────────────────────────────────

    private async Task ApplyWorkerWModeAsync()
    {
        var settings = await _settingsStore.LoadAsync(CancellationToken.None);
        if (!settings.PinBehindDesktopIcons) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        _originalParent = GetParent(hwnd);

        if (await TrySetWorkerWParentAsync(hwnd))
        {
            StartWorkerWWatchdog();
            _logger.LogInformation("Successfully reparented to WorkerW (pin behind desktop icons).");
        }
        else
        {
            _logger.LogWarning("Failed to find WorkerW window — falling back to normal always-on-top behavior.");
        }
    }

    private IntPtr GetParent(IntPtr hWnd)
    {
        return GetWindowLong(hWnd, -8); // GWL_HWNDPARENT = -8
    }

    private async Task<bool> TrySetWorkerWParentAsync(IntPtr hwnd)
    {
        // Find Progman
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            _logger.LogWarning("Progman window not found.");
            return false;
        }

        // Send message to spawn WorkerW (needed on newer Windows builds)
        SendMessageTimeout(progman, WM_USER + SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero,
            SendMessageTimeoutFlags.SMTO_ABORTIFHUNG, 1000, out _);

        // Find WorkerW
        var workerW = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (workerW == IntPtr.Zero)
        {
            _logger.LogWarning("WorkerW window not found after spawn message.");
            return false;
        }

        // Find the specific WorkerW that has the desktop icons (child of SHELLDLL_DefView)
        var shell = FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (shell != IntPtr.Zero)
        {
            workerW = FindWindowEx(progman, workerW, "WorkerW", null);
        }

        // Reparent our window to WorkerW
        var result = SetParent(hwnd, workerW);
        if (result == IntPtr.Zero)
        {
            _logger.LogWarning("SetParent failed with error: {Error}", Marshal.GetLastWin32Error());
            return false;
        }

        return true;
    }

    private void StartWorkerWWatchdog()
    {
        _workerWWatchdog?.Stop();
        _workerWWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _workerWWatchdog.Tick += async (_, _) =>
        {
            var settings = await _settingsStore.LoadAsync(CancellationToken.None);
            if (!settings.PinBehindDesktopIcons)
            {
                _workerWWatchdog?.Stop();
                return;
            }

            var hwnd = new WindowInteropHelper(this).Handle;
            var currentParent = GetParent(hwnd);
            var workerW = FindWorkerW();

            if (workerW == IntPtr.Zero || currentParent != workerW)
            {
                _logger.LogInformation("WorkerW parent lost — attempting to reattach.");
                if (await TrySetWorkerWParentAsync(hwnd))
                {
                    _logger.LogInformation("Successfully reattached to WorkerW.");
                }
                else
                {
                    _logger.LogWarning("Failed to reattach to WorkerW — falling back to normal behavior.");
                    _workerWWatchdog?.Stop();
                }
            }
        };
        _workerWWatchdog.Start();
    }

    private IntPtr FindWorkerW()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        SendMessageTimeout(progman, WM_USER + SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero,
            SendMessageTimeoutFlags.SMTO_ABORTIFHUNG, 1000, out _);

        var workerW = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        if (workerW == IntPtr.Zero) return IntPtr.Zero;

        var shell = FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (shell != IntPtr.Zero)
        {
            workerW = FindWindowEx(progman, workerW, "WorkerW", null);
        }

        return workerW;
    }

    private async Task RestoreNormalParentAsync()
    {
        if (_originalParent.HasValue)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetParent(hwnd, _originalParent.Value);
            _workerWWatchdog?.Stop();
            _logger.LogInformation("Restored normal parent window.");
        }
    }

    // ── Task 1: auto-collapse timer ───────────────────────────────────────────

    private void StartExpandedTimer()
    {
        _expandedTimer?.Stop();
        _expandedTimer = new DispatcherTimer { Interval = ExpandedTimeout };
        _expandedTimer.Tick += (_, _) =>
        {
            _expandedTimer.Stop();
            _stateManager.Fire(WidgetTrigger.Timeout);
            _logger.LogDebug("Expanded timeout fired — collapsing to Compact.");
        };
        _expandedTimer.Start();
    }

    private void StopExpandedTimer() => _expandedTimer?.Stop();

    // ── Deactivated → Compact ─────────────────────────────────────────────────

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        _stateManager.Fire(WidgetTrigger.OutsideClick);
    }

    // ── State machine → view transitions ─────────────────────────────────────

    private void OnStateChanged(WidgetState state)
    {
        Dispatcher.Invoke(() =>
        {
            switch (state)
            {
                case WidgetState.Compact:
                    StopExpandedTimer();
                    ShowCompact();
                    break;
                case WidgetState.Expanded:
                    StartExpandedTimer();   // start the 30-second auto-collapse clock
                    ShowExpanded();
                    break;
                case WidgetState.Pinned:
                    StopExpandedTimer();    // user explicitly pinned — kill the timeout
                    break;
            }
        });
    }

    // ── View helpers ──────────────────────────────────────────────────────────

    private void ShowSetupChoiceView()
    {
        CompactView.Visibility      = Visibility.Collapsed;
        ExpandedView.Visibility     = Visibility.Collapsed;
        ErrorView.Visibility        = Visibility.Collapsed;
        FirstRunView.Visibility     = Visibility.Collapsed;
        SetupChoiceView.Visibility  = Visibility.Visible;
    }

    private void ShowFirstRunView()
    {
        CompactView.Visibility  = Visibility.Collapsed;
        ExpandedView.Visibility = Visibility.Collapsed;
        ErrorView.Visibility    = Visibility.Collapsed;
        FirstRunView.Visibility = Visibility.Visible;
    }

    private void ShowCompact()
    {
        if (ExpandedView.Visibility == Visibility.Visible)
        {
            var fadeOut = ((Storyboard)FindResource("AnimFadeOut")).Clone();
            fadeOut.Completed += (_, _) =>
            {
                ExpandedView.Visibility = Visibility.Collapsed;
                CompactView.Visibility  = Visibility.Visible;
                PinButton.IsChecked     = false;
                ((Storyboard)FindResource("AnimFadeIn")).Begin(CompactView);
            };
            fadeOut.Begin(ExpandedView);
        }
        else
        {
            CompactView.Visibility = Visibility.Visible;
        }
        QuotaView.Visibility = Visibility.Collapsed;
        UpdateBadgeLabel();
    }

    private void ShowExpanded()
    {
        CompactView.Visibility  = Visibility.Collapsed;
        ExpandedView.Visibility = Visibility.Visible;
        ErrorView.Visibility    = Visibility.Collapsed;
        FirstRunView.Visibility = Visibility.Collapsed;
        QuotaView.Visibility    = Visibility.Collapsed;
        UpdateExpandedContent();
        ((Storyboard)FindResource("AnimSlideInUp")).Begin(ExpandedView);
    }

    private void ShowError(string message)
    {
        Dispatcher.Invoke(() =>
        {
            ErrorMessageText.Text   = message;
            CompactView.Visibility  = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Collapsed;
            FirstRunView.Visibility = Visibility.Collapsed;
            QuotaView.Visibility    = Visibility.Collapsed;
            ErrorView.Visibility    = Visibility.Visible;
        });
    }

    // ── Concept display ───────────────────────────────────────────────────────

    private void UpdateBadgeLabel()
    {
        if (_currentConcept is not null)
        {
            CompactTitle.Text = _currentConcept.Title;
            // Truncate explanation to ~60-80 characters for teaser
            var teaser = _currentConcept.Explanation.Length > 70
                ? _currentConcept.Explanation.Substring(0, 70) + "…"
                : _currentConcept.Explanation;
            CompactTeaser.Text = teaser;
        }
        else
        {
            CompactTitle.Text = "Loading…";
            CompactTeaser.Text = "Today's concept";
        }
    }

    private void UpdateExpandedContent()
    {
        if (_currentConcept is null) return;
        TitleText.Text       = _currentConcept.Title;
        ExplanationText.Text = _currentConcept.Explanation;
        CategoryTag.Text     = _currentConcept.Category;
        UpdateDots();
    }

    private void UpdateDots()
    {
        var active   = (System.Windows.Media.SolidColorBrush)FindResource("BrushPrimary");
        var inactive = (System.Windows.Media.SolidColorBrush)FindResource("BrushBorder");
        Dot1.Fill = _currentIndex == 0 ? active : inactive;
        Dot2.Fill = _currentIndex == 1 ? active : inactive;
        Dot3.Fill = _currentIndex == 2 ? active : inactive;
    }

    // ── Scheduler callbacks (public — called by App.xaml.cs for both modes) ──

    public void OnConceptSetReady(DailyConceptSet set)
    {
        _logger.LogInformation("New DailyConceptSet received for {Date}.", set.Date);
        _currentIndex = -1;
        _rotationScheduler.LoadSet(set);
        // Show tray hint on the first concept delivery if not yet seen
        _ = ShowTrayHintIfNeededAsync();
    }

    public void OnGenerationFailed(Exception ex)
    {
        _logger.LogError(ex, "Concept generation failed.");
        ShowError(
            "The AI model couldn't generate a concept right now. " +
            "Check that your AI endpoint is running (or internet is available), then retry.");
    }

    /// <summary>
    /// Called when the cloud provider returns HTTP 429 (shared quota reached).
    /// Shows QuotaView instead of the generic ErrorView — not a retryable failure.
    /// </summary>
    public void OnQuotaExceeded()
    {
        _logger.LogWarning("Quota exceeded — showing QuotaView.");
        Dispatcher.Invoke(() =>
        {
            CompactView.Visibility  = Visibility.Collapsed;
            ExpandedView.Visibility = Visibility.Collapsed;
            ErrorView.Visibility    = Visibility.Collapsed;
            FirstRunView.Visibility = Visibility.Collapsed;
            QuotaView.Visibility    = Visibility.Visible;
        });
    }

    private void OnConceptRotated(Concept concept)
    {
        Dispatcher.Invoke(() =>
        {
            _currentConcept = concept;
            _currentIndex   = (_currentIndex + 1) % 3;
            UpdateBadgeLabel();
            if (ExpandedView.Visibility == Visibility.Visible)
                UpdateExpandedContent();
        });
    }

    // ── UI event handlers ─────────────────────────────────────────────────────

    private void CompactView_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _stateManager.Fire(WidgetTrigger.Click);

    private void PinButton_Checked(object sender, RoutedEventArgs e)
        => _stateManager.Fire(WidgetTrigger.Pin);

    private void PinButton_Unchecked(object sender, RoutedEventArgs e)
        => _stateManager.Fire(WidgetTrigger.Unpin);

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        // Reset the expanded timer so user gets another full 30 s after pressing Next
        StartExpandedTimer();
        _rotationScheduler.AdvanceNow();
        _logger.LogDebug("User pressed Next.");
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_currentConcept is null) return;
        Clipboard.SetText($"{_currentConcept.Title}\n\n{_currentConcept.Explanation}");
        _logger.LogInformation("Copied to clipboard: {Title}", _currentConcept.Title);
    }

    private void ReadMore_Click(object sender, RoutedEventArgs e)
    {
        if (_currentConcept is null) return;
        var query = Uri.EscapeDataString(_currentConcept.Title);
        Process.Start(new ProcessStartInfo
        {
            FileName        = $"https://www.google.com/search?q={query}",
            UseShellExecute = true
        });
    }

    private void ErrorRetry_Click(object sender, RoutedEventArgs e)
    {
        ErrorView.Visibility   = Visibility.Collapsed;
        CompactView.Visibility = Visibility.Visible;
        _ = Task.Run(async () =>
        {
            var settings = await _settingsStore.LoadAsync(CancellationToken.None);
            if (settings.Mode == "cloud")
            {
                // Try refilling the buffer first, then consuming
                await _prefetchService.RefillIfConnectedAsync(CancellationToken.None);
                var set = await _prefetchService.TryConsumeAsync(CancellationToken.None);
                if (set is not null)
                    OnConceptSetReady(set);
                else
                    OnGenerationFailed(new InvalidOperationException(
                        "Cloud buffer is still empty. Check your internet connection."));
            }
            else
            {
                await _dailyScheduler.RunIfDueAsync(
                    DateOnly.FromDateTime(DateTime.Now), CancellationToken.None);
            }
        });
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        // Resolve a fresh SettingsWindow from DI (transient) and show it
        var settingsWin = _services.GetRequiredService<SettingsWindow>();
        settingsWin.Owner = this;
        settingsWin.Closed += async (_, _) => await ReapplySettingsAsync();
        settingsWin.ShowDialog();
    }

    private async Task ReapplySettingsAsync()
    {
        var settings = await _settingsStore.LoadAsync(CancellationToken.None);

        // Apply opacity
        Opacity = Math.Clamp(settings.WidgetOpacity, 0.4, 1.0);

        // Apply/restore WorkerW mode
        if (settings.PinBehindDesktopIcons)
        {
            if (_workerWWatchdog == null)
            {
                await ApplyWorkerWModeAsync();
            }
        }
        else
        {
            await RestoreNormalParentAsync();
        }
    }

    private void DownloadRetry_Click(object sender, RoutedEventArgs e)
    {
        DownloadErrorPanel.Visibility = Visibility.Collapsed;
        DownloadStatusText.Text       = "Retrying download…";
        DownloadProgress.Value        = 0;
        DownloadProgress.Visibility   = Visibility.Visible;

        _ = Task.Run(async () =>
        {
            var settings  = await _settingsStore.LoadAsync(CancellationToken.None);
            var modelPath = GetModelPath(settings);
            _downloadCts?.Cancel();
            _downloadCts = new CancellationTokenSource();
            await _downloadService.DownloadAsync(
                settings.Provider.BaseUrl, modelPath, _downloadCts.Token);
        });
    }

    private void SkipToCloud_Click(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        FirstRunView.Visibility = Visibility.Collapsed;

        // Open the real Settings window with Cloud mode pre-selected so the user
        // can enter their API key immediately — no JSON editing required.
        var settingsWin = _services.GetRequiredService<SettingsWindow>();
        settingsWin.PreSelectMode("cloud");
        settingsWin.Owner = this;
        settingsWin.ShowDialog();

        // After settings dialog closes, land on Compact regardless of what they saved
        CompactView.Visibility = Visibility.Visible;
    }

    // ── Setup choice handlers (Step 5) ────────────────────────────────────────

    private void SetupChooseLocal_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _ = ApplySetupChoiceAsync("local");

    private void SetupChooseCloud_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => _ = ApplySetupChoiceAsync("cloud");

    private async Task ApplySetupChoiceAsync(string mode)
    {
        _logger.LogInformation("Setup choice: user selected '{Mode}'.", mode);

        // Save the choice — clears IsFirstRun so this screen never shows again
        var current = await _settingsStore.LoadAsync(CancellationToken.None);
        var updated = current with { Mode = mode, IsFirstRun = false };
        await _settingsStore.SaveAsync(updated, CancellationToken.None);

        // Hide the setup screen before continuing
        Dispatcher.Invoke(() => SetupChoiceView.Visibility = Visibility.Collapsed);

        // Continue the normal startup flow with the chosen mode
        await ContinueAfterSetupChoiceAsync(updated);

        // Show the tray hint now that the user has seen the widget for the first time
        await ShowTrayHintIfNeededAsync();

        // If nothing else showed a view, land on Compact
        Dispatcher.Invoke(() =>
        {
            if (CompactView.Visibility    != Visibility.Visible
             && FirstRunView.Visibility   != Visibility.Visible
             && ErrorView.Visibility      != Visibility.Visible)
            {
                CompactView.Visibility = Visibility.Visible;
            }
        });
    }

    /// <summary>Dismisses the quota-reached view and returns to Compact.</summary>
    private void QuotaDismiss_Click(object sender, RoutedEventArgs e)
    {
        QuotaView.Visibility   = Visibility.Collapsed;
        CompactView.Visibility = Visibility.Visible;
        _logger.LogInformation("Quota view dismissed by user.");
    }

    private void CtxPin_Click(object sender, RoutedEventArgs e)
    {
        if (_stateManager.Current == WidgetState.Pinned)
            _stateManager.Fire(WidgetTrigger.Unpin);
        else if (_stateManager.Current == WidgetState.Expanded)
            _stateManager.Fire(WidgetTrigger.Pin);
        else
            // Compact → Expand → Pin in one gesture
            _stateManager.Fire(WidgetTrigger.Click);
    }

    private void CtxCopy_Click(object sender, RoutedEventArgs e)
        => Copy_Click(sender, e);

    private void CtxQuit_Click(object sender, RoutedEventArgs e)
        => WpfApp.Current.Shutdown();

    // ── Fix 1: Close-to-tray + tray hint ─────────────────────────────────────

    /// <summary>
    /// × button on both Compact and Expanded views.
    /// Hides the window to the system tray — does NOT exit the app.
    /// Full exit is via tray icon right-click → Quit.
    /// </summary>
    private void CloseToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _logger.LogInformation("Widget hidden to tray via × button.");
    }

    /// <summary>
    /// Dismisses the first-run tray-hint banner and persists the flag so it never shows again.
    /// </summary>
    private void TrayHint_Dismiss(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        TrayHintBanner.Visibility = Visibility.Collapsed;
        _ = Task.Run(async () =>
        {
            var current = await _settingsStore.LoadAsync(CancellationToken.None);
            if (!current.HasSeenTrayHint)
                await _settingsStore.SaveAsync(
                    current with { HasSeenTrayHint = true }, CancellationToken.None);
        });
        _logger.LogInformation("Tray hint dismissed.");
    }

    /// <summary>
    /// Shows the tray-hint banner once — after setup choice or on the very first startup
    /// where a concept is already waiting. Checks HasSeenTrayHint so it never re-appears.
    /// </summary>
    private async Task ShowTrayHintIfNeededAsync()
    {
        var settings = await _settingsStore.LoadAsync(CancellationToken.None);
        if (settings.HasSeenTrayHint) return;
        Dispatcher.Invoke(() => TrayHintBanner.Visibility = Visibility.Visible);
    }
}
