using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Diagnostics;

namespace FpvDvrCleaner.Desktop;

public partial class MainWindow
{
    private bool _simpleReady, _syncingSimple;
    private static readonly string[] SimpleLevels = ["light", "normal", "strong"];

    private void SetUpSimpleControls()
    {
        _syncingSimple = true;
        OutputHeightChoice.SelectedIndex = _settings.OutputHeight == 720 ? 0 : 1;
        OutputLayoutChoice.SelectedIndex = _settings.Comparison ? 1 : 0;
        RemoveAudio.IsChecked = _settings.RemoveAudio;
        _syncingSimple = false;
        _simpleReady = true;
        // Migrate arbitrary legacy settings to one of the three visible presets.
        var level = CurrentLevel()?.Level ?? "normal";
        ApplyLevel(Signal, level);
        SetLook(false, (0, 0, 0, 0, 0));
        ShowSimpleExportSummary();
    }

    private void SyncSimpleRepair(RepairPreset? level)
    {
        if (!_simpleReady) return;
        _syncingSimple = true;
        SimpleSignalChoice.SelectedIndex = IsAnalog ? 0 : 1;
        SimplePresetChoice.SelectedIndex = Array.IndexOf(SimpleLevels, level?.Level ?? "normal");
        SimplePresetNote.Text = level?.Description ?? "Choose a repair preset.";
        SimpleRemoveOsd.IsChecked = RemoveOsd.IsChecked;
        SaturationValue.Text = $"{(int)SaturationSlider.Value}%";
        _syncingSimple = false;
    }

    private void SimpleSignal_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_simpleReady || _syncingSimple) return;
        if (_selected != null) _signalSettled.Add(_selected.Path);
        ApplyLevel(SimpleSignalChoice.SelectedIndex == 1 ? "digital" : "analog",
            SimpleLevels[Math.Clamp(SimplePresetChoice.SelectedIndex, 0, 2)]);
    }

    private void SimplePreset_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_simpleReady || _syncingSimple) return;
        ApplyLevel(Signal, SimpleLevels[Math.Clamp(SimplePresetChoice.SelectedIndex, 0, 2)]);
    }

    private void Saturation_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (SaturationValue != null) SaturationValue.Text = $"{(int)e.NewValue}%";
        if (!_simpleReady || _applyingPreset) return;
        RememberSettings();
        RequestAutoRender();
    }

    private void SimpleOsd_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_simpleReady || _syncingSimple) return;
        RemoveOsd.IsChecked = SimpleRemoveOsd.IsChecked;
        OsdKeep.IsChecked = SimpleRemoveOsd.IsChecked != true;
        RememberSettings();
        RepairChanged();
    }

    private void SimpleExport_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_simpleReady || _syncingSimple) return;
        RememberSettings();
        ShowSimpleExportSummary();
    }

    private void RemoveAudio_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_simpleReady || _syncingSimple) return;
        RememberSettings();
        ShowSimpleExportSummary();
    }

    private void ShowSimpleExportSummary()
    {
        if (SimpleExportNote == null || OutputHeightChoice == null) return;
        var height = OutputHeightChoice.SelectedIndex == 0 ? 720 : 1080;
        SimpleExportNote.Text = OutputLayoutChoice.SelectedIndex == 1
            ? $"One labelled video: original on the left, cleaned on the right. Each view is {height} pixels high, keeping its original aspect ratio. {(RemoveAudio.IsChecked == true ? "Audio removed." : "Audio included when available.")}"
            : $"Cleaned video at {height} pixels high, keeping the original aspect ratio. No labels, crop or added bars. {(RemoveAudio.IsChecked == true ? "Audio removed." : "Audio included when available.")}";
    }

    private void OpenLastVideo_Click(object? sender, RoutedEventArgs e)
    {
        if (_reviewOutput == null || !System.IO.File.Exists(_reviewOutput)) return;
        if (OperatingSystem.IsMacOS()) Process.Start("open", [_reviewOutput]);
        else Process.Start(new ProcessStartInfo(_reviewOutput) { UseShellExecute = true });
    }
}
