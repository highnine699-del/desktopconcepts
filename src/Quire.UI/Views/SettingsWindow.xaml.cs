using Quire.Domain;
using Microsoft.Extensions.Logging;
using System.Windows;
using System.Windows.Media;

namespace Quire.UI.Views;

/// <summary>
/// Settings window — sidebar nav layout.
///
/// Panels:
///   AI &amp; Content — mode toggle (Local/Cloud), advanced cloud override, weekday topics
///   General        — storage info
///   Appearance     — opacity slider, pin-behind-desktop-icons
///   About          — version info
///
/// Cloud mode works with zero input from the user (shared proxy).
/// The Advanced section lets technical users point at their own provider/key.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ISettingsStore          _settingsStore;
    private readonly ILogger<SettingsWindow> _logger;

    private string  _selectedMode      = "local";
    private bool    _advancedExpanded;
    private bool    _apiKeyVisible;
    private string? _pendingModeOverride;
    private bool    _isClosed;          // guards against Close() after window is already disposed

    public SettingsWindow(ISettingsStore settingsStore, ILogger<SettingsWindow> logger)
    {
        _settingsStore = settingsStore;
        _logger        = logger;
        InitializeComponent();
        Loaded += async (_, _) => await LoadCurrentSettingsAsync();
    }

    /// <summary>
    /// Pre-selects a mode before the window opens (called before ShowDialog).
    /// Used by SkipToCloud_Click so the Cloud section is immediately visible.
    /// </summary>
    public void PreSelectMode(string mode) => _pendingModeOverride = mode;

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        base.OnClosed(e);
    }

    // ── Window chrome ─────────────────────────────────────────────────────────

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            DragMove();
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    // ── Sidebar navigation ────────────────────────────────────────────────────

    private void NavAI_Click(object sender, RoutedEventArgs e)         => ShowPanel("AI");
    private void NavGeneral_Click(object sender, RoutedEventArgs e)    => ShowPanel("General");
    private void NavAppearance_Click(object sender, RoutedEventArgs e) => ShowPanel("Appearance");
    private void NavAbout_Click(object sender, RoutedEventArgs e)      => ShowPanel("About");

    private void ShowPanel(string name)
    {
        PanelAI.Visibility         = name == "AI"         ? Visibility.Visible : Visibility.Collapsed;
        PanelGeneral.Visibility    = name == "General"    ? Visibility.Visible : Visibility.Collapsed;
        PanelAppearance.Visibility = name == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility      = name == "About"      ? Visibility.Visible : Visibility.Collapsed;

        ContentPanelTitle.Text = name switch
        {
            "AI"         => "AI & Content",
            "General"    => "General",
            "Appearance" => "Appearance",
            "About"      => "About",
            _            => name
        };

        // Update nav button styles
        NavAI.Style         = (Style)FindResource(name == "AI"         ? "NavButtonActive" : "NavButton");
        NavGeneral.Style    = (Style)FindResource(name == "General"    ? "NavButtonActive" : "NavButton");
        NavAppearance.Style = (Style)FindResource(name == "Appearance" ? "NavButtonActive" : "NavButton");
        NavAbout.Style      = (Style)FindResource(name == "About"      ? "NavButtonActive" : "NavButton");
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    private async Task LoadCurrentSettingsAsync()
    {
        var s = await _settingsStore.LoadAsync(CancellationToken.None);

        var effectiveMode = _pendingModeOverride ?? s.Mode;
        _selectedMode     = effectiveMode;
        ApplyModeSelection(effectiveMode);
        CloudSection.Visibility = effectiveMode == "cloud" ? Visibility.Visible : Visibility.Collapsed;

        // Advanced override — populate if set
        var adv = s.AdvancedCloudProvider;
        if (adv != null)
        {
            AdvancedBaseUrl.Text     = adv.BaseUrl;
            AdvancedModel.Text       = adv.Model;
            AdvancedApiKeyBox.Password  = adv.ApiKey ?? string.Empty;
            AdvancedApiKeyPlain.Text    = adv.ApiKey ?? string.Empty;

            // Auto-expand if an override is already set
            if (!string.IsNullOrWhiteSpace(adv.ApiKey) ||
                !string.IsNullOrWhiteSpace(adv.BaseUrl))
                SetAdvancedExpanded(true);
        }

        // Topics
        var map = s.Topics.Categories;
        TopicMon.Text = map.GetValueOrDefault(DayOfWeek.Monday,    "Programming");
        TopicTue.Text = map.GetValueOrDefault(DayOfWeek.Tuesday,   "Cybersecurity");
        TopicWed.Text = map.GetValueOrDefault(DayOfWeek.Wednesday, "Networking");
        TopicThu.Text = map.GetValueOrDefault(DayOfWeek.Thursday,  "AI");
        TopicFri.Text = map.GetValueOrDefault(DayOfWeek.Friday,    "Operating Systems");
        TopicSat.Text = map.GetValueOrDefault(DayOfWeek.Saturday,  "Mathematics");
        TopicSun.Text = map.GetValueOrDefault(DayOfWeek.Sunday,    "Computer Engineering");

        // Appearance settings
        OpacitySlider.Value = s.WidgetOpacity;
        UpdateOpacityText(s.WidgetOpacity);
        PinBehindIconsCheckBox.IsChecked = s.PinBehindDesktopIcons;
    }

    // ── Mode selection ────────────────────────────────────────────────────────

    private void LocalCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _selectedMode           = "local";
        CloudSection.Visibility = Visibility.Collapsed;
        ApplyModeSelection("local");
    }

    private void CloudCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _selectedMode           = "cloud";
        CloudSection.Visibility = Visibility.Visible;
        ApplyModeSelection("cloud");
    }

    private void ApplyModeSelection(string mode)
    {
        var activeBorder   = (SolidColorBrush)FindResource("BrushPrimary");
        var inactiveBorder = (SolidColorBrush)FindResource("BrushBorderStrong");
        var activeBg       = new SolidColorBrush(
            Color.FromArgb(30, activeBorder.Color.R, activeBorder.Color.G, activeBorder.Color.B));
        var inactiveBg     = (SolidColorBrush)FindResource("BrushSurface");

        LocalCard.BorderBrush  = mode == "local"  ? activeBorder  : inactiveBorder;
        LocalCard.Background   = mode == "local"  ? activeBg      : inactiveBg;
        CloudCard.BorderBrush  = mode == "cloud"  ? activeBorder  : inactiveBorder;
        CloudCard.Background   = mode == "cloud"  ? activeBg      : inactiveBg;
    }

    // ── Advanced section toggle ───────────────────────────────────────────────

    private void AdvancedToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => SetAdvancedExpanded(!_advancedExpanded);

    private void SetAdvancedExpanded(bool expanded)
    {
        _advancedExpanded           = expanded;
        AdvancedFields.Visibility   = expanded ? Visibility.Visible  : Visibility.Collapsed;
        AdvancedChevron.Text        = expanded ? "▾" : "›";
    }

    // ── API key show / hide ───────────────────────────────────────────────────

    private void ShowHideApiKey_Click(object sender, RoutedEventArgs e)
    {
        _apiKeyVisible = !_apiKeyVisible;
        if (_apiKeyVisible)
        {
            AdvancedApiKeyPlain.Text        = AdvancedApiKeyBox.Password;
            AdvancedApiKeyBox.Visibility    = Visibility.Collapsed;
            AdvancedApiKeyPlain.Visibility  = Visibility.Visible;
            ShowHideApiKey.Content          = "Hide";
        }
        else
        {
            AdvancedApiKeyBox.Password      = AdvancedApiKeyPlain.Text;
            AdvancedApiKeyBox.Visibility    = Visibility.Visible;
            AdvancedApiKeyPlain.Visibility  = Visibility.Collapsed;
            ShowHideApiKey.Content          = "Show";
        }
    }

    // ── Clear advanced override ───────────────────────────────────────────────

    private void ClearAdvanced_Click(object sender, RoutedEventArgs e)
    {
        AdvancedBaseUrl.Text            = string.Empty;
        AdvancedModel.Text              = string.Empty;
        AdvancedApiKeyBox.Password      = string.Empty;
        AdvancedApiKeyPlain.Text        = string.Empty;
        ApiKeyValidationText.Visibility = Visibility.Collapsed;
        SetAdvancedExpanded(false);
        _logger.LogInformation("Advanced cloud override cleared.");
    }

    // ── Validation ────────────────────────────────────────────────────────────

    private bool Validate()
    {
        ValidationBar.Visibility        = Visibility.Collapsed;
        ApiKeyValidationText.Visibility = Visibility.Collapsed;

        // Advanced section: if the user has entered anything, validate it is complete
        if (_selectedMode == "cloud" && _advancedExpanded)
        {
            var key     = _apiKeyVisible ? AdvancedApiKeyPlain.Text : AdvancedApiKeyBox.Password;
            var baseUrl = AdvancedBaseUrl.Text.Trim();
            var model   = AdvancedModel.Text.Trim();

            var anyFilled = !string.IsNullOrWhiteSpace(key)
                         || !string.IsNullOrWhiteSpace(baseUrl)
                         || !string.IsNullOrWhiteSpace(model);

            if (anyFilled)
            {
                if (string.IsNullOrWhiteSpace(baseUrl))
                    return ShowValidationError("API endpoint is required when using your own key.");
                if (string.IsNullOrWhiteSpace(model))
                    return ShowValidationError("Model name is required when using your own key.");
                if (string.IsNullOrWhiteSpace(key) || key.Length < 10)
                    return ShowValidationError("Paste your API key (at least 10 characters).");
            }
        }

        foreach (var (box, day) in DayBoxes())
        {
            if (string.IsNullOrWhiteSpace(box.Text))
                return ShowValidationError($"Topic for {day} cannot be empty.");
        }

        return true;
    }

    private bool ShowValidationError(string message)
    {
        ApiKeyValidationText.Text       = message;
        ApiKeyValidationText.Visibility = Visibility.Visible;
        ValidationBar.Visibility        = Visibility.Visible;
        SaveBar.Visibility              = Visibility.Collapsed;
        return false;
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;

        try
        {
            var current = await _settingsStore.LoadAsync(CancellationToken.None);

            // Build AdvancedCloudProvider — null if the section is empty/collapsed
            ProviderSettings? advanced = null;
            if (_selectedMode == "cloud" && _advancedExpanded)
            {
                var key     = (_apiKeyVisible ? AdvancedApiKeyPlain.Text : AdvancedApiKeyBox.Password).Trim();
                var baseUrl = AdvancedBaseUrl.Text.Trim();
                var model   = AdvancedModel.Text.Trim();
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(baseUrl))
                    advanced = new ProviderSettings(baseUrl, model, key);
            }

            var updated = current with
            {
                Mode                   = _selectedMode,
                AdvancedCloudProvider  = advanced,
                Topics = new WeekdayTopicMap(new Dictionary<DayOfWeek, string>
                {
                    [DayOfWeek.Monday]    = TopicMon.Text.Trim(),
                    [DayOfWeek.Tuesday]   = TopicTue.Text.Trim(),
                    [DayOfWeek.Wednesday] = TopicWed.Text.Trim(),
                    [DayOfWeek.Thursday]  = TopicThu.Text.Trim(),
                    [DayOfWeek.Friday]    = TopicFri.Text.Trim(),
                    [DayOfWeek.Saturday]  = TopicSat.Text.Trim(),
                    [DayOfWeek.Sunday]    = TopicSun.Text.Trim(),
                }),
                WidgetOpacity          = OpacitySlider.Value,
                PinBehindDesktopIcons  = PinBehindIconsCheckBox.IsChecked ?? false
            };

            await _settingsStore.SaveAsync(updated, CancellationToken.None);

            _logger.LogInformation(
                "Settings saved. Mode={Mode}, Advanced={HasAdvanced}",
                _selectedMode, advanced != null);

            // Show save confirmation in the status bar
            SaveStatusText.Text      = "✓  Settings saved. Restart the app to apply AI mode changes.";
            SaveBar.Visibility       = Visibility.Visible;
            ValidationBar.Visibility = Visibility.Collapsed;

            await Task.Delay(2500);
            if (!_isClosed) Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings.");
            ShowValidationError("Failed to save settings. Please try again.");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // ── Opacity slider live update ─────────────────────────────────────────────

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Guard: this event fires during XAML initialization (when the Slider's default Value
        // is applied) before InitializeComponent fully populates named elements.
        // OpacityValueText may be null at that point — skip the update safely.
        if (OpacityValueText is null) return;

        UpdateOpacityText(e.NewValue);

        // Live preview: Owner is null until ShowDialog is called, so the
        // pattern-match is already null-safe — no explicit null check needed.
        if (Owner is WidgetWindow widget)
        {
            widget.SetBackgroundOpacity(Math.Clamp(e.NewValue, 0.4, 1.0));
        }
    }

    private void UpdateOpacityText(double value)
    {
        var percent = (int)(value * 100);
        OpacityValueText.Text = $"{percent}%";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IEnumerable<(System.Windows.Controls.TextBox Box, string Day)> DayBoxes() =>
    [
        (TopicMon, "Monday"),   (TopicTue, "Tuesday"), (TopicWed, "Wednesday"),
        (TopicThu, "Thursday"), (TopicFri, "Friday"),  (TopicSat, "Saturday"),
        (TopicSun, "Sunday"),
    ];
}
