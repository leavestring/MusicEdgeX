using System.Windows;
using System.Windows.Input;
using MusicEdge.Models;

namespace MusicEdge;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _draft;
    private bool _loading;

    public AppSettings? ResultSettings { get; private set; }

    public SettingsWindow(AppSettings settings)
    {
        _draft = settings.Clone();
        InitializeComponent();

        SliderHover.ValueChanged += (_, _) => UpdateValueLabels();
        SliderCollapse.ValueChanged += (_, _) => UpdateValueLabels();
        SliderHide.ValueChanged += (_, _) => UpdateValueLabels();

        LoadSettings();
    }

    private void LoadSettings()
    {
        _loading = true;

        RbLeft.IsChecked = _draft.EdgeSide == "Left";
        RbRight.IsChecked = _draft.EdgeSide != "Left";

        SliderHover.Value = _draft.HoverDelayMs;
        SliderCollapse.Value = _draft.CollapseDelayMs;
        SliderHide.Value = _draft.HideDelayMs;

        CbFullscreen.IsChecked = _draft.DisableInFullscreen;
        CbAutoStart.IsChecked = _draft.AutoStart;
        CbStartPinned.IsChecked = _draft.StartPinned;
        CbPlayerCap.IsChecked = _draft.UsePlayerCap;
        TxtCapHost.Text = _draft.PlayerCapHost;
        TxtCapPlayer.Text = _draft.PlayerCapPlayer;

        _loading = false;
        UpdateValueLabels();
    }

    private void UpdateValueLabels()
    {
        if (_loading) return;

        TxtHoverValue.Text = $"{SliderHover.Value:0} ms";
        TxtCollapseValue.Text = $"{SliderCollapse.Value:0} ms";
        TxtHideValue.Text = $"{SliderHide.Value:0} ms";
    }

    private void ApplyToDraft()
    {
        _draft.EdgeSide = RbLeft.IsChecked == true ? "Left" : "Right";
        _draft.HoverDelayMs = (int)Math.Round(SliderHover.Value);
        _draft.CollapseDelayMs = (int)Math.Round(SliderCollapse.Value);
        _draft.HideDelayMs = (int)Math.Round(SliderHide.Value);
        _draft.DisableInFullscreen = CbFullscreen.IsChecked == true;
        _draft.AutoStart = CbAutoStart.IsChecked == true;
        _draft.StartPinned = CbStartPinned.IsChecked == true;
        _draft.UsePlayerCap = CbPlayerCap.IsChecked == true;
        _draft.PlayerCapHost = string.IsNullOrWhiteSpace(TxtCapHost.Text)
            ? "127.0.0.1:8765"
            : TxtCapHost.Text.Trim();
        _draft.PlayerCapPlayer = TxtCapPlayer.Text.Trim();
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        ApplyToDraft();
        ResultSettings = _draft.Clone();
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings();

        _loading = true;
        RbRight.IsChecked = true;
        SliderHover.Value = defaults.HoverDelayMs;
        SliderCollapse.Value = defaults.CollapseDelayMs;
        SliderHide.Value = defaults.HideDelayMs;
        CbFullscreen.IsChecked = defaults.DisableInFullscreen;
        CbAutoStart.IsChecked = defaults.AutoStart;
        CbStartPinned.IsChecked = defaults.StartPinned;
        CbPlayerCap.IsChecked = defaults.UsePlayerCap;
        TxtCapHost.Text = defaults.PlayerCapHost;
        TxtCapPlayer.Text = defaults.PlayerCapPlayer;
        _loading = false;

        UpdateValueLabels();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
