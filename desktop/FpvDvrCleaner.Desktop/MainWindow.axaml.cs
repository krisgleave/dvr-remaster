using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace FpvDvrCleaner.Desktop;

public partial class MainWindow : Window
{
    private sealed class FootageItem(string path, double duration, bool hasAudio, int width, int height, double fps)
    {
        public string Path { get; } = path;
        public double Duration { get; } = duration;
        public bool HasAudio { get; } = hasAudio;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public double Fps { get; } = fps;
        public RepairPreset? QueuedRepair { get; set; }
        public bool? QueuedPreserveOsd { get; set; }
        public Guid QueueId { get; set; }
        public double Sar { get; set; } = 1;
        public string Codec { get; set; } = "";
        public string AudioCodec { get; set; } = "";
        public long Bitrate { get; set; }
        public long Size { get; set; }

        /// <summary>Hand corrections of the OSD mask for this recording (a PNG in the settings folder), or null.</summary>
        public string? ManualMask { get; set; }
        /// <summary>The picture inside black side bars; null until measured or when there are none.</summary>
        public (int X, int Y, int Width, int Height)? Box { get; set; }
        public bool BoxRequested { get; set; }
        public bool BoxMeasured { get; set; }
        /// <summary>The kept parts, sorted and non-overlapping; at least one.</summary>
        public List<(double In, double Out)> Segments { get; set; } = [(0, duration)];
        public List<double> Bookmarks { get; set; } = [];
        public double In => Segments.Count > 0 ? Segments[0].In : 0;
        public double Out => Segments.Count > 0 ? Segments[^1].Out : Duration;
        public double Kept => Segments.Sum(s => s.Out - s.In);
        public List<(double Seconds, Bitmap Picture)> Strip { get; } = [];
        public bool StripRequested { get; set; }
        public float[]? Waveform { get; set; }
        public bool WaveformRequested { get; set; }
        public Stack<(List<(double In, double Out)> Segments, List<double> Bookmarks)> Undo { get; } = new();
        public Stack<(List<(double In, double Out)> Segments, List<double> Bookmarks)> Redo { get; } = new();
        public override string ToString() => System.IO.Path.GetFileName(Path);

        public FootageItem Copy()
        {
            var copy = new FootageItem(Path, Duration, HasAudio, Width, Height, Fps) { Sar = Sar, Codec = Codec, AudioCodec = AudioCodec, Bitrate = Bitrate, Size = Size, Box = Box, BoxRequested = BoxRequested, BoxMeasured = BoxMeasured, Segments = Segments.ToList(), Bookmarks = Bookmarks.ToList(),
                QueuedRepair = QueuedRepair?.Clone(), QueuedPreserveOsd = QueuedPreserveOsd, QueueId = QueueId, Waveform = Waveform, WaveformRequested = WaveformRequested, StripRequested = StripRequested, ManualMask = ManualMask };
            copy.Strip.AddRange(Strip);
            return copy;
        }
    }

    private readonly List<FootageItem> _queue = []; // imported recordings
    private readonly List<FootageItem> _exportQueue = [];
    private List<FootageItem> ExportClips() => _exportQueue.Count > 0 ? _exportQueue.ToList() : _selected is { } clip ? [clip] : [];

    private readonly UserSettings _settings = UserSettings.Load();
    private FootageItem? _selected;
    private int _page;
    private double _head;
    private bool _pickingClip;
    private bool _applyingPreset;
    private bool _hoverPaused;
    private int _frameRequest;
    private readonly DispatcherTimer _frameDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly FramePlayer _player = new(FfmpegPath());
    private bool _playing;
    private string? _reviewSource;
    private string? _reviewOutput;
    private List<(double In, double Out)> _reviewSegments = [];
    private FootageItem? _reviewClip;
    private (int Width, int Height, double Fps) _reviewOutInfo;
    private readonly FramePlayer _reviewPair = new(FfmpegPath());
    private bool _reviewPlaying;
    private bool _reviewSeeking;
    private int _reviewSegmentPlaying = -1;
    private double _assessedHeadroom = -1, _assessedLoss, _assessedGain;
    private bool _secondPassWorth;
    private bool _loadingReview;
    private CancellationTokenSource? _trainingCancel;
    private CancellationTokenSource? _exportCancel;
    private readonly DispatcherTimer _exportClock = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _exportStarted;
    private double _exportDone;
    private static readonly string[] Extensions = [".mp4", ".mov", ".mkv", ".avi", ".m4v", ".ts", ".webm"];
    /// <summary>Output formats of the Export page: engine name, file extension, label and one-line hint.</summary>
    private static readonly (string Id, string Extension, string Title, string Hint)[] Formats =
    [
        ("mp4-h264", ".mp4", "MP4 / H.264", ""),
        ("mp4-hevc", ".mp4", "MP4 / H.265", ""),
        ("mov-prores", ".mov", "MOV / ProRes 422", ""),
    ];
    private static readonly string[] Titles = ["Import", "Trim", "Repair", "Export", "Model training"];
    private static readonly string[] Subtitles =
    [
        "Bring in your DVR recordings.",
        "Keep only the part of each clip you want.",
        "Choose the repairs and check them on a real frame.",
        "Render every clip in the queue.",
        "Optional: learn from your own footage.",
    ];

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, PlaybackShortcut, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyUpEvent, PlaybackShortcutReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        _frameDelay.Tick += async (_, _) => { _frameDelay.Stop(); await RefreshFrameAsync(); };
        _player.FrameShown += OnPlaybackFrame;
        _player.PairShown += OnWorkspacePair;
        _player.Failed += message => { _playing = false; UpdatePlayIcon(false); PreviewStatus.Text = "Preview failed: " + message; CompareAfter.Source = null; CompareDivider.IsVisible = false; UpdateCompare(); };
        _player.Ended += () => { if (_playing) ContinueOrStop(); };
        _reviewPair.PairShown += OnReviewPair;
        _reviewPair.Ended += () => StopReviewPlayback();
        _exportClock.Tick += (_, _) => ShowExportTime();
        HookTimeline();
        DragDrop.AddDragOverHandler(TopImport, (_, e) => e.DragEffects = DragDropEffects.Copy);
        DragDrop.AddDropHandler(TopImport, async (_, e) =>
        {
            var files = e.DataTransfer.TryGetFiles();
            if (files == null) return;
            var paths = new List<string>();
            foreach (var item in files)
            {
                var path = item.TryGetLocalPath();
                if (path == null) continue;
                if (Directory.Exists(path)) paths.AddRange(VideosIn(path));
                else if (Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant())) paths.Add(path);
            }
            await AddFilesAsync(paths);
        });
        Opened += async (_, _) =>
        {
            RestoreSettings();
            SetUpRepairPage();
            SetUpSimpleControls();
            RefreshModelChoices(); RefreshResumeChoices(); ShowPage(0); UpdateCompare();
            // Files passed on the command line ("Open with", dropped on the exe) join the queue.
            var given = Environment.GetCommandLineArgs().Skip(1).Where(File.Exists).ToList();
            if (given.Count > 0) await AddFilesAsync(given);
        };
        Deactivated += (_, _) => _spaceHeld = false;
        Closing += (_, _) => { _previewCancel?.Cancel(); _player.Dispose(); DeleteLiveTicket(); _reviewPair.Dispose(); RememberSettings(); };
    }

    // ---------------------------------------------------------------- busy overlay

    private Action? _busyCancel;

    /// <summary>Only the progress card stays sharp; the page behind it is dimmed and blurred.</summary>
    private void ShowBusy(string title, string text = "", Action? cancel = null)
    {
        _busyCancel = cancel;
        BusyTitle.Text = title;
        BusyText.Text = text;
        BusyTime.Text = "";
        BusyBar.IsIndeterminate = true;
        BusyCancel.IsVisible = cancel != null;
        BusyCancel.IsEnabled = true;
        BusyOverlay.IsVisible = true;
        PageScroller.Effect = new BlurEffect { Radius = 10 };
    }

    private void UpdateBusy(double? fraction, string? text = null, string? time = null)
    {
        if (!BusyOverlay.IsVisible) return;
        if (fraction is { } value) { BusyBar.IsIndeterminate = false; BusyBar.Value = Math.Clamp(value * 100, 0, 100); }
        if (text != null) BusyText.Text = text;
        if (time != null) BusyTime.Text = time;
    }

    private void HideBusy()
    {
        BusyOverlay.IsVisible = false;
        PageScroller.Effect = null;
        _busyCancel = null;
    }

    private void BusyCancel_Click(object? sender, RoutedEventArgs e)
    {
        BusyCancel.IsEnabled = false;
        BusyText.Text = "Canceling…";
        _busyCancel?.Invoke();
    }

    // ---------------------------------------------------------------- settings

    /// <summary>Folders chosen last time come back, so nobody has to type them again.</summary>
    private void RestoreSettings()
    {
        if (Directory.Exists(_settings.OutputFolder)) _outputFolder = _settings.OutputFolder;
        ShowOutputFolder();
        if (Directory.Exists(_settings.TrainFolder)) TrainFolder.Text = _settings.TrainFolder;
        SuffixBox.Text = string.IsNullOrWhiteSpace(_settings.OutputSuffix) ? "-cleaned" : _settings.OutputSuffix;
        FillFormatChoices(_settings.OutputFormat);
        JoinClips.IsChecked = _settings.JoinClips;
        OpenAfterExport.IsChecked = _settings.OpenFolderAfterExport;
        SnapToggle.IsChecked = false;
        Timeline.Snap = _settings.Snap;
        if (_settings.LastRepair != null) ApplyRepair(_settings.LastRepair);
        else ShowLook();
        ReviewSideBySide.IsChecked = true; ReviewWipeMode.IsChecked = false;
        ReviewMode_Changed(null, new RoutedEventArgs());
        TrainFolder.LostFocus += (_, _) => RememberSettings();
    }

    private void RememberSettings()
    {
        // The default folder next to the first clip is not remembered: it follows the clips.
        _settings.OutputFolder = _outputFolder;
        _settings.TrainFolder = TrainFolder.Text?.Trim();
        _settings.OutputSuffix = SuffixBox.Text?.Trim() ?? "";
        _settings.OutputFormat = OutputFormat.Id;
        if (_simpleReady)
        {
            _settings.OutputHeight = SelectedOutputHeight;
            _settings.Comparison = OutputLayoutChoice.SelectedIndex == 1;
            _settings.RemoveAudio = RemoveAudio.IsChecked == true;
        }
        _settings.JoinClips = JoinClips.IsChecked == true;
        _settings.OpenFolderAfterExport = OpenAfterExport.IsChecked == true;
        _settings.Snap = SnapToggle.IsChecked == true;
        _settings.LastRepair = CurrentRepair();
        _settings.LastPreset = _basePreset is { } basis ? PresetKey(basis) : null;
        foreach (var clip in _queue) _settings.RememberCut(clip.Path, clip.Segments, clip.Bookmarks, clip.ManualMask);
        _settings.Save();
    }

    private void ExportOption_Changed(object? sender, RoutedEventArgs e)
    {
        if (SuffixBox == null || JoinClips == null) return;
        RememberSettings();
    }

    // ---- output file format

    private bool _fillingFormats;

    /// <summary>One line per format: the name, then a short hint in grey.</summary>
    private void FillFormatChoices(string chosen)
    {
        _fillingFormats = true;
        FormatChoice.Items.Clear();
        foreach (var format in Formats)
        {
            FormatChoice.Items.Add(new ComboBoxItem { Content = format.Title, Tag = format.Id, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left });
        }
        // An unknown or missing name (older settings) falls back to MP4 · H.264.
        FormatChoice.SelectedIndex = Math.Max(0, Array.FindIndex(Formats, format => format.Id == chosen));
        _fillingFormats = false;
        ShowFormatNote();
    }

    private (string Id, string Extension, string Title, string Hint) OutputFormat =>
        FormatChoice?.SelectedItem is ComboBoxItem { Tag: string id } && Formats.Any(format => format.Id == id)
            ? Formats.First(format => format.Id == id) : Formats[0];

    private bool FormatUsesQuality => OutputFormat.Id != "mov-prores";

    private void Format_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_fillingFormats || FormatNote == null) return;
        ShowFormatNote();
        RememberSettings();
        ShowSimpleExportSummary();
    }

    /// <summary>A purpose card picks the format; the full list stays under More options.</summary>
    private void Purpose_Changed(object? sender, RoutedEventArgs e)
    {
        if (_fillingFormats || sender is not RadioButton { IsChecked: true, Tag: string id }) return;
        var index = Array.FindIndex(Formats, format => format.Id == id);
        if (index >= 0 && FormatChoice.SelectedIndex != index) FormatChoice.SelectedIndex = index;
    }

    private RadioButton[] PurposeButtons => [PurposeShare, PurposeSmall, PurposeEdit, PurposeWeb];

    private void ShowFormatNote()
    {
        // ProRes stores a fixed data rate: quality and size limit have nothing to set.
        QualityValue.IsEnabled = SizeCap.IsEnabled = FormatUsesQuality;
        _fillingFormats = true;
        foreach (var card in PurposeButtons) card.IsChecked = card.Tag as string == OutputFormat.Id;
        _fillingFormats = false;
        if (PurposeButtons.All(card => card.IsChecked != true))
        {
            FormatNote.Text = string.Format(CultureInfo.CurrentCulture, UiText.PurposeOther, OutputFormat.Title) + " " + OutputFormat.Hint + ".";
            return;
        }
        FormatNote.Text = OutputFormat.Id switch
        {
            "mp4-hevc" => "About half the size of H.264 at the same quality. Older computers, TVs and some editors cannot open it.",
            "mov-prores" => "Keeps every detail for editing, so quality and size limit do not apply. Sound is stored uncompressed.",
            "mkv-h264" => "The same picture as MP4 · H.264. A file that was cut off still plays up to the break.",
            "webm-vp9" => "VP9 picture with Opus sound for websites. Encoding takes noticeably longer than H.264.",
            _ => "H.264 picture with AAC sound: the safe choice for phones, sharing and every editor.",
        };
    }

    private static void OpenInFileManager(string folder)
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS()) Process.Start("open", [folder]);
        else Process.Start("xdg-open", [folder]);
    }

    // ---------------------------------------------------------------- engine

    private static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(System.IO.Path.Combine(current.FullName, "pyproject.toml")) &&
                File.Exists(System.IO.Path.Combine(current.FullName, "src", "flugbild", "bridge.py")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Python video engine was not found next to this desktop build.");
    }

    private static string ModelFolder()
    {
        var folder = System.IO.Path.Combine(UserSettings.Folder(), "models");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string FfmpegPath()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var bundled = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", name);
        return File.Exists(bundled) ? bundled : "ffmpeg";
    }

    /// <summary>The engine shipped inside a release build, if present.</summary>
    private static string? BundledEngine()
    {
        var name = OperatingSystem.IsWindows() ? "flugbild-engine.exe" : "flugbild-engine";
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "engine", name);
        return File.Exists(path) ? path : null;
    }

    private static ProcessStartInfo EngineStart(string ticket)
    {
        ProcessStartInfo start;
        if (BundledEngine() is { } engine)
        {
            // Release build: frozen engine plus FFmpeg next to the app, no Python needed.
            start = new ProcessStartInfo(engine) { WorkingDirectory = AppContext.BaseDirectory };
            var tools = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            if (Directory.Exists(tools))
                start.Environment["PATH"] = tools + System.IO.Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        }
        else
        {
            var root = RepoRoot();
            start = new ProcessStartInfo("python") { WorkingDirectory = root };
            start.Environment["PYTHONPATH"] = System.IO.Path.Combine(root, "src");
            start.ArgumentList.Add("-m"); start.ArgumentList.Add("flugbild.bridge");
        }
        start.ArgumentList.Add(ticket);
        start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.StandardOutputEncoding = System.Text.Encoding.UTF8; start.StandardErrorEncoding = System.Text.Encoding.UTF8;
        // Without this, Windows Python writes the ANSI code page and umlauts in
        // file names or FFmpeg messages arrive as garbled characters.
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        return start;
    }

    private static async Task<JsonElement> EngineAsync(object request, Action<JsonElement>? progress = null,
        CancellationToken cancellation = default)
    {
        var ticket = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fpv-task-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(ticket, JsonSerializer.Serialize(request), cancellation);
        try
        {
            var start = EngineStart(ticket);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start video engine");
            using var killOnCancel = cancellation.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            });
            var errorTask = process.StandardError.ReadToEndAsync();
            JsonElement result = default;
            string? fault = null;
            while (await process.StandardOutput.ReadLineAsync(cancellation) is { } line)
            {
                using var record = JsonDocument.Parse(line);
                var item = record.RootElement.Clone();
                switch (item.GetProperty("kind").GetString())
                {
                    case "progress": progress?.Invoke(item); break;
                    case "result": result = item; break;
                    case "error": fault = item.GetProperty("message").GetString(); break;
                }
            }
            await process.WaitForExitAsync(cancellation);
            if (process.ExitCode != 0 || fault != null)
                throw new InvalidOperationException(fault ?? await errorTask);
            return result;
        }
        finally { File.Delete(ticket); }
    }

    private static Bitmap LoadBitmap(string path)
    {
        using var stream = File.OpenRead(path);
        return new Bitmap(stream);
    }

    /// <summary>A small vector icon for a button: text glyphs such as ▲ or × sit off-centre in most fonts.</summary>
    private static Avalonia.Controls.Shapes.Path VectorIcon(string data, string colour, bool filled)
    {
        var brush = new SolidColorBrush(Color.Parse(colour));
        return new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(data), Width = 10, Height = 10, Stretch = Stretch.None,
            Fill = filled ? brush : null, Stroke = filled ? null : brush, StrokeThickness = 1.8, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

    private static string TempFile(string stem, string extension = ".png") =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fpv-{stem}-{Guid.NewGuid():N}{extension}");

    private static string Clock(double seconds)
    {
        seconds = Math.Max(0, seconds);
        return string.Create(CultureInfo.InvariantCulture, $"{(int)(seconds / 60):00}:{seconds % 60:00.00}");
    }

    private static string Duration(TimeSpan span) => $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";

    private static bool TryParseClock(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3) return false;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0) return false;
            seconds = seconds * 60 + value;
        }
        return true;
    }

    // ---------------------------------------------------------------- steps

    private void ShowPage(int index)
    {
        if (index <= 3) index = 1;
        if (index != 1) StopPlay();
        if (index != 3) StopReviewPlayback();
        _page = index;
        Focus();
        var pages = new Control[] { ImportPage, TrimPage, CleanPage, ExportPage, TrainPage };
        var steps = new[] { ImportStep, TrimStep, CleanStep, ExportStep, TrainStep };
        for (var i = 0; i < pages.Length; i++) { pages[i].IsVisible = i == index; steps[i].Classes.Set("current", i == (index == 1 ? 0 : index)); }
        Workspace.IsVisible = index == 1;
        ExportPage.IsVisible = index == 1;
        ImportPage.IsVisible = index == 1;
        TrimPage.IsVisible = index == 1;
        PageTitle.Text = index == 1 ? "Workspace" : Titles[index];
        PageSubtitle.Text = index == 1 ? "Import, trim and restore your recordings." : Subtitles[index];
        BackButton.IsVisible = index == 3;
        NextButton.IsVisible = false;
        NextButton.Content = index switch { 0 => "Next: Trim →", 1 => "Next: Repair →", _ => "Next: Export →" };
        FooterNote.Text = index switch
        {
            0 => _queue.Count == 0 ? "Add at least one recording to continue." : "Select a clip, then trim it.",
            1 => "Every clip keeps its own start and end.",
            2 => "Settings apply to every clip in the queue.",
            _ => "",
        };
        // Trim, Repair and Export fill the window exactly; their own parts scroll if they must.
        PageScroller.VerticalScrollBarVisibility = index is >= 1 and <= 3
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        FitTrimPage();
        if (index == 1 && _selected != null) { LayoutStrip(); RequestFrame(); }
        if (index == 1) { ShowPresetState(); RequestAutoRender(); }
        else { _autoRender.Stop(); _previewCancel?.Cancel(); }
        if (index == 1) { ShowOutputFolder(); UpdateExportSummary(); ShowSimpleExportSummary(); }
        PageScroller.Offset = new Vector(0, 0);
    }
    private void ImportStep_Click(object? sender, RoutedEventArgs e) => ShowPage(0);
    private void TrimStep_Click(object? sender, RoutedEventArgs e) => ShowPage(1);
    private void CleanStep_Click(object? sender, RoutedEventArgs e) => ShowPage(2);
    private void ExportStep_Click(object? sender, RoutedEventArgs e) => ShowPage(3);
    private void TrainStep_Click(object? sender, RoutedEventArgs e) => ShowPage(4);
    private void Back_Step_Click(object? sender, RoutedEventArgs e) => ShowPage(Math.Max(0, _page - 1));
    private void Next_Step_Click(object? sender, RoutedEventArgs e)
    {
        if (_page == 0 && _queue.Count == 0) { ImportStatus.Text = "Add at least one recording first."; return; }
        ShowPage(Math.Min(3, _page + 1));
    }

    // ---------------------------------------------------------------- import

    private static IEnumerable<string> VideosIn(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(file => Extensions.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant()));

    private async Task<string?> PickFolderAsync(string? startIn = null)
    {
        var options = new FolderPickerOpenOptions { AllowMultiple = false };
        if (Directory.Exists(startIn))
            options.SuggestedStartLocation = await (_exportDialog?.StorageProvider ?? StorageProvider).TryGetFolderFromPathAsync(startIn);
        var folders = await (_exportDialog?.StorageProvider ?? StorageProvider).OpenFolderPickerAsync(options);
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private async void Add_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = "Add recordings" });
        await AddFilesAsync(files.Select(file => file.TryGetLocalPath()).OfType<string>());
    }

    private async void AddFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder != null) await AddFilesAsync(VideosIn(folder));
    }

    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var added = 0;
        var list = paths.ToList();
        if (list.Count > 0) ShowBusy("Reading recordings", "");
        try
        {
        foreach (var path in list)
        {
            if (_queue.Any(clip => clip.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            ImportStatus.Text = $"Reading {System.IO.Path.GetFileName(path)}…";
            UpdateBusy(null, ImportStatus.Text);
            try
            {
                var info = await EngineAsync(new { action = "inspect", source = path });
                var clip = new FootageItem(path, info.GetProperty("duration").GetDouble(),
                    info.TryGetProperty("audio", out var audio) && audio.GetBoolean(),
                    info.GetProperty("width").GetInt32(), info.GetProperty("height").GetInt32(),
                    info.TryGetProperty("fps", out var fps) ? fps.GetDouble() : 0);
                if (info.TryGetProperty("sar", out var sar)) clip.Sar = sar.GetDouble();
                if (info.TryGetProperty("codec", out var codec)) clip.Codec = codec.GetString() ?? "";
                if (info.TryGetProperty("audio_codec", out var ac)) clip.AudioCodec = ac.GetString() ?? "";
                if (info.TryGetProperty("bitrate", out var br)) clip.Bitrate = br.GetInt64();
                if (info.TryGetProperty("size", out var size)) clip.Size = size.GetInt64();
                if (_settings.RememberedCut(path) is { } memory)
                {
                    clip.ManualMask = UserSettings.RememberedMask(memory);
                }
                _queue.Add(clip);
                _ = MeasureBoxAsync(clip);
                added++;
            }
            catch (Exception ex) { ImportStatus.Text = $"Skipped {System.IO.Path.GetFileName(path)}: {ex.Message}"; }
        }
        }
        finally { HideBusy(); }
        if (added > 0) ImportStatus.Text = $"Added {added} clip{(added == 1 ? "" : "s")}.";
        DrawQueue();
        ShowOutputFolder();
        if (_selected == null && _queue.Count > 0) Select(_queue[0]);
        if (_page == 1) FitTrimPage();
    }

    private void DrawQueue()
    {
        QueueList.Children.Clear();
        QueueEmpty.IsVisible = _queue.Count == 0;
        CountLabel.Text = $"{_queue.Count} clip{(_queue.Count == 1 ? "" : "s")}";
        ImportHint.Text = _queue.Count == 0 ? "Add recordings" : CountLabel.Text;
        foreach (var clip in _queue)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 1),
                Background = new SolidColorBrush(Color.Parse(clip == _selected ? "#253B50" : "#2C2C2F")) };
            var choose = new Button
            {
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = System.IO.Path.GetFileName(clip.Path), FontWeight = clip == _selected ? FontWeight.SemiBold : FontWeight.Normal,
                            Foreground = new SolidColorBrush(Color.Parse("#F2EEE8")), TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = QueueSummary(clip),
                            FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#B4B0AA")) },
                    },
                },
            };
            choose.Classes.Add("row");
            choose.Click += (_, _) => Select(clip);
            var index = _queue.IndexOf(clip);
            // Square icon buttons as tall as Copy and Trim; a disabled one keeps its background and fades its icon.
            Button Square(string data, string colour, bool filled, string tip)
            {
                var button = new Button { Content = VectorIcon(data, colour, filled), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                button.Classes.Add("square");
                ToolTip.SetTip(button, tip);
                return button;
            }
            var up = Square("M1,7 L5,3 L9,7", "#F2EEE8", false, "Move up");
            up.IsEnabled = index > 0;
            up.Margin = new Thickness(6, 0, 0, 0);
            up.Click += (_, _) => { _queue.RemoveAt(index); _queue.Insert(index - 1, clip); DrawQueue(); };
            var down = Square("M1,3 L5,7 L9,3", "#F2EEE8", false, "Move down");
            down.IsEnabled = index < _queue.Count - 1;
            down.Margin = new Thickness(4, 0, 0, 0);
            down.Click += (_, _) => { _queue.RemoveAt(index); _queue.Insert(index + 1, clip); DrawQueue(); };
            var copy = new Button { Content = "Copy", Height = 30, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center };
            ToolTip.SetTip(copy, "Add the same recording once more, with its own cut");
            copy.Click += (_, _) => { _queue.Insert(index + 1, clip.Copy()); DrawQueue(); };
            var trim = new Button { Content = "Trim", Height = 30, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center };
            trim.Click += (_, _) => { Select(clip); ShowPage(1); };
            var remove = Square("M1.5,1.5 L8.5,8.5 M8.5,1.5 L1.5,8.5", "#9C3F1F", false, "Remove from queue");
            remove.Margin = new Thickness(4, 0, 0, 0);
            remove.Classes.Add("remove");
            remove.Click += (_, _) =>
            {
                _queue.Remove(clip);
                if (_selected == clip) { _selected = null; if (_queue.Count > 0) Select(_queue[0]); else ClearSelection(); }
                DrawQueue();
            };
            Grid.SetColumn(up, 1); Grid.SetColumn(down, 2); Grid.SetColumn(copy, 3); Grid.SetColumn(trim, 4); Grid.SetColumn(remove, 5);
            row.Children.Add(choose); Grid.SetColumn(remove, 1); row.Children.Add(remove);
            var menu = new ContextMenu();
            var moveUp = new MenuItem { Header = "Move up", IsEnabled = up.IsEnabled }; moveUp.Click += (_, _) => { _queue.RemoveAt(index); _queue.Insert(index - 1, clip); DrawQueue(); };
            var moveDown = new MenuItem { Header = "Move down", IsEnabled = down.IsEnabled }; moveDown.Click += (_, _) => { _queue.RemoveAt(index); _queue.Insert(index + 1, clip); DrawQueue(); };
            var duplicate = new MenuItem { Header = "Duplicate" }; duplicate.Click += (_, _) => { _queue.Insert(index + 1, clip.Copy()); DrawQueue(); };
            menu.Items.Add(moveUp); menu.Items.Add(moveDown); menu.Items.Add(duplicate); choose.ContextMenu = menu;
            QueueList.Children.Add(row);
        }
        _pickingClip = true;
        ClipPicker.ItemsSource = _queue.ToList();
        ClipPicker.SelectedItem = _selected;
        _pickingClip = false;
        UpdateExportSummary();
    }

    private void AddSection_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } clip || _exportCancel != null) return;
        var item = clip.Copy();
        item.QueueId = Guid.NewGuid();
        item.QueuedRepair = CurrentRepair();
        item.QueuedRepair.Name = _basePreset?.Name ?? "Custom";
        item.QueuedPreserveOsd = PreserveOsd.IsChecked == true;
        _exportQueue.Add(item);
        UpdateExportSummary();
        ExportStatus.Text = $"Added section to queue ({_exportQueue.Count}).";
    }

    private async void ManageQueue_Click(object? sender, RoutedEventArgs e)
    {
        if (_exportCancel != null) return;
        var dialog = new Window { Title = "Export queue", Width = 520, Height = 320, MinHeight = 220, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new DockPanel { Margin = new Thickness(14) };
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0,12,0,0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        var clear = new Button { Content = "Remove all", Height = 30 }; var close = new Button { Content = "Close", Height = 30, Width = 82, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        close.Classes.Add("accent"); Grid.SetColumn(close, 2);
        footer.Children.Add(clear); footer.Children.Add(close); root.Children.Add(footer);
        var summary = new TextBlock { Margin = new Thickness(0,0,0,10), Foreground = Brush.Parse("#AEB6BC") };
        DockPanel.SetDock(summary, Dock.Top); root.Children.Add(summary);
        var list = new StackPanel { Spacing = 6 }; root.Children.Add(new ScrollViewer { Content = list });
        void Refresh()
        {
            list.Children.Clear(); clear.IsEnabled = _exportQueue.Count > 0;
            summary.Text = $"{_exportQueue.Count} sections · {Clock(_exportQueue.Sum(c => c.Kept))} total";
            if (_exportQueue.Count == 0) list.Children.Add(new TextBlock { Text = "Queue is empty" });
            foreach (var item in _exportQueue.ToList())
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
                var info = new StackPanel { Spacing = 3 };
                info.Children.Add(new TextBlock { Text = $"{_exportQueue.IndexOf(item)+1:00}  {item}", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                info.Children.Add(new TextBlock { Text = $"{Clock(item.In)} – {Clock(item.Out)} · {Clock(item.Kept)}", Foreground = Brush.Parse("#AEB6BC"), FontSize = 11 });
                if (item.QueuedRepair is { } repair)
                {
                    info.Children.Add(new TextBlock { Text = repair.Name, Foreground = Brush.Parse("#0A84FF") });
                    info.Children.Add(new TextBlock { Text = $"Repair {(repair.Signal == "analog" ? repair.Colour : repair.Block)}% · Sharpness {repair.Detail}% · Saturation {repair.Saturation}% · Contrast {repair.Contrast}%", FontSize = 11, TextWrapping = TextWrapping.Wrap });
                    info.Children.Add(new TextBlock { Text = item.QueuedPreserveOsd == true ? "Preserve OSD" : "OSD protection off", FontSize = 11 });
                }
                row.Children.Add(info);
                Button QueueAction(string data, string colour, string label)
                {
                    var button = new Button { Content = VectorIcon(data, colour, false), Width = 30, Height = 30,
                        Padding = new Thickness(0), HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center };
                    ToolTip.SetTip(button, label);
                    Avalonia.Automation.AutomationProperties.SetName(button, label);
                    return button;
                }
                var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                var edit = QueueAction("M1,9 L1.8,6.2 L7.5,0.5 L9.5,2.5 L3.8,8.2 Z M6,2 L8,4", "#AEB6BC", "Edit queued restoration");
                var remove = QueueAction("M1,2.5 L9,2.5 M3,2.5 L3,0.5 L7,0.5 L7,2.5 M2,3 L2.6,9.5 L7.4,9.5 L8,3 M4,4.5 L4,7.5 M6,4.5 L6,7.5", "#FF453A", "Remove from queue");
                edit.Click += async (_, _) => { await EditQueuedRepair(item, dialog); Refresh(); };
                actions.Children.Add(edit); actions.Children.Add(remove);
                Grid.SetColumn(actions, 1); row.Children.Add(actions);
                remove.Click += (_, _) => { _exportQueue.Remove(item); Refresh(); };
                var card = new Border { Child = row, Padding = new Thickness(10) }; card.Classes.Add("card"); list.Children.Add(card);
            }
            UpdateExportSummary();
        }
        clear.Click += (_, _) => { _exportQueue.Clear(); Refresh(); }; close.Click += (_, _) => dialog.Close();
        Refresh(); dialog.Content = root; await dialog.ShowDialog(this);
    }

    private async Task EditQueuedRepair(FootageItem item, Window owner)
    {
        if (item.QueuedRepair is not { } original) return;
        var repair = original.Clone();
        var dialog = new Window { Title = "Edit queued restoration", Width = 400, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        var preset = new ComboBox { ItemsSource = RepairPreset.BuiltIn.Where(p => p.Signal == repair.Signal).Concat(_settings.Presets.Where(p => p.Signal == repair.Signal)).ToList(), DisplayMemberBinding = new Avalonia.Data.Binding("Name"), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, PlaceholderText = repair.Name };
        panel.Children.Add(preset);
        var labels = new[] { "Repair strength", "Sharpness", "Saturation", "Contrast" };
        var fields = new NumericUpDown[4];
        for (var i = 0; i < fields.Length; i++)
        {
            panel.Children.Add(new TextBlock { Text = labels[i] });
            fields[i] = new NumericUpDown { Minimum = i == 3 ? 50 : 0, Maximum = i == 3 ? 150 : i < 2 ? 100 : 200, Increment = 1 };
            panel.Children.Add(fields[i]);
        }
        void Sync() { fields[0].Value = repair.Signal == "analog" ? repair.Colour : repair.Block; fields[1].Value = repair.Detail; fields[2].Value = repair.Saturation; fields[3].Value = repair.Contrast; }
        Sync();
        preset.SelectionChanged += (_, _) => { if (preset.SelectedItem is RepairPreset selected) { repair = selected.AppliedTo(repair); repair.Name = selected.Name; Sync(); } };
        var preserve = new CheckBox { Content = "Preserve OSD", IsChecked = item.QueuedPreserveOsd == true }; panel.Children.Add(preserve);
        var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel" }; var save = new Button { Content = "Save" }; save.Classes.Add("accent");
        cancel.Click += (_, _) => dialog.Close();
        save.Click += (_, _) => { if (repair.Signal == "analog") repair.Colour = (int)(fields[0].Value ?? 0); else repair.Block = (int)(fields[0].Value ?? 0); repair.Detail = (int)(fields[1].Value ?? 0); repair.Saturation = (int)(fields[2].Value ?? 100); repair.Contrast = (int)(fields[3].Value ?? 100); item.QueuedRepair = repair; item.QueuedPreserveOsd = preserve.IsChecked == true; dialog.Close(); };
        actions.Children.Add(cancel); actions.Children.Add(save); panel.Children.Add(actions); dialog.Content = panel; await dialog.ShowDialog(owner);
    }

    private void NewSession_Click(object? sender, RoutedEventArgs e)
    {
        if (_exportCancel != null) return;
        StopPlay(); _autoRender.Stop(); _previewCancel?.Cancel(); DeleteLiveTicket();
        _selected = null; _queue.Clear(); _exportQueue.Clear(); _head = 0;
        _reviewOutput = null; _reviewSource = null; _reviewClip = null;
        SimpleExportResult.IsVisible = false; ClearSelection(); DrawQueue();
        Timeline.Head = 0; PlayheadLabel.Text = ""; HoverBadge.IsVisible = false; TrimLoading.IsVisible = false;
        InBox.Text = ""; OutBox.Text = ""; CutSummary.Text = ""; KeptLabel.Text = ""; PartsLabel.Text = "";
        CompareAfter.Source = null; CompareDivider.IsVisible = false; UpdateViewButtons(); _signalSettled.Clear();
        ApplyLevel("analog", "normal"); PreserveOsd.IsChecked = true; ShowOsdMask.IsChecked = false;
        ExportName.Text = ""; ExportStatus.Text = "Ready"; ExportRunText.Text = ""; ExportTime.Text = ""; ExportProgress.Value = 0;
    }

    /// <summary>Finds the 4:3 picture inside the bars once per clip, for cropped previews.</summary>
    private async Task MeasureBoxAsync(FootageItem clip)
    {
        if (clip.BoxRequested) return;
        clip.BoxRequested = true;
        try
        {
            var box = await EngineAsync(new { action = "picture", source = clip.Path });
            var found = (box.GetProperty("x").GetInt32(), box.GetProperty("y").GetInt32(),
                box.GetProperty("width").GetInt32(), box.GetProperty("height").GetInt32());
            clip.Box = found.Item3 < clip.Width || found.Item4 < clip.Height ? found : null;
        }
        catch { clip.Box = null; }
        clip.BoxMeasured = true;
        SettleSignal(clip);
    }

    /// <summary>A box scaled to another frame size (the cleaned file may be upscaled).</summary>
    private static (int X, int Y, int Width, int Height)? ScaledBox((int X, int Y, int Width, int Height)? box, int fromWidth, int toWidth)
    {
        if (box is not { } b || fromWidth <= 0 || toWidth == fromWidth) return box;
        var factor = (double)toWidth / fromWidth;
        return ((int)Math.Round(b.X * factor), (int)Math.Round(b.Y * factor), (int)Math.Round(b.Width * factor), (int)Math.Round(b.Height * factor));
    }

    /// <summary>The still shown without its black bars.</summary>
    private static IImage Cropped(Bitmap bitmap, (int X, int Y, int Width, int Height)? box, int sourceWidth)
    {
        var scaled = ScaledBox(box, sourceWidth, bitmap.PixelSize.Width);
        if (scaled is not { } b || b.Width <= 0 || b.Height <= 0) return bitmap;
        var rect = new PixelRect(Math.Clamp(b.X, 0, bitmap.PixelSize.Width - 1), Math.Clamp(b.Y, 0, bitmap.PixelSize.Height - 1),
            Math.Min(b.Width, bitmap.PixelSize.Width - Math.Clamp(b.X, 0, bitmap.PixelSize.Width - 1)),
            Math.Min(b.Height, bitmap.PixelSize.Height - Math.Clamp(b.Y, 0, bitmap.PixelSize.Height - 1)));
        return new CroppedBitmap(bitmap, rect);
    }

    /// <summary>Where a picture of the given aspect sits inside a canvas (Stretch=Uniform, centred).</summary>
    private static Rect ImageRect(Control canvas, double aspect)
    {
        var width = canvas.Bounds.Width;
        var height = canvas.Bounds.Height;
        if (width <= 0 || height <= 0 || aspect <= 0) return new Rect(0, 0, Math.Max(1, width), Math.Max(1, height));
        var shownWidth = Math.Min(width, height * aspect);
        var shownHeight = shownWidth / aspect;
        return new Rect((width - shownWidth) / 2, (height - shownHeight) / 2, shownWidth, shownHeight);
    }

    private static string QueueSummary(FootageItem clip)
    {
        var kept = clip.Segments.Count == 1 && clip.Kept >= clip.Duration - 0.05 ? "keeping everything"
            : clip.Segments.Count == 1 ? $"keeping {Clock(clip.In)} – {Clock(clip.Out)}"
            : $"keeping {Clock(clip.Kept)} in {clip.Segments.Count} parts";
        return $"{Clock(clip.Duration)} long · {kept}";
    }

    private void ClearSelection()
    {
        WorkspaceCompare.IsVisible = false;
        PreviewHost.IsVisible = true;
        TrimPreview.Source = null; PlayheadLabel.Text = "";
        SourceDetails.Text = "Select a recording";
        SourceLabels.IsVisible = false;
        Timeline.Duration = 1; Timeline.SetSegments([]); Timeline.SetBookmarks([]); Timeline.SetThumbnails([]); Timeline.SetWaveform(null, false);
        StripStatus.Text = "Choose a clip to build its filmstrip.";
        TrimHint.Text = "Pick the part to keep";
    }

    private void ClipPicker_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_pickingClip && ClipPicker.SelectedItem is FootageItem clip && clip != _selected) Select(clip);
    }

    private void Select(FootageItem clip)
    {
        StopPlay();
        WorkspaceCompare.IsVisible = false;
        PreviewHost.IsVisible = true;
        UpdateViewButtons();
        _selected = clip;
        ExportName.Text = System.IO.Path.GetFileNameWithoutExtension(clip.Path) + "-cleaned";
        ExportName.IsEnabled = true;
        var aspect = DisplayAspect(clip);
        var ratio = Math.Abs(aspect - 4.0/3) < .02 ? "4:3" : Math.Abs(aspect - 16.0/9) < .02 ? "16:9" : Math.Abs(aspect - 1.25) < .02 ? "5:4" : $"{aspect:0.##}:1";
        SourceDetails.Text = $"{DisplayWidth(clip, clip.Height)} × {clip.Height}\n{ratio}\n{clip.Fps:0.##} fps\n{(clip.Bitrate > 0 ? $"{clip.Bitrate / 1000000.0:0.##} Mbps" : "Unknown")}\n{clip.Size / 1000000.0:0.##} MB\n{Clock(clip.Duration)}";
        SourceLabels.IsVisible = true;
        RefreshOutputHeights();
        _head = clip.In;
        Timeline.Duration = clip.Duration;
        Timeline.SetSegments(clip.Segments);
        Timeline.SetBookmarks(clip.Bookmarks);
        Timeline.SetThumbnails(clip.Strip);
        Timeline.SetWaveform(clip.Waveform, clip.HasAudio);
        DrawQueue();
        UpdateCutFields();
        UpdateUndoButtons();
        LayoutStrip();
        RequestFrame();
        if (!clip.StripRequested) _ = LoadStripAsync(clip);

        ShowOsdState();
        SettleSignal(clip);
        RequestAutoRender();
    }

    // ---------------------------------------------------------------- trim editor

    private async Task LoadStripAsync(FootageItem clip)
    {
        clip.StripRequested = true;
        StripStatus.Text = "Building the filmstrip…";
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"fpv-strip-{Guid.NewGuid():N}");
        try
        {
            var outcome = await EngineAsync(new { action = "filmstrip", source = clip.Path, folder, count = 240, height = 82 });
            foreach (var tile in outcome.GetProperty("tiles").EnumerateArray())
                clip.Strip.Add((tile.GetProperty("seconds").GetDouble(), LoadBitmap(tile.GetProperty("path").GetString()!)));
            if (_selected == clip)
            {
                Timeline.SetThumbnails(clip.Strip);
                StripStatus.Text = "Drag the trim handles or press I / O.";
                ShowThumbnail(_head);
            }
        }
        catch (Exception ex) { clip.StripRequested = false; StripStatus.Text = $"Filmstrip unavailable: {ex.Message}"; }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    private async Task LoadWaveformAsync(FootageItem clip)
    {
        clip.WaveformRequested = true;
        try
        {
            var outcome = await EngineAsync(new { action = "waveform", source = clip.Path, buckets = 2000 });
            clip.Waveform = outcome.GetProperty("levels").EnumerateArray().Select(level => (float)level.GetDouble()).ToArray();
            if (_selected == clip) Timeline.SetWaveform(clip.Waveform, clip.HasAudio);
        }
        catch { clip.Waveform = []; if (_selected == clip) Timeline.SetWaveform(clip.Waveform, false); }
    }

    private void HookTimeline()
    {
        Timeline.HeadChanged += seconds => { StopPlay(); _head = seconds; PlaceMarkers(); ShowThumbnail(seconds); };
        Timeline.SelectionChanged += index =>
        {
            if (_selected == null) return;
            UpdateCutFields();
            StripStatus.Text = "Drag the trim handles or press I / O.";
        };
        Timeline.EditStarted += PushUndo;
        Timeline.SegmentsChanged += () =>
        {
            if (_selected == null) return;
            _selected.Segments = Timeline.Segments.ToList();
            UpdateCutFields();
        };
        Timeline.DragEnded += kind =>
        {
            _hoverPaused = true;
            HoverBadge.IsVisible = false;
            ShowThumbnail(_head);
            RequestFrame();
            RequestAutoRender();
            if (kind != TimelineView.DragKind.Playhead) DrawQueue();
        };
        Timeline.HoverChanged += seconds =>
        {
            if (_selected == null || _playing) return;
            if (seconds < 0) { _hoverPaused = false; HoverBadge.IsVisible = false; ShowThumbnail(_head); RequestFrame(); return; }
            if (_hoverPaused && Math.Abs(seconds - _head) * Timeline.PixelsPerSecond < 6) return;
            _hoverPaused = false;
            HoverBadge.IsVisible = true;
            HoverLabel.Text = Clock(seconds);
            _frameDelay.Stop();
            ShowThumbnail(seconds);
        };

        Timeline.ScrollRequested += pixels =>
            StripScroller.Offset = new Vector(Math.Clamp(StripScroller.Offset.X + pixels, 0, Math.Max(0, Timeline.Width - StripScroller.Bounds.Width)), 0);
    }

    private double StripWidth()
    {
        var view = StripScroller.Bounds.Width > 0 ? StripScroller.Bounds.Width : 1000;
        return Math.Max(view, view * ZoomSlider.Value);
    }

    private double XOf(double seconds) => _selected == null ? 0 : Timeline.XOf(seconds);

    private void LayoutStrip()
    {
        Timeline.Width = StripWidth();
        PlaceMarkers();
    }

    private void Strip_SizeChanged(object? sender, SizeChangedEventArgs e) => LayoutStrip();

    private void PlaceMarkers()
    {
        if (_selected == null) return;
        Timeline.Head = _head;
        PlayheadLabel.Text = Clock(_head);
        TrimHint.Text = _selected.Segments.Count == 1 ? $"Keeping {Clock(_selected.Kept)}" : $"Keeping {Clock(_selected.Kept)} in {_selected.Segments.Count} parts";
    }

    /// <summary>The part edits apply to: the selected one, else the one under or nearest to the playhead.</summary>
    private int CurrentSegment()
    {
        if (_selected == null || _selected.Segments.Count == 0) return -1;
        if (Timeline.Selected >= 0) return Timeline.Selected;
        var at = Timeline.SegmentAt(_head);
        return at >= 0 ? at : Timeline.NearestSegment(_head);
    }

    private void UpdateCutFields()
    {
        if (_selected == null) return;
        var index = CurrentSegment();
        if (index >= 0)
        {
            var segment = _selected.Segments[index];
            SegmentLabel.Text = _selected.Segments.Count > 1 ? $"PART {index + 1} OF {_selected.Segments.Count}: START" : "START";
            InBox.Text = Clock(segment.In);
            OutBox.Text = Clock(segment.Out);
        }
        KeptLabel.Text = $"Kept {Clock(_selected.Kept)} of {Clock(_selected.Duration)}";
        PartsLabel.Text = _selected.Segments.Count == 1 ? "" : $"· {_selected.Segments.Count} parts";
        CutSummary.Text = _selected.Segments.Count == 1
            ? $"[ {Clock(_selected.In)}   ] {Clock(_selected.Out)}"
            : string.Join("   ", _selected.Segments.Select((part, i) => $"{i + 1}: {Clock(part.In)}–{Clock(part.Out)}"));
        PlaceMarkers();
    }

    // ---- edits with undo

    private void PushUndo()
    {
        if (_selected == null) return;
        _selected.Undo.Push((_selected.Segments.ToList(), _selected.Bookmarks.ToList()));
        if (_selected.Undo.Count > 100) _selected.Undo.TryPop(out _);
        _selected.Redo.Clear();
        UpdateUndoButtons();
    }

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = _selected?.Undo.Count > 0;
        RedoButton.IsEnabled = _selected?.Redo.Count > 0;
    }

    /// <summary>Apply an edit to the selected clip with an undo step, then refresh everything.</summary>
    private void Edit(Action<FootageItem> change)
    {
        if (_selected == null) return;
        StopPlay();
        PushUndo();
        change(_selected);
        _selected.Segments = _selected.Segments.OrderBy(part => part.In).ToList();
        Timeline.SetSegments(_selected.Segments);
        Timeline.SetBookmarks(_selected.Bookmarks);
        UpdateCutFields();
        DrawQueue();
    }

    private void Undo_Click(object? sender, RoutedEventArgs e) => UndoEdit();
    private void Redo_Click(object? sender, RoutedEventArgs e) => RedoEdit();

    private void UndoEdit()
    {
        if (_selected == null || !_selected.Undo.TryPop(out var state)) return;
        _selected.Redo.Push((_selected.Segments.ToList(), _selected.Bookmarks.ToList()));
        _selected.Segments = state.Segments; _selected.Bookmarks = state.Bookmarks;
        Timeline.SetSegments(_selected.Segments); Timeline.SetBookmarks(_selected.Bookmarks);
        UpdateCutFields(); DrawQueue(); UpdateUndoButtons();
    }

    private void RedoEdit()
    {
        if (_selected == null || !_selected.Redo.TryPop(out var state)) return;
        _selected.Undo.Push((_selected.Segments.ToList(), _selected.Bookmarks.ToList()));
        _selected.Segments = state.Segments; _selected.Bookmarks = state.Bookmarks;
        Timeline.SetSegments(_selected.Segments); Timeline.SetBookmarks(_selected.Bookmarks);
        UpdateCutFields(); DrawQueue(); UpdateUndoButtons();
    }

    private void Split_Click(object? sender, RoutedEventArgs e) => SplitAtHead();

    private void SplitAtHead()
    {
        if (_selected == null) return;
        var index = Timeline.SegmentAt(_head);
        if (index < 0) { StripStatus.Text = "Move the playhead into a kept part to split it."; return; }
        var part = _selected.Segments[index];
        if (_head - part.In < 0.1 || part.Out - _head < 0.1) { StripStatus.Text = "Too close to the edge of the part to split."; return; }
        Edit(clip =>
        {
            clip.Segments.RemoveAt(index);
            clip.Segments.Insert(index, (_head, part.Out));
            clip.Segments.Insert(index, (part.In, _head));
        });
        Timeline.Selected = index + 1;
        UpdateCutFields();
        StripStatus.Text = $"Split into {_selected.Segments.Count} parts. Click a part and press Delete to remove it, or drag its edges.";
    }

    private void DeleteSegment_Click(object? sender, RoutedEventArgs e) => DeleteSegmentAtHead();

    private void DeleteSegmentAtHead()
    {
        if (_selected == null) return;
        var index = Timeline.Selected >= 0 ? Timeline.Selected : Timeline.SegmentAt(_head);
        if (index < 0) { StripStatus.Text = "Click the part you want to remove first."; return; }
        if (_selected.Segments.Count == 1) { StripStatus.Text = "At least one part has to stay. Split it first, or trim its edges."; return; }
        Edit(clip => clip.Segments.RemoveAt(index));
        Timeline.Selected = -1;
        MoveHead(Timeline.SourceOf(Timeline.OutputOf(_head)));
        UpdateCutFields();
        StripStatus.Text = $"Part removed. {Clock(_selected.Kept)} kept in {_selected.Segments.Count} part(s). Undo with Ctrl+Z.";
    }

    private void TrimBefore_Click(object? sender, RoutedEventArgs e) => MarkIn();
    private void TrimAfter_Click(object? sender, RoutedEventArgs e) => MarkOut();
    private void MarkIn_Click(object? sender, RoutedEventArgs e) => MarkIn();
    private void MarkOut_Click(object? sender, RoutedEventArgs e) => MarkOut();

    /// <summary>Start of the part under (or nearest to) the playhead moves to the playhead.</summary>
    private void MarkIn()
    {
        var index = CurrentSegment();
        if (index < 0 || _selected == null) return;
        var part = _selected.Segments[index];
        var floor = index > 0 ? _selected.Segments[index - 1].Out + 0.1 : 0;
        var start = Math.Clamp(_head, floor, part.Out - 0.1);
        Edit(clip => clip.Segments[index] = (start, part.Out));
    }

    private void MarkOut()
    {
        var index = CurrentSegment();
        if (index < 0 || _selected == null) return;
        var part = _selected.Segments[index];
        var ceiling = index < _selected.Segments.Count - 1 ? _selected.Segments[index + 1].In - 0.1 : _selected.Duration;
        var end = Math.Clamp(_head, part.In + 0.1, ceiling);
        Edit(clip => clip.Segments[index] = (part.In, end));
    }

    private void ResetCut_Click(object? sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        Edit(clip => clip.Segments = [(0, clip.Duration)]);
    }

    private void Bookmark_Click(object? sender, RoutedEventArgs e) => ToggleBookmark();

    private void ToggleBookmark()
    {
        if (_selected == null) return;
        var existing = _selected.Bookmarks.FindIndex(b => Math.Abs(b - _head) < 0.15);
        Edit(clip =>
        {
            if (existing >= 0) clip.Bookmarks.RemoveAt(existing);
            else { clip.Bookmarks.Add(_head); clip.Bookmarks.Sort(); }
        });
    }

    private IEnumerable<double> Edges()
    {
        if (_selected == null) yield break;
        foreach (var part in _selected.Segments) { yield return part.In; yield return part.Out; }
        foreach (var bookmark in _selected.Bookmarks) yield return bookmark;
    }

    private void PrevEdge_Click(object? sender, RoutedEventArgs e) => JumpEdge(-1);
    private void NextEdge_Click(object? sender, RoutedEventArgs e) => JumpEdge(1);

    private void JumpEdge(int direction)
    {
        var edges = direction < 0 ? Edges().Where(t => t < _head - 0.02).DefaultIfEmpty(0).Max()
                                  : Edges().Where(t => t > _head + 0.02).DefaultIfEmpty(_selected?.Duration ?? 0).Min();
        MoveHead(edges);
    }

    private void Snap_Changed(object? sender, RoutedEventArgs e)
    {
        if (Timeline == null || SnapToggle == null) return;
        Timeline.Snap = false;
        RememberSettings();
    }

    // ---- playhead and preview

    private void MoveHead(double seconds, bool fetch = true)
    {
        if (_selected == null) return;
        _head = Math.Clamp(seconds, 0, _selected.Duration);
        PlaceMarkers();
        UpdateCutFields();
        KeepHeadVisible();
        ShowThumbnail(_head);
        if (fetch) { RequestFrame(); RequestAutoRender(); }
    }

    private void KeepHeadVisible()
    {
        var x = XOf(_head);
        var view = StripScroller.Bounds.Width;
        var offset = StripScroller.Offset.X;
        if (x < offset + 20 || x > offset + view - 20)
            StripScroller.Offset = new Vector(Math.Max(0, x - view / 2), 0);
    }

    private void ShowThumbnail(double seconds)
    {
        if (_selected == null || _selected.Strip.Count == 0) return;
        var nearest = _selected.Strip.MinBy(tile => Math.Abs(tile.Seconds - seconds));
        TrimPreview.Source = nearest.Picture;
    }

    private void RequestFrame()
    {
        _frameDelay.Stop();
        _frameDelay.Start();
    }

    private async Task RefreshFrameAsync()
    {
        if (_selected == null) return;
        var request = ++_frameRequest;
        var clip = _selected;
        var moment = _head;
        var target = TempFile("frame");
        TrimLoading.IsVisible = true;
        try
        {
            await EngineAsync(new { action = "frame", source = clip.Path, seconds = moment, target,
                display_aspect = true });
            if (request == _frameRequest && clip == _selected && !HoverBadge.IsVisible) TrimPreview.Source = LoadBitmap(target);
        }
        catch { /* A damaged frame keeps the thumbnail in view. */ }
        finally { File.Delete(target); if (request == _frameRequest) TrimLoading.IsVisible = false; }
    }

    // ---- zoom

    private void Zoom_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Timeline == null) return;
        LayoutStrip();
        KeepHeadVisible();
    }

    /// <summary>Zoom so that the given moment stays under the pointer (or centred when none).</summary>
    private void ZoomTo(double zoom, double anchor, double viewportX)
    {
        if (_selected == null) return;
        zoom = Math.Clamp(zoom, ZoomSlider.Minimum, ZoomSlider.Maximum);
        ZoomSlider.Value = zoom;
        Timeline.Width = StripWidth();
        var offset = XOf(anchor) - viewportX;
        StripScroller.Offset = new Vector(Math.Clamp(offset, 0, Math.Max(0, Timeline.Width - StripScroller.Bounds.Width)), 0);
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => ZoomTo(ZoomSlider.Value * 1.5, _head, StripScroller.Bounds.Width / 2);
    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => ZoomTo(ZoomSlider.Value / 1.5, _head, StripScroller.Bounds.Width / 2);
    private void ZoomFit_Click(object? sender, RoutedEventArgs e) => ZoomTo(1, 0, 0);

    // ---- play

    private void UpdateViewButtons()
    {
        OriginalView.Classes.Set("accent", !WorkspaceCompare.IsVisible);
        CompareView.Classes.Set("accent", WorkspaceCompare.IsVisible);
    }

    private void WorkspaceOriginal_Click(object? sender, RoutedEventArgs e) => SetWorkspaceView(false);
    private void WorkspaceCompare_Click(object? sender, RoutedEventArgs e) => SetWorkspaceView(true);

    private void SetWorkspaceView(bool compare)
    {
        if (_selected == null || WorkspaceCompare.IsVisible == compare) return;
        var resume = _playing;
        StopPlay();
        WorkspaceCompare.IsVisible = compare;
        PreviewHost.IsVisible = !compare;
        UpdateViewButtons();
        if (compare) { CompareSlider.Value = 50; CompareAfter.Source = null; CompareDivider.IsVisible = false; UpdateCompare(); }
        if (resume) StartPlayback(_head);
        else if (compare) RequestAutoRender();
        else { _previewCancel?.Cancel(); RequestFrame(); }
    }

    private string? _liveTicket;
    private void DeleteLiveTicket()
    {
        if (_liveTicket != null) { try { File.Delete(_liveTicket); } catch (IOException) { } _liveTicket = null; }
    }

    private void PlayRestored(FootageItem clip, double moment, bool singleFrame = false)
    {
        _player.Stop();
        DeleteLiveTicket();
        _liveTicket = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dvr-live-{Guid.NewGuid():N}.json");
        PreviewStatus.Text = "Preparing restoration…";
        _player.PlayRestored(moment, clip.Fps, clip.Width, clip.Height, clip.Sar, (width, height, fps) =>
        {
            File.WriteAllText(_liveTicket, JsonSerializer.Serialize(new { action = "live_pairs", source = clip.Path,
                seconds = moment, width, height, fps, single_frame = singleFrame, show_osd_mask = ShowOsdMask.IsChecked == true, recipe = BuildRecipe(clip), mode = Mode, model = ModelPath.Text }));
            return EngineStart(_liveTicket);
        });
    }

    private void OnWorkspacePair(WriteableBitmap original, WriteableBitmap restored, double moment)
    {
        if (_selected == null || !WorkspaceCompare.IsVisible) return;
        if (_playing && moment >= _selected.Out) { EndTrimPlayback(); return; }
        _head = moment;
        _compareAspect = DisplayAspect(_selected);
        CompareBefore.Source = original; CompareAfter.Source = restored;
        CompareBefore.InvalidateVisual(); CompareAfter.InvalidateVisual();
        CompareHint.IsVisible = false; CompareDivider.IsVisible = true;
        LookCompare.IsVisible = false;
        CompareLeftLabel.Text = "ORIGINAL"; CompareRightLabel.Text = "RESTORED";
        PreviewStatus.Text = "Live restoration · export processing stages";
        UpdateCompare(); PlaceMarkers(); KeepHeadVisible();
    }

    private void Play_Click(object? sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_selected == null) return;
        if (_playing) { StopPlay(); return; }
        var from = _head;
        if (from < _selected.In || from >= _selected.Out - 0.0001) from = _selected.In;
        StartPlayback(from);
    }

    private void StartPlayback(double from)
    {
        if (_selected == null) return;
        if (from < _selected.In || from >= _selected.Out) from = _selected.In;
        HoverBadge.IsVisible = false;
        _frameDelay.Stop();
        _autoRender.Stop(); _previewCancel?.Cancel();
        _playing = true;
        UpdatePlayIcon(true);
        try {
            if (WorkspaceCompare.IsVisible) PlayRestored(_selected, from);
            else _player.Play(_selected.Path, from, _selected.Fps, _selected.Width, _selected.Height, null, _selected.Sar);
        }
        catch (Exception ex) { _playing = false; UpdatePlayIcon(false); StripStatus.Text = $"Playback unavailable: {ex.Message}"; }
    }

    private void StopPlay()
    {
        if (!_playing) return;
        _playing = false;
        _player.Stop();
        UpdatePlayIcon(false);
        RequestFrame(); RequestAutoRender();
    }

    private void OnPlaybackFrame(WriteableBitmap bitmap, double moment)
    {
        if (!_playing || _selected == null) return;
        if (moment >= _selected.Out) { EndTrimPlayback(); return; }
        _head = moment;
        if (!ReferenceEquals(TrimPreview.Source, bitmap)) TrimPreview.Source = bitmap;
        TrimPreview.InvalidateVisual();
        PlaceMarkers();
        KeepHeadVisible();
    }

    private void EndTrimPlayback()
    {
        if (_selected == null) return;
        _head = _selected.Out;
        StopPlay();
        PlaceMarkers(); UpdateCutFields(); KeepHeadVisible();
    }

    private void ContinueOrStop() => EndTrimPlayback();

    private void GoStart_Click(object? sender, RoutedEventArgs e) { if (_selected != null) MoveHead(0); }
    private void GoEnd_Click(object? sender, RoutedEventArgs e) { if (_selected != null) MoveHead(_selected.Duration); }
    private void Back_Click(object? sender, RoutedEventArgs e) => MoveHead(_head - 1);
    private void Forward_Click(object? sender, RoutedEventArgs e) => MoveHead(_head + 1);

    private void CutBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CutBox_Changed(sender, e); e.Handled = true; }
    }

    private void CutBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        var index = CurrentSegment();
        if (index >= 0 && TryParseClock(InBox.Text, out var start) && TryParseClock(OutBox.Text, out var end) && end > start)
        {
            var floor = index > 0 ? _selected.Segments[index - 1].Out + 0.1 : 0;
            var ceiling = index < _selected.Segments.Count - 1 ? _selected.Segments[index + 1].In - 0.1 : _selected.Duration;
            end = Math.Clamp(end, floor + 0.1, ceiling);
            start = Math.Clamp(start, floor, end - 0.1);
            Edit(clip => clip.Segments[index] = (start, end));
        }
        else UpdateCutFields();
    }

    private void Window_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (PageContent == null || PreviewHost == null) return;
        // Wide windows get a wider page; tall windows a taller preview, so the editor fills the screen.
        PageContent.MaxWidth = Math.Max(1180, e.NewSize.Width - 232 - 56 - 20);
        FitTrimPage();
        if (_page == 1) LayoutStrip();
    }

    /// <summary>The trim workspace fills the page exactly; nothing scrolls, the preview takes the rest.</summary>
    private void FitTrimPage()
    {
        if (TrimPage == null || PageScroller == null) return;
        var dark = _page == 1;
        PageScroller.Background = dark ? new SolidColorBrush(Color.Parse("#1E1E1E")) : Brushes.Transparent;
        PageContent.Margin = dark ? new Thickness(0) : _page is 2 or 3 ? new Thickness(20, 14) : new Thickness(28, 22);
        PageContent.MaxWidth = dark ? double.PositiveInfinity : Math.Max(1180, Bounds.Width - 232 - 56 - 20);
        if (dark) Workspace.Height = Math.Max(380, PageScroller.Bounds.Height);
    }

    private void WorkspaceFullScreen_Click(object? sender, RoutedEventArgs e) => ToggleFullScreen();

    private void UpdatePlayIcon(bool playing)
    {
        PlayButton.Content = playing ? "Ⅱ" : "▶";
        PlayButton.Classes.Set("playing", playing);
    }

    private void ToggleFullScreen()
    {
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
        FullScreenButton.Content = WindowState == WindowState.FullScreen ? "Window" : "Full screen";
    }

    private bool _spaceHeld;

    private void PlaybackShortcutReleased(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        if (_spaceHeld || FocusManager?.GetFocusedElement() is not TextBox) e.Handled = true;
        _spaceHeld = false;
    }

    private void PlaybackShortcut(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || FocusManager?.GetFocusedElement() is TextBox) return;
        e.Handled = true;
        if (_spaceHeld) return;
        _spaceHeld = true;
        if (BusyOverlay.IsVisible || MaskOverlay.IsVisible || ShareOverlay.IsVisible) return;
        if (_page == 1) TogglePlay();
        else if (_page == 3 && ReviewPanel.IsVisible && _reviewOutput != null) ReviewPlay_Click(sender, e);
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; return; }
        if (e.Key == Key.Escape && ShareOverlay.IsVisible && !BusyOverlay.IsVisible) { CloseShare(); e.Handled = true; return; }
        if (MaskOverlay.IsVisible) { if (!BusyOverlay.IsVisible && MaskKey(e)) e.Handled = true; return; }
        if (e.Key == Key.Escape && WindowState == WindowState.FullScreen) { WindowState = WindowState.Normal; e.Handled = true; return; }
        if (_page != 1 || _selected == null || FocusManager?.GetFocusedElement() is TextBox) return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : 1 / Math.Max(1, _selected.Fps);
        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        switch (e.Key)
        {
            case Key.Space: return;
            case Key.Left: StopPlay(); MoveHead(_head - step); break;
            case Key.Right: StopPlay(); MoveHead(_head + step); break;
            case Key.I when !control: MarkIn(); break;
            case Key.O when !control: MarkOut(); break;
            case Key.X when e.KeyModifiers.HasFlag(KeyModifiers.Alt): ResetCut_Click(null, e); break;
            case Key.Z when control && e.KeyModifiers.HasFlag(KeyModifiers.Shift): RedoEdit(); break;
            case Key.Z when control: UndoEdit(); break;
            case Key.Y when control: RedoEdit(); break;
            case Key.Home: MoveHead(0); break;
            case Key.End: MoveHead(_selected.Duration); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------------------------------------------------------------- clean

    private bool IsAnalog => AnalogChoice.IsChecked == true;
    private int SizePercent => Size200.IsChecked == true ? 200 : Size150.IsChecked == true ? 150 : 100;

    private void Signal_Changed(object? sender, RoutedEventArgs e)
    {
        if (ColourCard == null || BlockCard == null) return;
        ColourCard.IsVisible = IsAnalog;
        BlockCard.IsVisible = !IsAnalog;
        SignalNote.Text = IsAnalog ? UiText.SignalNoteAnalog : UiText.SignalNoteHdzero;
        RefreshModelChoices();
        UpdateCleanHint();
    }

    private void Setting_Changed(object? sender, RoutedEventArgs e)
    {
        if (_applyingPreset) return;
        if (ColourValue == null || BlockValue == null || DetailValue == null || MotionValue == null || NoiseValue == null) return;
        ColourValue.Text = $"{(int)ColorSlider.Value}";
        BlockValue.Text = $"{(int)BlockSlider.Value}";
        DetailValue.Text = $"{(int)DetailSlider.Value}";
        MotionValue.Text = $"{(int)MotionSlider.Value}";
        NoiseValue.Text = $"{(int)NoiseSlider.Value}";
        UpdateCleanHint();
    }

    private void UpdateCleanHint()
    {
        if (CleanHint == null || RemoveOsd == null) return;
        var parts = new List<string> { IsAnalog ? "Analog" : "HDZero" };
        if (NoiseSlider.Value > 0) parts.Add("denoised");
        if (RemoveOsd.IsChecked == true) parts.Add("OSD removed");
        if (SizePercent != 100) parts.Add((SizePercent / 100.0).ToString("0.#", CultureInfo.InvariantCulture) + "×");
        if (Effort != "balanced") parts.Add(Effort);
        if (LookOn) parts.Add("look");
        CleanHint.Text = string.Join(" · ", parts);
        if (!_applyingPreset) RepairChanged();
    }

    private string Effort => EffortFast?.IsChecked == true ? "fast" : EffortBest?.IsChecked == true ? "best" : "balanced";

    private void Effort_Changed(object? sender, RoutedEventArgs e)
    {
        if (EffortNote == null) return;
        EffortNote.Text = Effort switch
        {
            "fast" => UiText.SpeedNoteFast,
            "best" => UiText.SpeedNoteBest,
            _ => UiText.SpeedNoteBalanced,
        };
        UpdateCleanHint();
    }

    private void Mode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (ModelPanel != null) ModelPanel.IsVisible = ModeChoice.SelectedIndex == 1;
    }

    // ---------------------------------------------------------------- look

    /// <summary>Look presets: strengths of grade, glow, vignette, grain and lens fringe, 0..100.</summary>
    private static readonly (string Id, string Title, (int Grade, int Glow, int Vignette, int Grain, int Fringe) Values)[] Looks =
    [
        ("clean", "Clean", (30, 20, 25, 10, 0)),
        ("cinematic", "Cinematic", (60, 45, 50, 25, 35)),
        ("punchy", "Punchy", (100, 70, 60, 15, 50)),
        ("retro-fpv", "Retro FPV", (50, 80, 85, 90, 100)),
    ];

    private bool _settingLook;

    private bool LookOn => LookToggle?.IsChecked == true;

    private static int LookValue(Slider slider) => (int)Math.Round(slider.Value);

    private (int Grade, int Glow, int Vignette, int Grain, int Fringe) LookValues =>
        (LookValue(LookGradeSlider), LookValue(LookGlowSlider), LookValue(LookVignetteSlider), LookValue(LookGrainSlider), LookValue(LookFringeSlider));

    private void SetLook(bool on, (int Grade, int Glow, int Vignette, int Grain, int Fringe) values)
    {
        _settingLook = true;
        try
        {
            LookToggle.IsChecked = on;
            LookGradeSlider.Value = Math.Clamp(values.Grade, 0, 100);
            LookGlowSlider.Value = Math.Clamp(values.Glow, 0, 100);
            LookVignetteSlider.Value = Math.Clamp(values.Vignette, 0, 100);
            LookGrainSlider.Value = Math.Clamp(values.Grain, 0, 100);
            LookFringeSlider.Value = Math.Clamp(values.Fringe, 0, 100);
        }
        finally { _settingLook = false; }
        ShowLook();
    }

    private void LookPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string id }) return;
        var look = Looks.FirstOrDefault(item => item.Id == id);
        // Off keeps the slider values, so switching back on returns to them.
        if (look.Id == null) SetLook(false, LookValues);
        else SetLook(true, look.Values);
    }

    private void LookToggle_Changed(object? sender, RoutedEventArgs e)
    {
        if (_settingLook || LookFringeSlider == null) return;
        // Switched on with every slider at 0: start from the Clean preset.
        if (LookOn && LookValues == (0, 0, 0, 0, 0)) SetLook(true, Looks[0].Values);
        else ShowLook();
    }

    private void LookSlider_Changed(object? sender, RoutedEventArgs e)
    {
        if (_settingLook || LookFringeSlider == null) return;
        ShowLook();
    }

    /// <summary>Values, enabled state, and which preset chip matches; any other values are Custom.</summary>
    private void ShowLook()
    {
        if (LookFringeSlider == null || LookPresets == null) return;
        var values = LookValues;
        LookGradeValue.Text = $"{values.Grade}";
        LookGlowValue.Text = $"{values.Glow}";
        LookVignetteValue.Text = $"{values.Vignette}";
        LookGrainValue.Text = $"{values.Grain}";
        LookFringeValue.Text = $"{values.Fringe}";
        LookSliders.IsEnabled = LookOn;
        var match = Looks.FirstOrDefault(item => item.Values == values);
        var chosen = !LookOn ? "off" : match.Id;
        foreach (var chip in LookPresets.Children.OfType<RadioButton>())
            chip.IsChecked = chosen != null && chip.Tag as string == chosen;
        LookCustom.IsVisible = LookOn && match.Id == null;
        ToolTip.SetTip(LookToggle, !LookOn ? UiText.LookOff
            : match.Id != null ? string.Format(CultureInfo.CurrentCulture, UiText.LookPresetOn, match.Title)
            : UiText.LookCustomOn);
        UpdateCleanHint();
    }

    private void LookAdjust_Click(object? sender, RoutedEventArgs e)
    {
        LookSliders.IsVisible = !LookSliders.IsVisible;
        LookAdjustButton.Content = LookSliders.IsVisible ? UiText.Hide : UiText.Adjust;
        ToolTip.SetTip(LookAdjustButton, LookSliders.IsVisible ? "Fold the look sliders away" : "Show the five look sliders");
    }

    // The last rendered frame: original and, with a look, the repaired frame without it.
    private IImage? _compareOriginal, _comparePlain;

    private void LookCompare_Changed(object? sender, RoutedEventArgs e) => ShowCompareLeft();

    private void ShowCompareLeft()
    {
        if (CompareLeftLabel == null || LookCompare == null) return;
        var plain = LookCompare.IsChecked == true && _comparePlain != null;
        if (_compareOriginal != null) CompareBefore.Source = plain ? _comparePlain : _compareOriginal;
        CompareLeftLabel.Text = plain ? "WITHOUT LOOK" : UiText.LabelOriginal;
        CompareRightLabel.Text = UiText.LabelCleaned;
    }

    // ---------------------------------------------------------------- presets

    private RepairPreset CurrentRepair() => new()
    {
        Signal = IsAnalog ? "analog" : "digital",
        Saturation = (int)(SaturationSlider?.Value ?? 100),
        Brightness = _brightness, Contrast = _contrast, Gamma = _gamma, Spatial = _spatial,
        Colour = (int)ColorSlider.Value, Noise = (int)NoiseSlider.Value, Block = (int)BlockSlider.Value,
        Detail = (int)DetailSlider.Value, Motion = (int)MotionSlider.Value, Size = SizePercent,
        Osd = RemoveOsd.IsChecked == true, Effort = Effort,
        Quality = (int)(QualityValue.Value ?? 18), SizeCapMb = (int)(SizeCap.Value ?? 0),
        Look = LookOn, LookGrade = LookValue(LookGradeSlider), LookGlow = LookValue(LookGlowSlider),
        LookVignette = LookValue(LookVignetteSlider), LookGrain = LookValue(LookGrainSlider), LookFringe = LookValue(LookFringeSlider),
    };

    private void ApplyRepair(RepairPreset preset)
    {
        _applyingPreset = true;
        try
        {
            if (preset.Signal == "digital") DigitalChoice.IsChecked = true; else AnalogChoice.IsChecked = true;
            ColorSlider.Value = Math.Clamp(preset.Colour, 0, 100);
            NoiseSlider.Value = Math.Clamp(preset.Noise, 0, 100);
            BlockSlider.Value = Math.Clamp(preset.Block, 0, 100);
            DetailSlider.Value = Math.Clamp(preset.Detail, 0, 100);
            MotionSlider.Value = 0;
            SaturationSlider.Value = Math.Clamp(preset.Saturation, 0, 200);
            _brightness = preset.Brightness; _contrast = preset.Contrast; _gamma = preset.Gamma; _spatial = preset.Spatial;
            SizeOriginal.IsChecked = true; Size150.IsChecked = false; Size200.IsChecked = false;
            RemoveOsd.IsChecked = false; OsdKeep.IsChecked = true;
            EffortFast.IsChecked = false; EffortBest.IsChecked = false;
            EffortBalanced.IsChecked = true;
            QualityValue.Value = Math.Clamp(preset.Quality, 14, 32);
            SizeCap.Value = Math.Max(0, preset.SizeCapMb);
            SetLook(false, (0, 0, 0, 0, 0));
        }
        finally { _applyingPreset = false; }
        Setting_Changed(null, new RoutedEventArgs());
    }

    private object BuildRecipe(FootageItem clip) => RecipeOf(CurrentRepair(), clip);

    /// <summary>The engine recipe for these settings and this clip, with the export choices of the Export page.</summary>
    private object RecipeOf(RepairPreset settings, FootageItem clip, bool? preserve = null) => new
    {
        signalart = settings.Signal == "digital" ? "digital" : "analog",
        farbberuhigung = Math.Clamp(settings.Colour, 0, 100),
        blockpflege = Math.Clamp(settings.Block, 0, 100),
        kantendetail = Math.Clamp(settings.Detail, 0, 100),
        vergroesserung_prozent = 100,
        output_height = SelectedOutputHeight == 0 ? clip.Height : Math.Max(clip.Height, SelectedOutputHeight),
        comparison = OutputLayoutChoice.SelectedIndex == 1,
        remove_audio = RemoveAudio.IsChecked == true,
        saturation = Math.Clamp(settings.Saturation, 0, 200) / 100.0,
        brightness = settings.Brightness / 100.0, contrast = settings.Contrast / 100.0, gamma = settings.Gamma / 100.0, spatial = settings.Spatial / 100.0,
        bewegungsspur = Math.Clamp(settings.Motion, 0, 100),
        schnittbeginn = clip.In,
        schnittende = clip.Out,
        untertiteltext = "",
        kompressionswert = (int)(QualityValue.Value ?? 18),
        dateiziel_mb = 0,
        osd_entfernen = settings.Osd,
        preserve_osd = (preserve ?? (PreserveOsd.IsChecked == true)) && !settings.Osd,
        rauschminderung = Math.Clamp(settings.Noise, 0, 100),
        aufwand = settings.Effort is "fast" or "best" ? settings.Effort : "balanced",
        dateiformat = OutputFormat.Id,
        look = false,
        look_farbstimmung = Math.Clamp(settings.LookGrade, 0, 100) / 100.0,
        look_leuchten = Math.Clamp(settings.LookGlow, 0, 100) / 100.0,
        look_farbsaum = Math.Clamp(settings.LookFringe, 0, 100) / 100.0,
        look_vignette = Math.Clamp(settings.LookVignette, 0, 100) / 100.0,
        look_koernung = Math.Clamp(settings.LookGrain, 0, 100) / 100.0,
        // Hand corrections of the OSD mask, made in the mask editor for this recording.
        osd_handmaske = clip.ManualMask is { } mask && File.Exists(mask) ? mask : "",
    };

    private string Mode => ModeChoice.SelectedIndex == 1 ? "model" : "manual";

    private void Compare_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e) => UpdateCompare();

    private bool _compareDragging;
    private double _compareAspect = 16.0 / 9;
    private double _reviewAspect = 16.0 / 9;

    private void SetCompareFromPointer(double x)
    {
        var shown = ImageRect(CompareCanvas, _compareAspect);
        CompareSlider.Value = Math.Clamp((x - shown.X) / Math.Max(1, shown.Width) * 100, 0, 100);
    }
    private void Compare_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CompareDivider.IsVisible || !e.GetCurrentPoint(CompareCanvas).Properties.IsLeftButtonPressed) return;
        _compareDragging = true; e.Pointer.Capture(CompareCanvas);
        SetCompareFromPointer(e.GetPosition(CompareCanvas).X);
        e.Handled = true;
    }
    private void Compare_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_compareDragging) SetCompareFromPointer(e.GetPosition(CompareCanvas).X);
    }
    private void Compare_PointerReleased(object? sender, PointerReleasedEventArgs e) { _compareDragging = false; e.Pointer.Capture(null); }
    private void Compare_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateCompare();

    private void UpdateCompare()
    {
        if (CompareCanvas == null || CompareAfter == null || CompareDivider == null) return;
        // The split line lives inside the shown picture, not in the empty canvas around it.
        var shown = ImageRect(CompareCanvas, _compareAspect);
        var width = CompareCanvas.Bounds.Width;
        var edge = shown.X + shown.Width * CompareSlider.Value / 100;
        CompareAfterClip.Clip = new RectangleGeometry(new Rect(edge, 0, Math.Max(0, width - edge), CompareCanvas.Bounds.Height));
        CompareDivider.Margin = new Thickness(edge - 1, 0, 0, 0);
        CompareGrip.Margin = new Thickness(edge - 13, 0, 0, 0);
        CompareGrip.IsVisible = CompareDivider.IsVisible;
    }

    // ---------------------------------------------------------------- models

    private static bool IsCompatibleModel(string path, string signal)
    {
        try
        {
            using var data = JsonDocument.Parse(File.ReadAllText(path));
            return data.RootElement.GetProperty("format").GetString() is
                       "fpv-blind-neighborhood-1" or "fpv-motion-context-2" &&
                   data.RootElement.GetProperty("signalart").GetString() == signal;
        }
        catch { return false; }
    }

    private static IEnumerable<string> ModelFiles()
    {
        var folders = new List<string> { ModelFolder() };
        try { folders.Add(System.IO.Path.Combine(RepoRoot(), ".scratch", "training", "profiles")); }
        catch (InvalidOperationException) { }
        return folders.Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.json"))
            .OrderByDescending(File.GetLastWriteTimeUtc);
    }

    private void RefreshModelChoices()
    {
        if (ModelChoice == null || AnalogChoice == null) return;
        var signal = IsAnalog ? "analog" : "digital";
        var previous = ModelPath?.Text;
        ModelChoice.Items.Clear();
        foreach (var path in ModelFiles().Where(path => IsCompatibleModel(path, signal)))
            ModelChoice.Items.Add(new ComboBoxItem { Content = System.IO.Path.GetFileNameWithoutExtension(path), Tag = path });
        var selected = ModelChoice.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, previous));
        ModelChoice.SelectedItem = selected ?? ModelChoice.Items.OfType<ComboBoxItem>().FirstOrDefault();
        if (ModelChoice.Items.Count == 0 && ModelPath != null) ModelPath.Text = "";
    }

    private void RefreshResumeChoices()
    {
        if (ResumeChoice == null || TrainSignal == null) return;
        var signal = TrainSignal.SelectedIndex == 0 ? "analog" : "digital";
        ResumeChoice.Items.Clear();
        foreach (var path in ModelFiles().Where(path => IsCompatibleModel(path, signal)))
            ResumeChoice.Items.Add(new ComboBoxItem { Content = System.IO.Path.GetFileNameWithoutExtension(path), Tag = path });
        ResumeChoice.SelectedItem = ResumeChoice.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void ModelChoice_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (ModelPath != null && ModelChoice?.SelectedItem is ComboBoxItem entry) ModelPath.Text = entry.Tag as string;
    }

    private async void ModelBrowse_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path == null) return;
        if (!IsCompatibleModel(path, IsAnalog ? "analog" : "digital")) { PreviewStatus.Text = "That file is not a model for this signal type."; return; }
        ModelPath.Text = path;
    }

    private void TrainSignal_Changed(object? sender, SelectionChangedEventArgs e) => RefreshResumeChoices();
    private void TrainApproach_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (ResumePanel != null && TrainApproach != null) ResumePanel.IsVisible = TrainApproach.SelectedIndex == 1;
    }
    private void TrainUnit_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (TrainMinutes != null && TrainUnit != null) TrainMinutes.Maximum = TrainUnit.SelectedIndex == 1 ? 24 : 60;
    }
    private async void TrainFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync(TrainFolder.Text) is { } folder) { TrainFolder.Text = folder; RememberSettings(); }
    }

    private async void StartTrain_Click(object? sender, RoutedEventArgs e)
    {
        if (_trainingCancel != null) return;
        if (string.IsNullOrWhiteSpace(TrainFolder.Text) || !Directory.Exists(TrainFolder.Text)) { TrainStatus.Text = "Choose a folder with recordings."; return; }
        var signal = TrainSignal.SelectedIndex == 0 ? "analog" : "digital";
        var continuing = TrainApproach.SelectedIndex == 1;
        var initialModel = continuing ? (ResumeChoice.SelectedItem as ComboBoxItem)?.Tag as string : null;
        if (continuing && (initialModel == null || !IsCompatibleModel(initialModel, signal)))
        { TrainStatus.Text = "Choose one of your models for this signal type."; return; }
        var destination = System.IO.Path.Combine(ModelFolder(), $"{signal}-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        TrainStatus.Text = "Sampling footage and training locally…"; TrainProgress.Value = 0;
        _trainingCancel = new CancellationTokenSource(); CancelTrainButton.IsEnabled = true;
        ShowBusy("Training the model", TrainStatus.Text, () => _trainingCancel?.Cancel());
        UpdateBusy(0);
        try
        {
            var seconds = (int)(TrainMinutes.Value ?? 5) * (TrainUnit.SelectedIndex == 1 ? 3600 : 60);
            var started = DateTime.UtcNow;
            var outcome = await EngineAsync(new { action = "train", folder = TrainFolder.Text, signal, seconds,
                initial_model = initialModel, target = destination },
                item => Dispatcher.UIThread.Post(() =>
                {
                    TrainProgress.Value = Math.Min(99, (DateTime.UtcNow - started).TotalSeconds / seconds * 100);
                    TrainStatus.Text = $"Learning from {item.GetProperty("samples").GetInt32():N0} samples…";
                    UpdateBusy(TrainProgress.Value / 100, TrainStatus.Text, Duration(DateTime.UtcNow - started));
                }), _trainingCancel.Token);
            TrainProgress.Value = 100;
            TrainStatus.Text = $"Saved version {outcome.GetProperty("training_runs").GetInt32()} ({outcome.GetProperty("samples").GetInt32():N0} sampled pixels) to {destination}";
            RefreshModelChoices(); RefreshResumeChoices();
        }
        catch (OperationCanceledException) { TrainStatus.Text = "Training stopped."; }
        catch (Exception ex) { TrainStatus.Text = ex.Message; }
        finally { _trainingCancel.Dispose(); _trainingCancel = null; CancelTrainButton.IsEnabled = false; HideBusy(); }
    }
    private void CancelTrain_Click(object? sender, RoutedEventArgs e) => _trainingCancel?.Cancel();

    // ---------------------------------------------------------------- export

    // The folder the user chose; null means the default "Cleaned" folder next to the first clip.
    private string? _outputFolder;

    /// <summary>A "Cleaned" folder next to the first clip in the queue, made when the export starts.</summary>
    private string? DefaultOutputFolder()
    {
        var first = _queue.FirstOrDefault()?.Path;
        var folder = first == null ? null : System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(first));
        return folder == null ? null : System.IO.Path.Combine(folder, "Cleaned");
    }

    /// <summary>Where the files go: the chosen folder, else the default next to the first clip.</summary>
    private string? OutputFolderPath => !string.IsNullOrWhiteSpace(_outputFolder) ? _outputFolder : DefaultOutputFolder();

    private void ShowOutputFolder()
    {
        if (OutputFolderChip == null) return;
        var folder = OutputFolderPath;
        OutputFolderChip.Text = folder ?? UiText.FolderNone;
        ToolTip.SetTip(OutputFolderChip, folder);
        OutputFolderNote.Text = folder != null && !Directory.Exists(folder) ? UiText.FolderCreatedOnExport : "";
    }

    private async void OutputFolder_Click(object? sender, RoutedEventArgs e)
    {
        var start = OutputFolderPath;
        if (start != null && !Directory.Exists(start)) start = System.IO.Path.GetDirectoryName(start);
        if (await PickFolderAsync(start) is { } folder) { _outputFolder = folder; ShowOutputFolder(); RememberSettings(); }
    }

    private void OpenOutput_Click(object? sender, RoutedEventArgs e)
    {
        var folder = OutputFolderPath;
        if (!Directory.Exists(folder)) return;
        try { OpenInFileManager(folder); }
        catch (Exception ex) { ExportStatus.Text = $"Could not open the folder: {ex.Message}"; }
    }

    private void UpdateExportSummary()
    {
        var pending = ExportClips();
        var total = pending.Sum(clip => clip.Kept);
        QueueCounter.Content = $"Queue ({_exportQueue.Count})";
        if (_exportCancel == null && !SimpleExportResult.IsVisible) { ExportStatus.Text = _selected is { } clip ? $"{DisplayWidth(clip, clip.Height)} × {clip.Height} · {clip.Fps:0.##} fps · {Clock(total)}" : ""; }
        ExportButton.Content = "Export video";
        ExportButton.IsEnabled = pending.Count > 0 && _exportCancel == null;
        AddSectionButton.IsEnabled = _selected != null && _exportCancel == null;
        ExportName.IsEnabled = true;
        ExportSummary.Text = pending.Count == 0 ? UiText.ExportSummaryEmpty
            : string.Format(CultureInfo.CurrentCulture, UiText.ExportSummary, pending.Count, Clock(total), CleanHint.Text);
        ExportFoldedText.Text = string.Format(CultureInfo.CurrentCulture, UiText.ExportFoldedSummary, OutputFolderPath, OutputFormat.Title);
    }

    private Window? _exportDialog;
    private async void Export_Clicked(object? sender, RoutedEventArgs e)
    {
        if (_exportCancel != null || _exportDialog != null || ExportClips().Count == 0) return;
        StopPlay();
        ShowSimpleExportSummary();
        ShowOutputFolder();
        ExportName.IsEnabled = true;
        var dialog = new Window { Title = "Export video", Width = 510, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush.Parse("#242426"), FontSize = 12 };
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "Cancel", Width = 100, Height = 30, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var go = new Button { Content = "Export", Width = 100, Height = 30, Background = Brush.Parse("#0A84FF"), Foreground = Brushes.White, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        bool confirmed = false;
        cancel.Click += (_, _) => dialog.Close();
        go.Click += (_, _) => { confirmed = true; dialog.Close(); };
        buttons.Children.Add(cancel); buttons.Children.Add(go);
        ExportSettingsHost.Child = null;
        ExportDetails.IsVisible = true;
        panel.Children.Add(ExportDetails); panel.Children.Add(buttons); dialog.Content = panel;
        _exportDialog = dialog;
        try { await dialog.ShowDialog(this); }
        finally { panel.Children.Remove(ExportDetails); ExportSettingsHost.Child = ExportDetails; _exportDialog = null; }
        if (confirmed) await ExportAsync(ExportClips().Select(c => c.Copy()).ToList(), SuffixBox.Text?.Trim() ?? "");
    }

    /// <summary>One click: settings from the measurement, then the original is exported again as a new file.</summary>
    private async void AutoRefine_Click(object? sender, RoutedEventArgs e)
    {
        if (_exportCancel != null) return;
        if (_reviewClip == null || !_queue.Contains(_reviewClip)) { ReviewNote.Text = "The clip is no longer in the queue."; return; }
        if (_assessedHeadroom < 0) { ReviewNote.Text = "The measurement is still running; try again in a moment."; return; }
        StopReviewPlayback();
        var (settings, explanation) = SuggestSettings(secondPass: false);
        ApplyRepair(settings);
        RememberSettings();
        ExportStatus.Text = $"Auto refine: {explanation}";
        ReviewNote.Text = "Auto refine is exporting the original again with the suggested settings; the result replaces this view when it is done.";
        await ExportAsync([_reviewClip], (SuffixBox.Text?.Trim() ?? "") + "-refined");
    }

    /// <summary>Progress shows right under the Export button; the settings are locked while it runs.</summary>
    private void ShowExportRunning(bool running)
    {
        ExportRunning.IsVisible = running;
        ExportIdle.IsVisible = !running;
        ExportButton.IsVisible = !running;
        CancelExportButton.IsVisible = running;
        QueueCounter.IsEnabled = !running;
        ExportButton.IsEnabled = !running && ExportClips().Count > 0;
        AddSectionButton.IsEnabled = !running && _selected != null;
        ExportDetails.IsEnabled = !running;
        ExportFoldButton.IsEnabled = !running;
        CancelExportButton.IsEnabled = running;
        AutoRefineButton.IsEnabled = !running;
        ShareButton.IsEnabled = !running;
    }

    private async Task ExportAsync(List<FootageItem> clips, string suffix)
    {
        if (_exportCancel != null) return;
        if (clips.Count == 0) { ExportStatus.Text = "Open a video first."; return; }
        var folder = OutputFolderPath;
        if (string.IsNullOrWhiteSpace(folder)) { ExportStatus.Text = "Choose a destination folder."; return; }
        if (Mode == "model" && !File.Exists(ModelPath.Text)) { ExportStatus.Text = "Choose a trained model under Repair → Advanced."; return; }
        if (suffix.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) { ExportStatus.Text = "The file name suffix contains characters that are not allowed."; return; }
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ExportStatus.Text = $"Could not create the folder: {ex.Message}"; return; }
        ShowOutputFolder();
        RememberSettings();
        var join = false;
        SimpleExportResult.IsVisible = false;
        ExportIdle.IsVisible = true;
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<string>();
        // The file extension follows the chosen format; the engine checks that they match.
        var extension = OutputFormat.Extension;
        var joined = System.IO.Path.Combine(folder, System.IO.Path.GetFileNameWithoutExtension(clips[0].Path) + suffix + "-joined" + extension);
        var customName = ExportName.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(customName) && customName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            customName = customName[..^extension.Length].Trim();
        if (string.IsNullOrWhiteSpace(customName)) customName = System.IO.Path.GetFileNameWithoutExtension(clips[0].Path) + suffix;
        if (customName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || customName.Contains('\\') || customName.Contains('/') || customName.StartsWith('.'))
        { ExportStatus.Text = "Invalid output filename."; return; }
        for (var i = 0; i < clips.Count; i++)
        {
            var stem = customName + (clips.Count > 1 ? $"_{i + 1}" : "");
            var target = System.IO.Path.Combine(folder, stem + extension);
            for (var number = 2; File.Exists(target) || !usedTargets.Add(target); number++)
                target = System.IO.Path.Combine(folder, $"{stem}-{number}{extension}");
            targets.Add(target);
        }
        // The settings are fixed when the export starts: the pages stay usable while it runs.
        var recipes = clips.Select(c => RecipeOf(c.QueuedRepair ?? CurrentRepair(), c, c.QueuedPreserveOsd)).ToList();
        var mode = Mode;
        var model = ModelPath.Text;
        var quality = (int)(QualityValue.Value ?? 18);
        var signal = IsAnalog ? "analog" : "digital";
        // Remaining time is weighted by cut length, so long clips count for more.
        var total = Math.Max(0.001, clips.Sum(clip => clip.Kept));
        _exportCancel = new CancellationTokenSource();
        _exportStarted = DateTime.UtcNow; _exportDone = 0;
        ExportProgress.Value = 0; ExportTime.Text = UiText.Estimating; ExportRunText.Text = UiText.ExportStarting;
        ShowExportRunning(true);
        _exportClock.Start();
        var finished = 0.0;
        var completedCount = 0;
        var verified = new List<(FootageItem Clip, string Target)>();
        _lastExportCount = 0;
        try
        {
            for (var i = 0; i < clips.Count; i++)
            {
                var clip = clips[i];
                var target = targets[i];
                ExportStatus.Foreground = Brush.Parse("#B4B0AA");
                ExportStatus.Text = "";
                ExportRunText.Text = clips.Count == 1 ? $"{System.IO.Path.GetFileName(target)}" : $"{i + 1}/{clips.Count}: {System.IO.Path.GetFileName(target)}";
                ExportHint.Text = $"Rendering {i + 1}/{clips.Count}";
                var before = finished;
                var share = clip.Kept / total;
                var sampleClock = Stopwatch.StartNew();
                var sampleKey = EstimateKey(clip);
                try
                {
                    await EngineAsync(new { action = "export", source = clip.Path, target, recipe = recipes[i], mode, model,
                            segments = clip.Segments.Select(part => new[] { part.In, part.Out }).ToList() },
                        item =>
                        {
                            var done = before + share * item.GetProperty("fraction").GetDouble();
                            Dispatcher.UIThread.Post(() => { _exportDone = done; ExportProgress.Value = 100 * done; });
                        }, _exportCancel.Token);
                }
                catch (OperationCanceledException)
                {
                    RemovePartialOutput(target);
                    if (join) foreach (var part in targets) { try { File.Delete(part); } catch (IOException) { } }
                    ExportStatus.Text = $"Export canceled. {i} of {clips.Count} clip(s) finished.";
                    ExportHint.Text = "Canceled";
                    return;
                }
                await VerifyVisibleOutput(target);
                sampleClock.Stop();
                if (clip.Kept > 0 && File.Exists(target))
                    _exportSamples[sampleKey] = (sampleClock.Elapsed.TotalSeconds / clip.Kept, new FileInfo(target).Length / clip.Kept);
                finished = before + share;
                _exportDone = finished;
                StopReviewPlayback();
                _reviewSource = clip.Path; _reviewOutput = target; _reviewSegments = clip.Segments.ToList(); _reviewClip = clip;
                try
                {
                    var made = await EngineAsync(new { action = "inspect", source = target });
                    _reviewOutInfo = (made.GetProperty("width").GetInt32(), made.GetProperty("height").GetInt32(),
                        made.TryGetProperty("fps", out var fps) ? fps.GetDouble() : clip.Fps);
                }
                catch (Exception ex) { throw new IOException("Export could not be read", ex); }
                ReviewTitle.Text = $"{clip} · original and cleaned result";
                _reviewSeeking = true; ReviewSeek.Maximum = clip.Kept; ReviewSeek.Value = 0; _reviewSeeking = false;
                ReviewPanel.IsVisible = false;

                SimpleOutputPath.Text = target;
                verified.Add((clip, target));
                completedCount++;
                Dispatcher.UIThread.Post(() => { _reviewSeeking = true; ReviewSeek.Value = 0; _reviewSeeking = false; });

            }
            if (join)
            {
                ExportRunText.Text = "Joining the clips into one video…";
                try
                {
                    await EngineAsync(new { action = "merge", sources = targets, target = joined }, null, _exportCancel.Token);
                    foreach (var part in targets) { try { File.Delete(part); } catch (IOException) { } }
                    _reviewSource = null; _reviewOutput = null; ReviewPanel.IsVisible = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The clips stay as separate files, made visible again.
                    foreach (var part in targets)
                    {
                        var visible = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(part).TrimStart('.'));
                        try { File.Move(part, visible, overwrite: true); } catch (IOException) { }
                    }
                    ExportStatus.Text = $"Exported {clips.Count} clip(s), but joining failed: {ex.Message} The clips were saved separately.";
                    ExportHint.Text = "Done";
                    FinishExport(folder);
                    return;
                }
            }
            ExportProgress.Value = 100;
            ExportStatus.Foreground = Brush.Parse("#34C759");
            ExportStatus.Text = join
                ? $"Exported {clips.Count} clip(s) joined into {System.IO.Path.GetFileName(joined)} in {Duration(DateTime.UtcNow - _exportStarted)}."
                : $"Export complete · {completedCount}/{clips.Count} files · {Duration(DateTime.UtcNow - _exportStarted)}";
            ExportHint.Text = "Done";
            SimpleExportResult.IsVisible = true;
            FinishExport(folder);
        }
        catch (Exception ex) { ExportStatus.Foreground = Brush.Parse("#FF7777"); ExportStatus.Text = $"Export failed · {completedCount}/{clips.Count} files: {ex.Message}"; ExportHint.Text = "Failed"; }
        finally
        {
            foreach (var output in verified)
            {
                if (output.Clip.QueueId != Guid.Empty) _exportQueue.RemoveAll(q => q.QueueId == output.Clip.QueueId);
                _lastExportCount++;
            }
            _reviewOutput = verified.Count > 0 ? verified[^1].Target : null;
            PlayExportButton.IsVisible = _lastExportCount == 1;
            SimpleExportResult.IsVisible = _lastExportCount > 0;
            UpdateExportSummary();
            _exportClock.Stop(); ExportTime.Text = "";
            _exportCancel.Dispose(); _exportCancel = null;
            ShowExportRunning(false);
            ShowOutputFolder();
        }
    }

    private int _lastExportCount;
    private static async Task VerifyVisibleOutput(string target)
    {
        if (!File.Exists(target) || new FileInfo(target).Length == 0) throw new IOException($"Missing output: {target}");
        if (OperatingSystem.IsMacOS())
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/chflags") { ArgumentList = { "nohidden", target }, UseShellExecute = false });
            if (process == null) throw new IOException("Could not verify Finder visibility");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException($"Could not make output visible: {target}");
        }
    }

    /// <summary>After an export the settings fold into one line, so the result view gets the room.</summary>
    private void ExportFold_Click(object? sender, RoutedEventArgs e) => FoldExportSettings(ExportDetails.IsVisible);

    private void FoldExportSettings(bool fold)
    {
        UpdateExportSummary();
        ExportDetails.IsVisible = !fold;
        ExportActions.IsVisible = !fold;
        ExportFolded.IsVisible = ReviewPanel.IsVisible;
        ExportFoldButton.Content = fold ? UiText.ShowSettings : UiText.HideSettings;
        ExportFoldedText.IsVisible = fold;
    }

    /// <summary>Exports take minutes: bring the window back and open the folder if wanted.</summary>
    private void FinishExport(string folder)
    {
        ShowSimpleExportSummary();
        if (ReviewPanel.IsVisible) FoldExportSettings(true);
        try { Activate(); } catch { /* Not every window manager allows it. */ }

    }

    private void CancelExport_Click(object? sender, RoutedEventArgs e)
    {
        if (_exportCancel == null) return;
        CancelExportButton.IsEnabled = false;
        ExportRunText.Text = "Canceling…";
        _exportCancel.Cancel();
    }

    private void ShowExportTime()
    {
        var elapsed = DateTime.UtcNow - _exportStarted;
        // The first seconds go to OSD search and decoder start-up; wait for real
        // progress before projecting a finishing time.
        if (_exportDone < 0.005 || elapsed.TotalSeconds < 3)
        { ExportTime.Text = $"Elapsed {Duration(elapsed)} · estimating remaining time…"; return; }
        var remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - _exportDone) / _exportDone);
        ExportTime.Text = $"{100 * _exportDone:0}% · about {Duration(remaining)} left · elapsed {Duration(elapsed)}";
    }

    private static void RemovePartialOutput(string target)
    {
        // The engine renders into ".<name>-<random>.<ext>" next to the target and
        // only renames it on success; a stopped engine leaves that file behind.
        var folder = System.IO.Path.GetDirectoryName(target);
        if (folder == null || !Directory.Exists(folder)) return;
        var pattern = $".{System.IO.Path.GetFileNameWithoutExtension(target)}-*{System.IO.Path.GetExtension(target)}";
        foreach (var leftover in Directory.EnumerateFiles(folder, pattern))
        {
            try { File.Delete(leftover); }
            catch (IOException) { /* Still locked by the closing encoder; harmless. */ }
        }
    }

    private async Task RefreshReviewAsync()
    {
        if (_loadingReview || _reviewSource == null || _reviewOutput == null) return;
        _loadingReview = true;
        var before = TempFile("review-before");
        var after = TempFile("review-after");
        try
        {
            var moment = ReviewSeek.Value;

            await Task.WhenAll(
                EngineAsync(new { action = "frame", source = _reviewSource, seconds = SourceTimeOf(moment), target = before,
                    display_aspect = true, crop = (int[]?)null }),
                EngineAsync(new { action = "frame", source = _reviewOutput, seconds = moment, target = after,
                    display_aspect = true, crop = (int[]?)null }));
            var original = LoadBitmap(before);
            var cleaned = LoadBitmap(after);
            _reviewAspect = (double)cleaned.PixelSize.Width / Math.Max(1, cleaned.PixelSize.Height);
            BeforeImage.Source = original; SideBefore.Source = original;
            AfterImage.Source = cleaned; SideAfter.Source = cleaned;
            ReviewTime.Text = Clock(moment);
            UpdateWipe();
        }
        catch (Exception ex) { ExportStatus.Text = $"Review frame unavailable: {ex.Message}"; }
        finally { File.Delete(before); File.Delete(after); _loadingReview = false; }
    }

    /// <summary>The source moment that ended up at this point of the exported file.</summary>
    private double SourceTimeOf(double outputSeconds)
    {
        var remaining = outputSeconds;
        foreach (var part in _reviewSegments)
        {
            var length = part.Out - part.In;
            if (remaining <= length) return part.In + remaining;
            remaining -= length;
        }
        return _reviewSegments.Count > 0 ? _reviewSegments[^1].Out : outputSeconds;
    }

    private async void ReviewSeek_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_reviewSeeking || _reviewSource == null) return;
        if (_reviewPlaying) { StartReviewPlayback(ReviewSeek.Value); return; }
        await RefreshReviewAsync();
    }

    // ---- live comparison: original and cleaned clip play in sync

    private void ReviewPlay_Click(object? sender, RoutedEventArgs e)
    {
        if (_reviewPlaying) StopReviewPlayback();
        else StartReviewPlayback(ReviewSeek.Value >= ReviewSeek.Maximum - 0.05 ? 0 : ReviewSeek.Value);
    }

    private void StartReviewPlayback(double outputSeconds)
    {
        if (_reviewSource == null || _reviewOutput == null || _reviewClip == null) return;
        _reviewPlaying = true;
        ReviewPlayButton.Content = "⏸ Pause";
        try
        {
            var source = SourceTimeOf(outputSeconds);
            _reviewSegmentPlaying = SegmentIndexOf(source);
            _reviewPairOffset = outputSeconds - source;
            // One decoder for both clips keeps them frame-accurate; the original side
            // is restarted at the next kept part when the cleaned clip crosses a cut.
            _reviewPair.PlayPair(_reviewOutput, outputSeconds, _reviewSource, source, _reviewOutInfo.Fps,
                _reviewOutInfo.Width, _reviewOutInfo.Height);
            _reviewAspect = DisplayAspect(_reviewClip);
        }
        catch (Exception ex) { StopReviewPlayback(); ReviewNote.Text = $"Playback unavailable: {ex.Message}"; }
    }

    private double _reviewPairOffset;

    private void StopReviewPlayback()
    {
        if (!_reviewPlaying) return;
        _reviewPlaying = false;
        _reviewPair.Stop();
        ReviewPlayButton.Content = "▶ Play both";
    }

    private int SegmentIndexOf(double sourceSeconds)
    {
        for (var i = 0; i < _reviewSegments.Count; i++)
            if (sourceSeconds >= _reviewSegments[i].In - 1e-6 && sourceSeconds <= _reviewSegments[i].Out + 1e-6) return i;
        return -1;
    }

    private void OnReviewPair(WriteableBitmap cleaned, WriteableBitmap original, double moment)
    {
        if (!_reviewPlaying) return;
        ShowReviewBitmap(cleaned, before: false);
        ShowReviewBitmap(original, before: true);
        _reviewSeeking = true; ReviewSeek.Value = Math.Min(moment, ReviewSeek.Maximum); _reviewSeeking = false;
        ReviewTime.Text = Clock(moment);
        // The original side runs straight on; when the cleaned clip crosses a cut, both restart there.
        var index = SegmentIndexOf(SourceTimeOf(moment));
        if (index != _reviewSegmentPlaying && index >= 0) StartReviewPlayback(moment);
    }

    private void ShowReviewBitmap(WriteableBitmap bitmap, bool before)
    {
        var wipe = before ? BeforeImage : AfterImage;
        var side = before ? SideBefore : SideAfter;
        if (!ReferenceEquals(wipe.Source, bitmap)) { wipe.Source = bitmap; side.Source = bitmap; }
        wipe.InvalidateVisual(); side.InvalidateVisual();
    }

    /// <summary>Measures artefacts in original and result: what was gained, what is left, what a re-encode costs.</summary>
    private async Task AssessAsync(FootageItem clip, string target, int quality, string signal)
    {
        _assessedHeadroom = -1;
        GainLabel.Text = HeadroomLabel.Text = LossLabel.Text = "…";
        ReviewAssessment.Text = "Measuring grain, colour noise and streaks in original and result…";
        try
        {
            var report = await EngineAsync(new { action = "assess", source = clip.Path, target,
                segments = clip.Segments.Select(part => new[] { part.In, part.Out }).ToList(),
                quality, signal });
            if (_reviewOutput != target) return;
            _assessedGain = report.GetProperty("gain_percent").GetDouble();
            _assessedHeadroom = report.GetProperty("headroom_percent").GetDouble();
            _assessedLoss = report.GetProperty("reencode_loss_percent").GetDouble();
            _secondPassWorth = report.GetProperty("second_pass_worth").GetBoolean();
            _assessedHeadrooms = report.GetProperty("headrooms").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetDouble());
            GainLabel.Text = $"{_assessedGain:0} %";
            HeadroomLabel.Text = $"~{_assessedHeadroom:0} %";
            LossLabel.Text = $"−{_assessedLoss:0.#} % detail";
            var gains = report.GetProperty("gains");
            var parts = new List<string>();
            foreach (var item in gains.EnumerateObject())
                parts.Add($"{item.Name switch { "grain" => "grain", "colour" => "colour noise", "streaks" => "streaks", _ => "block seams" }} −{item.Value.GetDouble():0} %");
            var advice = _assessedHeadroom < 5
                ? "The result is close to what the footage allows. A second pass would mostly cost detail."
                : _secondPassWorth
                    ? $"Worth another go: raise noise reduction or colour repair under Try other settings (no loss), or run the cleaned file again (about {_assessedLoss:0.#} % detail lost per pass)."
                    : "Small headroom left. Prefer Try other settings: it re-exports the original and loses nothing.";
            ReviewAssessment.Text = $"Artefacts removed so far: {string.Join(", ", parts)}. {advice} Estimates from {16} sampled frames.";
        }
        catch (Exception ex)
        {
            GainLabel.Text = HeadroomLabel.Text = LossLabel.Text = "—";
            ReviewAssessment.Text = $"Could not measure the result: {ex.Message}";
        }
    }

    private Dictionary<string, double> _assessedHeadrooms = new();

    /// <summary>
    /// Settings for the next round, derived from what the measurement found still
    /// missing: each remaining artefact raises the control that fights it. A second
    /// pass on the cleaned file also keeps the encode near lossless and skips what
    /// was already done (OSD, upscaling).
    /// </summary>
    private (RepairPreset Settings, string Explanation) SuggestSettings(bool secondPass)
    {
        var current = CurrentRepair();
        var next = current.Clone();
        var reasons = new List<string>();
        double Left(string key) => _assessedHeadrooms.TryGetValue(key, out var value) ? value : 0;
        static int Raise(int value, double headroom, int ceiling = 100) =>
            (int)Math.Clamp(value + Math.Round(headroom * 0.6), 0, ceiling);
        // Film grain added by the look is wanted; it is not noise left to remove.
        var grain = current.Look && current.LookGrain > 0 ? 0 : Left("grain");
        if (grain > 5 || Left("streaks") > 5)
        {
            next.Noise = Raise(current.Noise, Math.Max(grain, Left("streaks")));
            if (next.Noise != current.Noise) reasons.Add($"noise reduction {current.Noise} → {next.Noise}");
        }
        if (current.Signal == "analog" && Left("colour") > 5)
        {
            next.Colour = Raise(current.Colour, Left("colour"));
            if (next.Colour != current.Colour) reasons.Add($"colour repair {current.Colour} → {next.Colour}");
        }
        if (current.Signal == "digital" && Left("seams") > 5)
        {
            next.Block = Raise(current.Block, Left("seams"));
            if (next.Block != current.Block) reasons.Add($"block repair {current.Block} → {next.Block}");
        }
        if (_assessedHeadroom >= 15 && current.Effort != "best") { next.Effort = "best"; reasons.Add("speed → Best"); }
        if (secondPass)
        {
            // The first pass already handled these; doing them twice only blurs.
            if (current.Osd) { next.Osd = false; reasons.Add("OSD removal off (already done)"); }
            if (current.Size != 100) { next.Size = 100; reasons.Add("size → original (already upscaled)"); }
            if (current.Look) { next.Look = false; reasons.Add("look off (already applied)"); }
            if (current.Signal == "analog" && Left("colour") <= 5 && current.Colour > 20) { next.Colour = 20; reasons.Add("colour repair → 20 (colour is clean already)"); }
            next.Detail = Math.Min(current.Detail, 10);
            next.Quality = Math.Min(current.Quality, 15); reasons.Add($"quality → {next.Quality} to keep the loss small");
        }
        var explanation = reasons.Count == 0 ? "The settings already match what is left; nothing changed."
            : "Settings chosen from the measurement: " + string.Join(", ", reasons) + ".";
        return (next, explanation);
    }

    /// <summary>Not happy yet: change the settings and export the original again, lossless.</summary>
    private void TryAgain_Click(object? sender, RoutedEventArgs e)
    {
        StopReviewPlayback();
        if (_reviewClip != null && _queue.Contains(_reviewClip)) Select(_reviewClip);
        ShowPage(2);
        if (_assessedHeadroom >= 0)
        {
            var (settings, explanation) = SuggestSettings(secondPass: false);
            ApplyRepair(settings);
            PreviewStatus.Text = $"{explanation} Render a frame to check, then export again: the original is used, so nothing is lost.";
        }
        else PreviewStatus.Text = "Adjust the settings and render a frame to check; then export again. The original is used, so nothing is lost.";
    }

    /// <summary>A second pass over the cleaned file, for repairs that build on the first result.</summary>
    private async void Refine_Click(object? sender, RoutedEventArgs e)
    {
        if (_reviewOutput == null || !File.Exists(_reviewOutput)) { ReviewNote.Text = "The cleaned file is no longer there."; return; }
        StopReviewPlayback();
        if (_assessedHeadroom >= 0 && !_secondPassWorth)
        {
            // Say what the second pass costs and what it can bring before anything is queued.
            ReviewNote.Text = $"Heads-up: about {_assessedHeadroom:0} % improvement is still possible, but every pass loses about {_assessedLoss:0.#} % detail to re-encoding. " +
                              "Try other settings keeps the original and picks matching settings for you. Press Refine again to go ahead anyway.";
            _assessedHeadroom = -1;
            return;
        }
        var output = _reviewOutput;
        await AddFilesAsync([output]);
        var added = _queue.FirstOrDefault(clip => clip.Path.Equals(output, StringComparison.OrdinalIgnoreCase));
        if (added == null) return;
        Select(added);
        ShowPage(2);
        var (settings, explanation) = SuggestSettings(secondPass: true);
        ApplyRepair(settings);
        PreviewStatus.Text = $"Second pass on the cleaned file. {explanation} Every re-encode costs about {Math.Max(0.5, (settings.Quality - 12) * 0.6):0.#} % detail, so prefer Try other settings for colour fixes.";
    }

    private void ReviewMode_Changed(object? sender, RoutedEventArgs e)
    {
        if (SideBySideView == null || WipeView == null || WipeSlider == null) return;
        var wipe = ReviewWipeMode.IsChecked == true;
        SideBySideView.IsVisible = !wipe;
        WipeView.IsVisible = wipe;
        UpdateWipe();
    }
    private void Wipe_Changed(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e) => UpdateWipe();

    private bool _wipeDragging;
    private void SetWipeFromPointer(double x)
    {
        var shown = ImageRect(WipeView, _reviewAspect);
        WipeSlider.Value = Math.Clamp((x - shown.X) / Math.Max(1, shown.Width) * 100, 0, 100);
    }
    private void Wipe_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(WipeView).Properties.IsLeftButtonPressed) return;
        _wipeDragging = true; e.Pointer.Capture(WipeView);
        SetWipeFromPointer(e.GetPosition(WipeView).X);
        e.Handled = true;
    }
    private void Wipe_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_wipeDragging) SetWipeFromPointer(e.GetPosition(WipeView).X);
    }
    private void Wipe_PointerReleased(object? sender, PointerReleasedEventArgs e) { _wipeDragging = false; e.Pointer.Capture(null); }
    private void Review_SizeChanged(object? sender, SizeChangedEventArgs e) => UpdateWipe();

    private void UpdateWipe()
    {
        if (ReviewCanvas == null || AfterImage == null || Divider == null || WipeSlider == null) return;
        var shown = ImageRect(ReviewCanvas, _reviewAspect);
        var width = ReviewCanvas.Bounds.Width;
        var edge = shown.X + shown.Width * WipeSlider.Value / 100;
        AfterImage.Clip = new RectangleGeometry(new Rect(edge, 0, Math.Max(0, width - edge), ReviewCanvas.Bounds.Height));
        Divider.Margin = new Thickness(edge - 1, 0, 0, 0);
        DividerGrip.Margin = new Thickness(edge - 13, 0, 0, 0);
    }

    // ---------------------------------------------------------------- share video

    private bool _fillingShare;
    private string? _shareMade;

    private ShareChoices ShareMemory => _settings.Share ??= new ShareChoices();
    private bool ShareIsVertical => ShareVertical.IsChecked == true;

    /// <summary>Opens the share card with the last choices, ready for one click on Make video.</summary>
    private void Share_Click(object? sender, RoutedEventArgs e)
    {
        if (_reviewOutput == null || !File.Exists(_reviewOutput))
        { ReviewNote.Text = "The cleaned file is no longer there."; return; }
        StopReviewPlayback();
        _fillingShare = true;
        var memory = ShareMemory;
        ShareVertical.IsChecked = memory.Format == "vertical";
        ShareLandscape.IsChecked = memory.Format != "vertical";
        _fillingShare = false;
        ShowShareSound();
        _shareMade = null;
        ShareFolderButton.IsVisible = false;
        ShareStatus.Text = $"Saved next to the cleaned file as {System.IO.Path.GetFileName(ShareTarget())}.";
        ShareOverlay.IsVisible = true;
        PageScroller.Effect = new BlurEffect { Radius = 10 };
    }

    /// <summary>Sound is remembered per format: on for 16:9, off for 9:16 unless changed.</summary>
    private void ShowShareSound()
    {
        _fillingShare = true;
        ShareSound.IsChecked = ShareIsVertical ? ShareMemory.SoundVertical : ShareMemory.SoundLandscape;
        _fillingShare = false;
    }

    private void ShareFormat_Changed(object? sender, RoutedEventArgs e)
    {
        if (_fillingShare || ShareSound == null || sender is RadioButton { IsChecked: not true }) return;
        ShowShareSound();
        RememberShare();
        ShareStatus.Text = $"Saved next to the cleaned file as {System.IO.Path.GetFileName(ShareTarget())}.";
    }

    private void ShareSound_Changed(object? sender, RoutedEventArgs e)
    {
        if (_fillingShare || ShareSound == null) return;
        RememberShare();
    }

    private void RememberShare()
    {
        var memory = ShareMemory;
        var vertical = ShareIsVertical;
        memory.Format = vertical ? "vertical" : "landscape";
        if (vertical) memory.SoundVertical = ShareSound.IsChecked == true;
        else memory.SoundLandscape = ShareSound.IsChecked == true;
        _settings.Save();
    }

    /// <summary>"name-youtube.mp4" or "name-short.mp4" next to the cleaned file, numbered if taken.</summary>
    private string ShareTarget()
    {
        var output = _reviewOutput ?? "";
        var folder = System.IO.Path.GetDirectoryName(output) ?? "";
        var stem = System.IO.Path.GetFileNameWithoutExtension(output) + (ShareIsVertical ? "-short" : "-youtube");
        var target = System.IO.Path.Combine(folder, stem + ".mp4");
        for (var number = 2; File.Exists(target); number++) target = System.IO.Path.Combine(folder, $"{stem}-{number}.mp4");
        return target;
    }

    private void ShareClose_Click(object? sender, RoutedEventArgs e) => CloseShare();

    private void CloseShare()
    {
        ShareOverlay.IsVisible = false;
        if (!BusyOverlay.IsVisible) PageScroller.Effect = null;
    }

    private void ShareFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folder = System.IO.Path.GetDirectoryName(_shareMade ?? _reviewOutput ?? "");
        if (folder == null || !Directory.Exists(folder)) return;
        try { OpenInFileManager(folder); }
        catch (Exception ex) { ShareStatus.Text = $"Could not open the folder: {ex.Message}"; }
    }

    private CancellationTokenSource? _shareCancel;

    private async void ShareMake_Click(object? sender, RoutedEventArgs e)
    {
        if (_shareCancel != null || _reviewOutput == null) return;
        if (!File.Exists(_reviewOutput)) { ShareStatus.Text = "The cleaned file is no longer there."; return; }
        RememberShare();
        var target = ShareTarget();
        var vertical = ShareIsVertical;
        _shareCancel = new CancellationTokenSource();
        var started = DateTime.UtcNow;
        ShareOverlay.IsVisible = false;
        ShowBusy(vertical ? "Making the 9:16 video" : "Making the 16:9 video",
            vertical ? "Cropping the cleaned clip to the phone screen, following the action." : "Placing the cleaned clip in a 16:9 frame.",
            () => _shareCancel?.Cancel());
        UpdateBusy(0);
        try
        {
            var result = await EngineAsync(new
            {
                action = "share", target = _reviewOutput, output = target,
                format = vertical ? "vertical" : "landscape", sound = ShareSound.IsChecked == true,
            }, item =>
            {
                var done = item.GetProperty("fraction").GetDouble();
                Dispatcher.UIThread.Post(() => UpdateBusy(done, null, $"elapsed {Duration(DateTime.UtcNow - started)}"));
            }, _shareCancel.Token);
            _shareMade = target;
            var seconds = result.GetProperty("duration").GetDouble();
            ShareStatus.Text = $"Saved {System.IO.Path.GetFileName(target)}: {seconds:0.0} s, {result.GetProperty("width").GetInt32()}×{result.GetProperty("height").GetInt32()}.";
            ShareFolderButton.IsVisible = true;
        }
        catch (OperationCanceledException)
        {
            RemovePartialOutput(target);
            ShareStatus.Text = "Canceled. Nothing was saved.";
        }
        catch (Exception ex) { ShareStatus.Text = $"The share video failed: {ex.Message}"; }
        finally
        {
            _shareCancel.Dispose(); _shareCancel = null;
            HideBusy();
            ShareOverlay.IsVisible = true;
            PageScroller.Effect = new BlurEffect { Radius = 10 };
        }
    }
}
