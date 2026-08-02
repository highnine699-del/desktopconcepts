using DesktopConcepts.Domain;
using Microsoft.Extensions.Logging;
using System.Windows;
using System.Windows.Media;

namespace DesktopConcepts.UI.Views;

/// <summary>
/// Settings window — no JSON editing required.
///
/// Sections:
///   1. AI mode toggle  — Local / Cloud (two clickable cards)
///   2. Cloud section   — zero-setup info banner + collapsed Advanced override
///      Advanced:       Base URL, Model, API key (masked) — all optional
///   3. Weekday topics  — one TextBox per day
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
        var inactiveBorder = (SolidColorBrush)FindResource("BrushBorder");
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
        AdvancedChevron.Text        = expanded ? "▾" : "▸";
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
        ApiKeyValidationText.Visibility = Visibility.Collapsed;

        // Advanced section: if the user has entered anything, validate it is complete
        if (_selectedMode == "cloud" && _advancedExpanded)
        {
            var key     = _apiKeyVisible ? AdvancedApiKeyPlain.Text : AdvancedApiKeyBox.Password;
            var baseUrl = AdvancedBaseUrl.Text.Trim();
            var model   = AdvancedModel.Text.Trim();

            // If ANY advanced field is filled, require ALL three
            var anyFilled = !string.IsNullOrWhiteSpace(key)
                         || !string.IsNullOrWhiteSpace(baseUrl)
                         || !string.IsNullOrWhiteSpace(model);

            if (anyFilled)
            {
                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    ApiKeyValidationText.Text       = "API endpoint is required when using your own key.";
                    ApiKeyValidationText.Visibility = Visibility.Visible;
                    return false;
                }
                if (string.IsNullOrWhiteSpace(model))
                {
                    ApiKeyValidationText.Text       = "Model name is required when using your own key.";
                    ApiKeyValidationText.Visibility = Visibility.Visible;
                    return false;
                }
                if (string.IsNullOrWhiteSpace(key) || key.Length < 10)
                {
                    ApiKeyValidationText.Text       = "Paste your API key (at least 10 characters).";
                    ApiKeyValidationText.Visibility = Visibility.Visible;
                    return false;
                }
            }
        }

        // Topics — no blank days
        foreach (var (box, day) in DayBoxes())
        {
            if (string.IsNullOrWhiteSpace(box.Text))
            {
                ApiKeyValidationText.Text       = $"Topic for {day} cannot be empty.";
                ApiKeyValidationText.Visibility = Visibility.Visible;
                return false;
            }
        }

        return true;
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

            SaveStatusText.Text       = "✓ Settings saved. Restart the app to apply AI mode changes.";
            SaveStatusText.Visibility = Visibility.Visible;

            await Task.Delay(2500);
            Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings.");
            ApiKeyValidationText.Text       = "Failed to save settings. Please try again.";
            ApiKeyValidationText.Visibility = Visibility.Visible;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // ── Opacity slider live update ─────────────────────────────────────────────

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOpacityText(e.NewValue);
        // Apply live to widget window background brush if it's open
        if (Owner is WidgetWindow widget)
        {
            var clampedOpacity = Math.Clamp(e.NewValue, 0.4, 1.0);
            var alpha = (byte)(clampedOpacity * 255);

            if (widget.FindResource("BrushBackground") is SolidColorBrush backgroundBrush)
            {
                var currentColor = backgroundBrush.Color;
                backgroundBrush.Color = System.Windows.Media.Color.FromArgb(alpha, currentColor.R, currentColor.G, currentColor.B);
            }
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
