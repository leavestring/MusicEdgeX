using System.Windows;
using MusicEdge.Services;

namespace MusicEdge;

/// <summary>
/// Settings dialog for configuring panel behavior.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly EdgeDetector _edgeDetector;

    public SettingsWindow(EdgeDetector edgeDetector)
    {
        InitializeComponent();
        _edgeDetector = edgeDetector;
        LoadSettings();
    }

    private void LoadSettings()
    {
        // Load from registry / config file
        // For now, use defaults from EdgeDetector
        SliderHoverDelay.Value = _edgeDetector.HoverDelayMs;
        SliderHideDelay.Value = _edgeDetector.HideDelayMs;
        SliderFullHideDelay.Value = _edgeDetector.FullHideDelayMs;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        // Apply settings
        _edgeDetector.HoverDelayMs = (int)SliderHoverDelay.Value;
        _edgeDetector.HideDelayMs = (int)SliderHideDelay.Value;
        _edgeDetector.FullHideDelayMs = (int)SliderFullHideDelay.Value;

        // Check if auto-start should be enabled
        if (CbAutoStart.IsChecked == true)
        {
            EnableAutoStart();
        }
        else
        {
            DisableAutoStart();
        }

        this.DialogResult = true;
        this.Close();
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        SliderHoverDelay.Value = 300;
        SliderHideDelay.Value = 500;
        SliderFullHideDelay.Value = 800;
        CbEnableAcrylic.IsChecked = true;
        CbFullscreenDisable.IsChecked = true;
        CbMinimizeToTray.IsChecked = true;
        RbRight.IsChecked = true;
    }

    private void EnableAutoStart()
    {
        try
        {
            var startupPath = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            var shortcutPath = System.IO.Path.Combine(startupPath, "MusicEdge.lnk");

            // Create a shortcut using WScript.Shell COM object
            if (!System.IO.File.Exists(shortcutPath))
            {
                var exePath = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exePath))
                {
                    // Simple file copy approach - for proper shortcut we'd need COM interop
                    // For now, just write a registry entry
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                    key?.SetValue("MusicEdge", exePath);
                }
            }
        }
        catch
        {
            // Non-critical
        }
    }

    private void DisableAutoStart()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue("MusicEdge", false);
        }
        catch
        {
            // Non-critical
        }
    }
}
