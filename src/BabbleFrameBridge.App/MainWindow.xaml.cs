// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BabbleFrameBridge.Shared;
using Microsoft.Win32;

namespace BabbleFrameBridge.App;

public partial class MainWindow : Window
{
    private readonly string _root;
    private readonly ModuleStore _store;
    private AppSettings _settings;
    private Process? _worker;
    private TaskCompletionSource<bool>? _ready;
    private bool _busy, _initialized, _closing, _allowClose, _running, _pendingRestart;
    private readonly string _logPath;
    private readonly object _fileLock = new();
    private int _restartCount;
    private DateTime _restartWindow = DateTime.UtcNow;
    private FrameworkElement? _currentPage;
    private bool _motionEnabled;

    public MainWindow(string root)
    {
        InitializeComponent();
        BuildLabel.Text = "INDEPENDENT BUILD  /  v" + typeof(App).Assembly.GetName().Version?.ToString(3);
        SelectPage("overview");
        _root = root;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        _logPath = Path.Combine(root, "logs", $"bridge-{DateTime.Now:yyyy-MM-dd}.log");
        _store = new ModuleStore(root);
        _settings = AppSettings.Load(root);
        if (!File.Exists(Path.Combine(root, "settings.json"))) { _settings.AutoStart = true; _settings.Save(root); }
        HostBox.Text = _settings.OutputHost;
        PortBox.Text = _settings.OutputPort.ToString();
        ReceiveBox.Text = _settings.ReceivePort.ToString();
        DesktopSourceButton.IsChecked = DesktopSource;
        FrameSourceButton.IsChecked = !DesktopSource;
        UpdateSourceLabels();
        AutoStartBox.IsChecked = _settings.AutoStart;
        ApplyMotionPreference();
        SystemParameters.StaticPropertyChanged += MotionPreference_Changed;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= MotionPreference_Changed;
        Loaded += async (_, _) => await RunOperation(async () => {
            _initialized = true;
            Motion.Transition(_currentPage ?? OverviewPage, _motionEnabled);
            RefreshModule();
            TargetLabel.Text = $"{_settings.OutputHost} : {_settings.OutputPort}";
            if (_store.Current is { } current)
            {
                SetStoppedState();
                if (_settings.AutoStart) await StartWorker(current);
            }
            else
            {
                SetState("Add your tracking module", "Choose your Baballonia module ZIP to start tracking.", false);
                TrackingRoute.Text = "Add a module to begin";
                Log("No tracking module installed. Choose your Baballonia module ZIP or drop it onto this window.");
            }
        });
        Closing += ClosingAsync;
    }

    private async Task RunOperation(Func<Task> action)
    {
        if (_busy || _closing) return;
        GlobalNotice.Visibility = Visibility.Collapsed;
        _busy = true; RefreshButtons();
        try { await action(); }
        catch (Exception ex) { Log("ERROR: " + ex.Message); SetState("Needs attention", ex.Message, false, true); ShowNotice(ex.Message, true); }
        finally { _busy = false; RefreshButtons(); if (_pendingRestart && !_closing) { _pendingRestart = false; _ = RestartForHeadset(); } }
    }

    private void RefreshButtons()
    {
        StartButton.IsEnabled = !_busy && _initialized && (_running || _store.Current is not null);
        ImportButton.IsEnabled = !_busy && _initialized;
        RollbackButton.IsEnabled = !_busy && _store.Previous is not null;
        SaveButton.IsEnabled = !_busy && _initialized;
        StartButton.Content = _running ? "Stop bridge" : "Start bridge";
    }

    private void RefreshModule()
    {
        var current = _store.Current;
        ModuleLabel.Text = current is null ? "No module installed" : $"{current.ModuleName}  /  {current.Version}";
        HashLabel.Text = current is null ? "" : $"SHA-256  {current.Sha256[..Math.Min(24, current.Sha256.Length)].ToLowerInvariant()}…";
        ImportButton.Content = current is null ? "Add module ZIP  ↑" : "Import module ZIP  ↑";
        PackageDetail.Text = current is null
            ? "Provide your own Baballonia ZIP. Choose a file or drop it onto this window."
            : _store.Previous is null ? "Ready for updates. Import a new ZIP or drop it onto this window."
            : $"Previous version {_store.Previous.Version} is saved and ready to restore.";
        RefreshButtons();
    }

    private void SetState(string title, string detail, bool live, bool error = false)
    {
        StateTitle.Text = title; StateDetail.Text = detail;
        StateDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#F4C2C2" : live ? "#BDE5D1" : "#A6C5B8"));
    }

    private bool DesktopSource => _settings.InputSource == TrackingInputSource.BaballoniaDesktop;

    private void UpdateSourceLabels()
    {
        HeadsetState.Text = DesktopSource ? "Baballonia · stopped" : "Steam Frame · waiting";
        ModeLabel.Text = DesktopSource ? "Eye and face inputs enabled." : "Eyes and face follow your headset.";
    }

    private void SetStoppedState()
    {
        UpdateSourceLabels();
        if (_store.Current is null) SetState("Add your tracking module", "Choose your Baballonia module ZIP to start tracking.", false);
        else SetState("Bridge stopped", DesktopSource
            ? "Start the bridge to accept tracking from Baballonia on this computer."
            : "Start the bridge to listen for your Steam Frame.", false);
    }

    private void SetListeningState() => SetState(DesktopSource ? "Baballonia desktop input" : "Listening for Steam Frame", DesktopSource
        ? "Send Baballonia’s VRCFaceTracking output to this computer on port 8888."
        : "Start Babble Frame Runtime on the headset. Discovery is automatic.", true);

    private void ShowNotice(string message, bool error = false)
    {
        GlobalNoticeText.Text = message;
        GlobalNotice.SetResourceReference(Border.BackgroundProperty, error ? "ErrorBackground" : "NoticeBackground");
        GlobalNotice.SetResourceReference(Border.BorderBrushProperty, error ? "ErrorBorder" : "NoticeBorder");
        GlobalNoticeText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorInk" : "NoticeInk");
        GlobalNotice.Visibility = Visibility.Visible;
        Motion.Transition(GlobalNotice, _motionEnabled);
    }

    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (_fileLock)
        {
            try {
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > 8_000_000) File.Move(_logPath, _logPath + ".previous", true);
                File.AppendAllText(_logPath, line + Environment.NewLine);
            } catch (IOException) { }
        }
        LogBox.AppendText(line + Environment.NewLine);
        LatestActivity.Text = message;
        if (LogBox.Text.Length > 35000) LogBox.Text = LogBox.Text[^25000..];
        LogBox.ScrollToEnd();
    }

    private async Task StartWorker(ModuleRelease release)
    {
        if (_closing) throw new OperationCanceledException("The app is closing.");
        if (_worker is not null) await StopWorker();
        var executable = Path.Combine(AppContext.BaseDirectory, "runtime", "BabbleFrameBridge.Worker.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("The runtime folder is missing. Extract the entire portable app ZIP before starting.", executable);
        var start = new ProcessStartInfo(executable) {
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        start.ArgumentList.Add("--module"); start.ArgumentList.Add(Path.Combine(release.DirectoryPath, release.DllFileName));
        start.ArgumentList.Add("--data"); start.ArgumentList.Add(_root);
        start.ArgumentList.Add("--parent"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _worker = process;
        process.OutputDataReceived += (_, e) => {
            if (e.Data is null) return;
            Dispatcher.BeginInvoke(new Action(() => { if (_worker == process) HandleWorkerLine(e.Data); }));
        };
        process.ErrorDataReceived += (_, e) => {
            if (!string.IsNullOrWhiteSpace(e.Data)) Dispatcher.BeginInvoke(new Action(() => { if (_worker == process) Log(e.Data); }));
        };
        process.Exited += (_, _) => Dispatcher.BeginInvoke(new Action(() => {
            if (_worker != process) return;
            _running = false;
            _ready?.TrySetException(new InvalidOperationException("The tracking runtime exited. Check the activity log for details."));
            SetState("Runtime stopped", "Check the activity log, then start the bridge again.", false, true);
            ShowNotice("The runtime stopped. Check the activity log, then start the bridge again.", true);
            HeadsetState.Text = DesktopSource ? "Baballonia · stopped" : "Steam Frame · disconnected";
            OutputRate.Text = "0 msgs/s"; RefreshButtons();
        }));
        SetState("Starting bridge", $"Loading Baballonia {release.Version}…", false);
        Log($"Starting module {release.Version}.");
        try
        {
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(35));
            if (process.HasExited) throw new InvalidOperationException("The runtime exited immediately after loading. Check the activity log.");
            if (_closing) throw new OperationCanceledException("The app is closing.");
            _running = true;
            SetListeningState();
            RefreshButtons();
        }
        catch { await StopWorker(); throw; }
    }

    private async Task StopWorker()
    {
        var process = _worker;
        _worker = null; _running = false;
        if (_closing) _ready?.TrySetCanceled();
        if (process is not null)
        {
            try {
                if (!process.HasExited) {
                    try { await process.StandardInput.WriteLineAsync("stop"); await process.StandardInput.FlushAsync(); }
                    catch (Exception e) when (e is IOException or InvalidOperationException) { }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
                    catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync(); }
                }
            } catch (InvalidOperationException) { }
            finally { process.Dispose(); }
        }
        OutputRate.Text = "0 msgs/s";
        HeadsetState.Text = DesktopSource ? "Baballonia · stopped" : "Steam Frame · waiting";
        TrackingRoute.Text = "Bridge stopped";
        if (_store.Current is null)
            SetState("Add your tracking module", "Choose your Baballonia module ZIP to start tracking.", false);
        else
            SetStoppedState();
        RefreshButtons();
    }

    private static string Str(JsonElement e, string name, string fallback = "") => e.TryGetProperty(name, out var x) ? x.ToString() : fallback;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.True;
    private static double Number(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.TryGetDouble(out var value) && double.IsFinite(value) ? value : 0;

    private void HandleWorkerLine(string line)
    {
        try {
            using var document = JsonDocument.Parse(line);
            var item = document.RootElement;
            switch (Str(item, "type")) {
                case "ready": _ready?.TrySetResult(true); Log(DesktopSource
                    ? "Module loaded. Desktop Baballonia eye and face inputs are enabled."
                    : "Module loaded. Listening for the headset."); break;
                case "fatal":
                    var message = Str(item, "message", "Tracking runtime failed."); Log("ERROR: " + message);
                    _ready?.TrySetException(new InvalidOperationException(message));
                    SetState("Needs attention", message, false, true); break;
                case "log": Log(Str(item, "message")); break;
                case "restart":
                    Log(Str(item, "message", "The headset changed tracking mode. Restarting the module."));
                    _ = RestartForHeadset(); break;
                case "status": UpdateStatus(item); break;
                default: Log(line); break;
            }
        } catch (JsonException) { Log(line); }
    }

    private void UpdateStatus(JsonElement item)
    {
        var linked = Bool(item, "linked") || Bool(item, "connected") || Bool(item, "bfrConnected");
        var eye = Bool(item, "eye"); var face = Bool(item, "face");
        HeadsetState.Text = DesktopSource ? "Baballonia · input enabled" : linked ? "Steam Frame · connected" : "Steam Frame · waiting";
        ModeLabel.Text = $"Eyes {(eye ? "enabled" : "off")} · face {(face ? "enabled" : "off")}" + (DesktopSource ? " · desktop input" : " · selected by headset");
        OutputRate.Text = $"{Number(item, "rate"):N0} msgs/s";
        var avatar = Str(item, "avatar");
        AvatarLabel.Text = string.IsNullOrWhiteSpace(avatar) ? "Avatar · waiting for VRChat" : "Avatar · " + avatar;
        TrackingRoute.Text = Bool(item, "nativeFaceTracking") ? "Native face tracking · SDK beta"
            : Number(item, "parameters") > 0 ? "Avatar parameters · VRCFaceTracking" : "Waiting for avatar tracking support";
        var receiving = Bool(item, "receiving");
        var actualHost = Str(item, "outputHost", _settings.OutputHost);
        var actualPort = Number(item, "outputPort");
        TargetLabel.Text = $"{actualHost} : {(actualPort > 0 ? actualPort : _settings.OutputPort):0}";
        if (_running && !_busy)
        {
            if (DesktopSource)
            {
                var inputPort = Number(item, "inputPort");
                SetState("Baballonia desktop input", $"Send Baballonia’s VRCFaceTracking output to this computer on port {(inputPort > 0 ? inputPort : 8888):0}.", true);
            }
            else SetState(linked ? "Steam Frame connected" : "Listening for Steam Frame",
                receiving ? "Receiving expressions from Babble Frame Runtime." : linked ? "Connected. Waiting for expression data from the headset." : "Start Babble Frame Runtime on the headset. Discovery is automatic.", true);
        }
    }

    private async Task RestartForHeadset()
    {
        if (_closing) return;
        if (_busy) { _pendingRestart = true; return; }
        if (_store.Current is null) return;
        if (DateTime.UtcNow - _restartWindow > TimeSpan.FromMinutes(1)) { _restartWindow = DateTime.UtcNow; _restartCount = 0; }
        if (++_restartCount > 5) { Log("Tracking mode changed repeatedly. Restart the bridge when headset settings settle."); return; }
        await RunOperation(async () => { await StopWorker(); await StartWorker(_store.Current!); });
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunOperation(async () => {
        if (_running) await StopWorker();
        else if (_store.Current is { } current) await StartWorker(current);
        else ShowNotice("Add your Baballonia module ZIP before starting the bridge.");
    });

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Import Baballonia module ZIP", Filter = "Baballonia module ZIP (*.zip)|*.zip", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) await UpdateModule(picker.FileName);
    }

    private async Task UpdateModule(string zipPath) => await RunOperation(async () => {
        SetState("Checking module ZIP", "Validating the package before switching modules…", false);
        var release = await Task.Run(() => _store.Prepare(zipPath));
        if (_closing) return;
        await SwitchModule(release);
    });

    private async Task SwitchModule(ModuleRelease release)
    {
        var old = _store.Current;
        var wasRunning = _running;
        await StopWorker();
        try {
            await StartWorker(release);
            _store.Activate(release);
            RefreshModule();
            var activated = old is null ? $"Module {release.Version} is active. Your tracking source is ready."
                : $"Module {release.Version} is active. Your previous module is saved.";
            Log(activated);
            ShowNotice(activated);
        }
        catch (Exception failure) {
            await StopWorker();
            Log("The update could not start: " + failure.Message);
            if (wasRunning && old is not null) {
                try { await StartWorker(old); Log("Restored the previous active module."); }
                catch (Exception restoreFailure) { Log("Previous module could not restart: " + restoreFailure.Message); }
            }
            RefreshModule();
            throw new InvalidOperationException((old is null
                ? "The module could not start. No module was activated. "
                : "The new module could not start. Your previous module is still installed. ") + failure.Message);
        }
    }

    private async void Rollback_Click(object sender, RoutedEventArgs e) => await RunOperation(async () => { var previous = _store.Previous; if (previous is not null) await SwitchModule(previous); });

    private async void Save_Click(object sender, RoutedEventArgs e) => await RunOperation(async () => {
        if (!int.TryParse(PortBox.Text, out var port) || !int.TryParse(ReceiveBox.Text, out var receive)) throw new InvalidOperationException("Enter valid port numbers.");
        var updated = new AppSettings { OutputHost = HostBox.Text.Trim(), OutputPort = port, ReceivePort = receive,
            LinkPort = _settings.LinkPort, EyeEnabled = _settings.EyeEnabled, FaceEnabled = _settings.FaceEnabled,
            InputSource = DesktopSourceButton.IsChecked == true ? TrackingInputSource.BaballoniaDesktop : TrackingInputSource.SteamFrameRuntime,
            AutoStart = AutoStartBox.IsChecked == true };
        updated.Validate(); updated.Save(_root); _settings = updated;
        TargetLabel.Text = $"{updated.OutputHost} : {updated.OutputPort}";
        Log("Connection settings saved.");
        if (_running) { await StopWorker(); await StartWorker(_store.Current!); }
        else SetStoppedState();
        ShowNotice("Connection settings saved.");
    });

    private void AutoStart_Changed(object sender, RoutedEventArgs e) { if (!_initialized) return; _settings.AutoStart = AutoStartBox.IsChecked == true; _settings.Save(_root); }
    private void MotionPreference_Changed(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.BeginInvoke(new Action(ApplyMotionPreference));
    }

    private void ApplyMotionPreference() {
        _motionEnabled = SystemParameters.ClientAreaAnimation;
        Motion.Enabled = _motionEnabled;
        HeroParticles.AnimationsEnabled = _motionEnabled;
        if (!_motionEnabled) {
            foreach (var page in new FrameworkElement[] { OverviewPage, ModulesPage, SettingsPage, ActivityPage, GlobalNotice }) Motion.Transition(page, false);
        }
    }

    private void Hero_SizeChanged(object sender, SizeChangedEventArgs e) {
        if (sender is FrameworkElement surface && surface.ActualWidth > 0 && surface.ActualHeight > 0)
            surface.Clip = new RectangleGeometry(new Rect(0, 0, surface.ActualWidth, surface.ActualHeight), 21, 21);
    }
    private void Window_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Window_Drop(object sender, DragEventArgs e) {
        if (_busy || !_initialized) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1 && Path.GetExtension(files[0]).Equals(".zip", StringComparison.OrdinalIgnoreCase)) await UpdateModule(files[0]);
        else Log("Choose one Baballonia module ZIP to import.");
    }
    private void Logs_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", Path.Combine(_root, "logs")) { UseShellExecute = true });
    private void Guide_Click(object sender, RoutedEventArgs e) {
        var guide = Path.Combine(AppContext.BaseDirectory, "START-HERE.txt");
        if (File.Exists(guide)) Process.Start(new ProcessStartInfo(guide) { UseShellExecute = true });
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (OverviewPage is null || sender is not RadioButton navigation) return;
        SelectPage(navigation.Tag?.ToString() ?? "overview");
    }

    private void SelectPage(string page)
    {
        OverviewPage.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ModulesPage.Visibility = page == "modules" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = page == "activity" ? Visibility.Visible : Visibility.Collapsed;
        FrameworkElement selected = page switch { "modules" => ModulesPage, "settings" => SettingsPage, "activity" => ActivityPage, _ => OverviewPage };
        if (_currentPage != selected) { _currentPage = selected; Motion.Transition(selected, _motionEnabled); }
        (PageTitle.Text, PageSubtitle.Text) = page switch {
            "modules" => ("Module & updates", "A fresh module, whenever you’re ready."),
            "settings" => ("Connection", "Keep your expressions flowing."),
            "activity" => ("Activity log", "A clear view of your tracking session."),
            _ => ("Frame Bridge", "A little more you, in VR.")
        };
    }

    private void ShowActivity_Click(object sender, RoutedEventArgs e) { ActivityNav.IsChecked = true; }
    private void Attribution_Click(object sender, RoutedEventArgs e) {
        var notices = Path.Combine(AppContext.BaseDirectory, "NOTICE.txt");
        if (File.Exists(notices)) Process.Start(new ProcessStartInfo(notices) { UseShellExecute = true });
    }
    private void Website_Click(object sender, RoutedEventArgs e) => OpenLink("https://github.com/ClassicWasTaken/Steam-Frame-Babble-Bridge");
    private static void OpenLink(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private async void ClosingAsync(object? sender, CancelEventArgs e) {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; IsEnabled = false;
        await StopWorker();
        _allowClose = true;
        Dispatcher.BeginInvoke(new Action(Close));
    }
}
