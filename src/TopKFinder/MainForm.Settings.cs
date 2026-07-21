using System;
using System.IO;
using System.Text.Json;

namespace TopKFinder;

partial class MainForm
{
    private sealed class GuiSettings
    {
        public string N { get; set; } = "25";
        public string M { get; set; } = "5";
        public string K { get; set; } = "5";
        public int ModeIndex { get; set; }
        public string Theme { get; set; } = nameof(ColorTheme.Dark);
        public bool PauseEachStage { get; set; }
    }

    private static string SettingsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sort", "settings.json");

    private ColorTheme ParseSelectedTheme()
    {
        return Enum.TryParse<ColorTheme>(_themeComboBox.SelectedItem?.ToString(), out var theme)
            ? theme
            : ColorTheme.Dark;
    }

    private void LoadSettings()
    {
        try
        {
            string path = SettingsFilePath;
            if (!File.Exists(path))
                return;

            GuiSettings? settings = JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(path));
            if (settings is null)
                return;

            _nTextBox.Text = settings.N;
            _mTextBox.Text = settings.M;
            _kTextBox.Text = settings.K;
            if (settings.ModeIndex >= 0 && settings.ModeIndex < _modeComboBox.Items.Count)
                _modeComboBox.SelectedIndex = settings.ModeIndex;
            if (_themeComboBox.Items.Contains(settings.Theme))
                _themeComboBox.SelectedItem = settings.Theme;
            _pauseEachStageCheckBox.Checked = settings.PauseEachStage;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Ignore unreadable/corrupt settings and fall back to defaults.
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new GuiSettings
            {
                N = _nTextBox.Text,
                M = _mTextBox.Text,
                K = _kTextBox.Text,
                ModeIndex = _modeComboBox.SelectedIndex,
                Theme = ParseSelectedTheme().ToString(),
                PauseEachStage = _pauseEachStageCheckBox.Checked,
            };

            string path = SettingsFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore failures to persist settings.
        }
    }
}
