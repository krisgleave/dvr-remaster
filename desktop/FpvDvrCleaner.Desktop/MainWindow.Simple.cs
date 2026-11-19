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
        RefreshOutputHeights();
        OutputLayoutChoice.SelectedIndex = _settings.Comparison ? 1 : 0;
        RemoveAudio.IsChecked = true;
        _syncingSimple = false;
        _simpleReady = true;
        // Migrate arbitrary legacy settings to one of the three visible presets.
        var level = CurrentLevel()?.Level ?? "normal";
        if (_settings.LastRepair is { } saved) ApplyRepair(saved); else ApplyLevel(Signal, level);
        RefreshAdvancedPresets();
        SetLook(false, (0, 0, 0, 0, 0));
        ShowSimpleExportSummary();
    }

    private void SyncSimpleRepair(RepairPreset? level)
    {
        if (!_simpleReady) return;
        _syncingSimple = true;
        SimpleSignalChoice.SelectedIndex = IsAnalog ? 0 : 1;
        RefreshAdvancedPresets();
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
        if (SimplePresetChoice.SelectedItem is RepairPreset preset) { ChoosePreset(preset); }
    }

    private void Saturation_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (SaturationValue != null) SaturationValue.Text = $"{(int)e.NewValue}%";
        if (!_simpleReady || _applyingPreset) return;
        RememberSettings();
        RequestAutoRender();
    }

    private void OsdProtection_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_simpleReady || PreserveOsd == null || ShowOsdMask == null) return;
        ShowOsdMask.IsEnabled = PreserveOsd.IsChecked == true && SimpleRemoveOsd.IsChecked != true;
        RequestAutoRender();
    }

    private void CloseExportResult_Click(object? sender, RoutedEventArgs e)
    {
        SimpleExportResult.IsVisible = false;
        ExportIdle.IsVisible = true;
        ExportStatus.Text = "";
        UpdateExportSummary();
    }

    private void RevealExport_Click(object? sender, RoutedEventArgs e)
    {
        if (_reviewOutput == null || !System.IO.File.Exists(_reviewOutput)) return;
        if (OperatingSystem.IsMacOS()) { if (_lastExportCount > 1) Process.Start("open", [System.IO.Path.GetDirectoryName(_reviewOutput)!]); else Process.Start("open", ["-R", _reviewOutput]); }
        else OpenOutput_Click(sender, e);
    }

    private void SimpleOsd_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_simpleReady || _syncingSimple) return;
        RemoveOsd.IsChecked = SimpleRemoveOsd.IsChecked;
        OsdProtection_Changed(sender, e);
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

    private static double DisplayAspect(FootageItem clip) => clip.Width * clip.Sar / Math.Max(1, clip.Height);
    private static int DisplayWidth(FootageItem clip, int height) => Math.Max(2, (int)Math.Round(height * DisplayAspect(clip) / 2) * 2);
    private int SelectedOutputHeight => OutputHeightChoice.SelectedItem is ComboBoxItem { Tag: int h } ? h : 0;

    private void RefreshOutputHeights()
    {
        var previous = _syncingSimple;
        _syncingSimple = true;
        OutputHeightChoice.Items.Clear();
        var clip = _selected;
        OutputHeightChoice.Items.Add(new ComboBoxItem { Content = clip == null ? "Original" : $"Original — {DisplayWidth(clip, clip.Height)} × {clip.Height}", Tag = 0 });
        if (clip != null)
            foreach (var (label, height) in new[] { ("HD", 1080), ("2K", 1440), ("4K", 2160) })
                OutputHeightChoice.Items.Add(new ComboBoxItem { Content = label, Tag = height });
        OutputHeightChoice.SelectedIndex = 0;
        _syncingSimple = previous;
        ShowSimpleExportSummary();
    }

    private void ShowSimpleExportSummary()
    {
        if (SimpleExportNote == null || OutputHeightChoice == null) return;
        var pending = ExportClips();
        if (pending.FirstOrDefault() is not { } clip) { SimpleExportNote.Text = "Select a recording to see output information."; if (ExportEstimate != null) ExportEstimate.Text = ""; return; }
        var height = SelectedOutputHeight == 0 ? clip.Height : Math.Max(clip.Height, SelectedOutputHeight);
        var comparison = OutputLayoutChoice.SelectedIndex == 1;
        var width = DisplayWidth(clip, height) * (comparison ? 2 : 1);
        SimpleExportNote.Text = pending.Count > 1 ? $"{pending.Count} files · {Clock(pending.Sum(c => c.Kept))} total" : $"{width} × {height} · {clip.Fps:0.##} fps · {Clock(clip.Kept)}";
        ShowExportEstimates();
    }

    // Estimates are deliberately broad until an export with matching settings has finished.
    private readonly Dictionary<string, (double SecondsPerSecond, double BytesPerSecond)> _exportSamples = new();

    private string EstimateKey(FootageItem clip)
    {
        var h = Math.Max(clip.Height, SelectedOutputHeight);
        return $"{clip.Width}:{clip.Height}:{DisplayWidth(clip, h)}:{h}:{clip.Fps}:{OutputFormat.Id}:{OutputLayoutChoice.SelectedIndex}:{RemoveAudio.IsChecked}:{clip.HasAudio}:{Mode}:{System.Text.Json.JsonSerializer.Serialize(CurrentRepair())}";
    }

    private (double Bytes, double Seconds, bool Measured) EstimateClip(FootageItem clip)
    {
        if (_exportSamples.TryGetValue(EstimateKey(clip), out var sample))
            return (sample.BytesPerSecond * clip.Kept, sample.SecondsPerSecond * clip.Kept, true);
        var h = Math.Max(clip.Height, SelectedOutputHeight);
        var comparison = OutputLayoutChoice.SelectedIndex == 1;
        var pixels = DisplayWidth(clip, h) * (double)h * (comparison ? 2 : 1);
        var scale = pixels * clip.Fps / (1920.0 * 1080 * 30);
        var sourceScale = clip.Width * (double)clip.Height * clip.Fps / (1920.0 * 1080 * 30);
        var (mbps, encode) = OutputFormat.Id switch
        {
            "mov-prores" => (147.0, 1.0),
            "mp4-hevc" => (7.0, 4.0),
            _ => (12.0, 1.5),
        };
        if (OutputFormat.Id != "mov-prores") mbps *= Math.Pow(2, (18 - (double)(QualityValue.Value ?? 18)) / 6);
        var audio = RemoveAudio.IsChecked != true && clip.HasAudio ? (OutputFormat.Id == "mov-prores" ? 1.6 : .128) : 0;
        var seconds = clip.Kept * Math.Max(.2, sourceScale * 3 + scale * encode);
        return ((mbps * scale + audio) * 125000 * clip.Kept, seconds, false);
    }

    private static string EstimateSize(double bytes) => bytes >= 1e9 ? $"{bytes / 1e9:0.00} GB" : $"{bytes / 1e6:0.#} MB";
    private static string EstimateTime(double seconds) => seconds < 60 ? $"{Math.Max(1, Math.Ceiling(seconds)):0}s" : $"{Math.Ceiling(seconds / 60):0} min";

    private void ShowExportEstimates()
    {
        if (ExportEstimate == null) return;
        var estimates = ExportClips().Select(EstimateClip).ToList();
        if (estimates.Count == 0) { ExportEstimate.Text = ""; return; }
        var bytes = estimates.Sum(x => x.Bytes);
        var seconds = estimates.Sum(x => x.Seconds);
        var measured = estimates.All(x => x.Measured);
        var low = measured ? .75 : .5;
        var high = measured ? 1.35 : 2.0;
        ExportEstimate.Text = $"Est. size: {EstimateSize(bytes * low)} – {EstimateSize(bytes * high)}  ·  " +
            $"Est. time: {EstimateTime(seconds * low)} – {EstimateTime(seconds * high)}" +
            (estimates.Count > 1 ? $" (all {estimates.Count} clips)" : "") +
            (measured ? " · Measured this session" : " · Approximate");
    }

    private void OpenLastVideo_Click(object? sender, RoutedEventArgs e)
    {
        if (_reviewOutput == null || !System.IO.File.Exists(_reviewOutput)) return;
        if (OperatingSystem.IsMacOS()) Process.Start("open", [_reviewOutput]);
        else Process.Start(new ProcessStartInfo(_reviewOutput) { UseShellExecute = true });
    }
}
