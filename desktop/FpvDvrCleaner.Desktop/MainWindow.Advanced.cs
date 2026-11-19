using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Input;
namespace FpvDvrCleaner.Desktop;
public partial class MainWindow
{
    private int _brightness = 100, _contrast = 100, _gamma = 100, _spatial;
    private void RefreshAdvancedPresets()
    {
        var old = _syncingSimple; _syncingSimple = true;
        var current = CurrentRepair();
        var choices = RepairPreset.BuiltIn.Where(p => p.Signal == Signal).Concat(_settings.Presets.Where(p => p.Signal == Signal)).ToList();
        var match = choices.FirstOrDefault(p => p.Matches(current));
        if (match == null) { current.Name = "Custom"; choices.Add(current); match = current; }
        SimplePresetChoice.ItemsSource = choices;
        SimplePresetChoice.DisplayMemberBinding = new Avalonia.Data.Binding("Name");
        SimplePresetChoice.SelectedItem = match;
        SyncInlineRepair();
        _syncingSimple = old;
    }
    private void SyncInlineRepair()
    {
        if (InlineContrastControl == null) return;
        var old = _syncingSimple; _syncingSimple = true;
        try {
            var p = CurrentRepair();
            InterferenceControl.Value = IsAnalog ? p.Colour : p.Block;
            SharpnessControl.Value = p.Detail;
            InlineSaturationControl.Value = p.Saturation;
            InlineContrastControl.Value = p.Contrast;
            UpdateInlineReadouts();
        } finally { _syncingSimple = old; }
    }
    private void UpdateInlineReadouts()
    {
        var controls = AdjustmentSliders();
        var fields = AdjustmentFields();
        var markers = new[] { Marker0, Marker1, Marker2, Marker3 };
        var defaults = AdjustmentDefaults();
        for (var i = 0; i < controls.Length; i++)
        {
            fields[i].Text = $"{controls[i].Value:0}";
            markers[i].Fraction = Math.Clamp((defaults[i] - controls[i].Minimum) / (controls[i].Maximum - controls[i].Minimum), 0, 1);
        }
    }
    private Slider[] AdjustmentSliders() => [InterferenceControl, SharpnessControl, InlineSaturationControl, InlineContrastControl];
    private TextBox[] AdjustmentFields() => [InterferenceReadout, SharpnessReadout, InlineSaturationReadout, InlineContrastReadout];
    private double[] AdjustmentDefaults()
    {
        var p = _basePreset is { } basis && basis.Signal == Signal ? basis : RepairPreset.LevelFor(Signal, "normal");
        return [IsAnalog ? p.Colour : p.Block, p.Detail, p.Saturation, p.Contrast];
    }
    private void CommitAdjustment(object? sender)
    {
        if (!_simpleReady || _syncingSimple || sender is not TextBox field || !int.TryParse(field.Tag?.ToString(), out var i)) return;
        var slider = AdjustmentSliders()[i];
        if (int.TryParse(field.Text, out var value)) slider.Value = Math.Clamp(value, slider.Minimum, slider.Maximum);
        field.Text = $"{slider.Value:0}";
    }
    private void AdjustmentValue_LostFocus(object? sender, RoutedEventArgs e) => CommitAdjustment(sender);
    private void AdjustmentValue_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitAdjustment(sender); e.Handled = true; }
    }
    private void ResetAdjustment_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && int.TryParse(button.Tag?.ToString(), out var i)) AdjustmentSliders()[i].Value = AdjustmentDefaults()[i];
    }
    private void ResetAdjustments_Click(object? sender, RoutedEventArgs e)
    {
        var sliders = AdjustmentSliders(); var defaults = AdjustmentDefaults();
        for (var i = 0; i < sliders.Length; i++) sliders[i].Value = defaults[i];
    }

    private void InlineRepair_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_simpleReady || _syncingSimple || _applyingPreset || InlineContrastControl == null) return;
        var p = CurrentRepair();
        if (IsAnalog) p.Colour = (int)InterferenceControl.Value; else p.Block = (int)InterferenceControl.Value;
        p.Detail = (int)SharpnessControl.Value; p.Saturation = (int)InlineSaturationControl.Value; p.Contrast = (int)InlineContrastControl.Value;
        ApplyRepair(p); RememberSettings(); ShowSimpleExportSummary();
    }
    private async void SaveAdvancedPreset_Click(object? sender, RoutedEventArgs e)
    {
        var name = await AskPresetName("Save preset", ""); if (string.IsNullOrWhiteSpace(name)) return;
        if (_settings.Presets.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) || RepairPreset.BuiltIn.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        var preset = CurrentRepair(); preset.Name = name; _settings.Presets.Add(preset); _basePreset = preset; RememberSettings(); RefreshAdvancedPresets();
    }
    private async Task<string?> AskPresetName(string title, string initial)
    {
        var w = new Window { Title = title, Width = 340, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var text = new TextBox { Text = initial, Watermark = "Preset name" }; string? result = null;
        var ok = new Button { Content = "Save", Width = 82, Height = 30, HorizontalContentAlignment = HorizontalAlignment.Center }; ok.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", Width = 82, Height = 30, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_,_) => { result = text.Text?.Trim(); w.Close(); }; cancel.Click += (_,_) => w.Close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 }; actions.Children.Add(cancel); actions.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 12 }; panel.Children.Add(new TextBlock { Text = "Preset name" }); panel.Children.Add(text); panel.Children.Add(actions); w.Content = panel; await w.ShowDialog(this); return result;
    }
    private async void ManageAdvancedPreset_Click(object? sender, RoutedEventArgs e)
    {
        var w = new Window { Title = "Custom presets", Width = 380, Height = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new DockPanel { Margin = new Thickness(14) };
        var close = new Button { Content = "Close", Width = 82, Height = 30, HorizontalAlignment = HorizontalAlignment.Right, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0,10,0,0) }; close.Classes.Add("accent"); close.Click += (_,_) => w.Close();
        DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        var panel = new StackPanel { Spacing = 6 };
        void Refresh()
        {
            panel.Children.Clear();
            if (_settings.Presets.Count == 0) panel.Children.Add(new TextBlock { Text = "No custom presets" });
            foreach (var preset in _settings.Presets.ToArray())
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
                row.Children.Add(new TextBlock { Text = preset.Name, TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                var remove = new Button { Content = "Delete", Height = 30 }; Grid.SetColumn(remove, 1); row.Children.Add(remove);
                var card = new Border { Child = row, Padding = new Thickness(10) }; card.Classes.Add("card"); panel.Children.Add(card);
                remove.Click += (_,_) => { _settings.Presets.Remove(preset); RememberSettings(); RefreshAdvancedPresets(); Refresh(); };
            }
        }
        Refresh(); root.Children.Add(new ScrollViewer { Content = panel }); w.Content = root; await w.ShowDialog(this);
    }
}
