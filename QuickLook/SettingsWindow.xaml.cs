// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLook program.

using QuickLook.Common.Helpers;
using System;
using System.Windows;
using System.Windows.Media;

namespace QuickLook;

public partial class SettingsWindow : Window
{
    private static SettingsWindow _instance;

    private SettingsWindow()
    {
        InitializeComponent();

        if (OSThemeHelper.AppsUseDarkTheme())
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/QuickLook.Common;component/Styles/MainWindowStyles.Dark.xaml")
            });
        }

        Title = TranslationHelper.Get("Settings_Title", failsafe: "Settings");
        settingsHeader.Text = Title;
        diagnosticLoggingTitle.Text = TranslationHelper.Get(
            "Settings_DiagnosticLogging", failsafe: "Diagnostic logging");
        diagnosticLoggingDescription.Text = TranslationHelper.Get(
            "Settings_DiagnosticLoggingDescription",
            failsafe: "Write detailed performance and exception logs. Enable this only while diagnosing a problem.");
        logLocationText.Text = string.Format(
            TranslationHelper.Get("Settings_LogLocation", failsafe: "Existing log files are kept in: {0}"),
            SettingHelper.LocalDataPath);

        FontFamily = new FontFamily(TranslationHelper.Get("UI_FontFamily", failsafe: "Segoe UI"));
        diagnosticLoggingToggle.IsChecked = DiagnosticLogging.IsEnabled;
        diagnosticLoggingToggle.Checked += DiagnosticLoggingToggleChanged;
        diagnosticLoggingToggle.Unchecked += DiagnosticLoggingToggleChanged;
    }

    public static void ShowSettings()
    {
        if (_instance != null)
        {
            if (_instance.WindowState == WindowState.Minimized)
                _instance.WindowState = WindowState.Normal;
            _instance.Activate();
            return;
        }

        _instance = new SettingsWindow();
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
        _instance.Activate();
    }

    private void DiagnosticLoggingToggleChanged(object sender, RoutedEventArgs e) =>
        DiagnosticLogging.IsEnabled = diagnosticLoggingToggle.IsChecked == true;
}
