// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using BabbleFrameBridge.Shared;
using Microsoft.Win32;

namespace BabbleFrameBridge.App;

public partial class MainWindow : Window
{
    private readonly string _root;
    private readonly ModuleStore _store;
    private readonly bool _useDiscovery;
    private readonly string _workerExecutable;
    private AppSettings _settings;
    private Process? _worker;
    private ModuleRelease? _sessionRelease;
    private TaskCompletionSource<bool>? _ready;
    private bool _busy, _initialized, _closing, _allowClose, _running, _pendingRestart;
    private readonly string _logPath;
    private readonly object _fileLock = new();
    private int _restartCount;
    private DateTime _restartWindow = DateTime.UtcNow;
    private FrameworkElement? _currentPage;
    private bool _motionEnabled;
    private readonly DispatcherTimer _noticeTimer;
    private bool _noticeFading;

    public MainWindow(string root, bool useDiscovery = true, string? workerExecutable = null)
    {
        InitializeComponent();
        _noticeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _noticeTimer.Tick += NoticeTimer_Tick;
        BuildLabel.Text = "INDEPENDENT BUILD  /  v" + typeof(App).Assembly.GetName().Version?.ToString(3);
        SelectPage("overview");
        _root = root;
        _useDiscovery = useDiscovery;
        _workerExecutable = workerExecutable ?? Path.Combine(AppContext.BaseDirectory, "runtime", "BabbleFrameBridge.Worker.exe");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        _logPath = Path.Combine(root, "logs", $"bridge-{DateTime.Now:yyyy-MM-dd}.log");
        _store = new ModuleStore(root);
        // A new profile defaults to Headset Runtime; existing profiles retain their saved source.
        _settings = AppSettings.Load(root);
        if (!File.Exists(Path.Combine(root, "settings.json"))) { _settings.AutoStart = true; _settings.Save(root); }
        HostBox.Text = _settings.OutputHost;
        PortBox.Text = _settings.OutputPort.ToString();
        ReceiveBox.Text = _settings.ReceivePort.ToString();
        _store.MigrateLegacyProfiles(_settings.InputSource);
        UpdateSourceLabels();
        AutoStartBox.IsChecked = _settings.AutoStart;
        ApplyMotionPreference();
        SystemParameters.StaticPropertyChanged += MotionPreference_Changed;
        Closed += (_, _) => { SystemParameters.StaticPropertyChanged -= MotionPreference_Changed; _noticeTimer.Stop(); };
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
                SetState("Add your tracking module", "Choose a supported module ZIP to start tracking.", false);
                TrackingRoute.Text = "Add a module to begin";
                Log("No tracking module installed. Choose a supported module ZIP or drop it onto this window.");
            }
        });
        Closing += ClosingAsync;
    }

    private async Task RunOperation(Func<Task> action)
    {
        if (_busy || _closing) return;
        DismissNotice();
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
        AutoStartBox.IsEnabled = !_busy && _initialized;
        StartButton.Content = _running ? "Stop bridge" : "Start bridge";
        RefreshLibrary();
    }

    private void RefreshModule()
    {
        var current = _store.Current;
        ModuleLabel.Text = current is null ? "No module selected" : $"{ModuleCatalog.Get(current.Kind).Name}  /  {current.Version}";
        HashLabel.Text = "";
        ImportButton.Content = "Add module ZIP  ↑";
        PackageDetail.Text = current is null ? "Open Modules to add your tracking module ZIP."
            : $"{ConnectionFor(current)} · input selected automatically";
        ProviderTag.Text = current is null ? "MODULES" : ModuleCatalog.Get(current.Kind).Name.ToUpperInvariant();
        RefreshButtons();
    }

    private sealed record ModuleCard(ModuleRelease Release, string Name, string Detail, string Hint, string Action, bool CanSelect);
    private void RefreshLibrary()
    {
        ModuleLibrary.ItemsSource = _store.Installed.Select(release => new ModuleCard(release,
            ModuleCatalog.Get(release.Kind).Name, $"Version {release.Version} · {ConnectionFor(release)}",
            InstructionsFor(release),
            release.Id == _store.Current?.Id ? "Selected ✓" : "Use module",
            !_busy && _initialized && release.Id != _store.Current?.Id)).ToArray();
    }

    private async void SelectModule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ModuleRelease release })
            await RunOperation(() => SwitchModule(release));
    }

    private int InputPortFor(ModuleRelease release)
    {
        if (release.Kind == ModuleKind.BabbleHeadsetRuntime) return _settings.LinkPort;
        if (release.Kind != ModuleKind.BaballoniaDesktop) return ModuleCatalog.Get(release.Kind).InputPort;
        return BaballoniaOscPortFor(release);
    }
    private static int BaballoniaOscPortFor(ModuleRelease release)
    {
        try
        {
            using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(release.DirectoryPath, "BabbleConfig.json")));
            if (config.RootElement.ValueKind == JsonValueKind.Object && config.RootElement.TryGetProperty("Port", out var port) && port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out var value) && value is > 0 and <= 65535) return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return 8888;
    }
    private void ValidateConnection(AppSettings settings, ModuleRelease? release)
    {
        settings.Validate();
        if (release is null) return;
        var inputs = new List<int> { InputPortFor(release) };
        // The Babble DLL also owns its OSC listener while BFR Link is active.
        if (release.Kind == ModuleKind.BabbleHeadsetRuntime) inputs.Add(BaballoniaOscPortFor(release));
        foreach (var port in inputs.Where(port => port > 0))
            if (port == settings.ReceivePort || port == settings.OutputPort)
                throw new InvalidOperationException($"{ModuleCatalog.Get(release.Kind).Name} uses input port {port}. Choose different VRChat ports in Connection.");
    }
    private string ConnectionFor(ModuleRelease release) => InputPortFor(release) is var port && port > 0 ? $"UDP {port}" : "Shared memory · Virtual Desktop Streamer";
    private string InstructionsFor(ModuleRelease release) => release.Kind == ModuleKind.BaballoniaDesktop
        ? $"Enable Baballonia’s VRCFaceTracking output to 127.0.0.1:{InputPortFor(release)}."
        : ModuleCatalog.Get(release.Kind).Instructions;
    private string ProviderInstructions => (_sessionRelease ?? _store.Current) is { } release ? InstructionsFor(release) : Provider.Instructions;
    private ModuleDefinition Provider => ModuleCatalog.Get((_sessionRelease ?? _store.Current)?.Kind ?? ModuleKind.BabbleHeadsetRuntime);

    private void SetState(string title, string detail, bool live, bool error = false)
    {
        StateTitle.Text = title; StateDetail.Text = detail;
        StateDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(error ? "#F4C2C2" : live ? "#BDE5D1" : "#A6C5B8"));
    }

    private void UpdateSourceLabels() => HeadsetState.Text = _sessionRelease is null && _store.Current is null ? "Waiting for module" : Provider.Name + " · ready";

    private void SetStoppedState()
    {
        UpdateSourceLabels();
        if (_store.Current is null) SetState("Add your tracking module", "Choose a supported module ZIP to start tracking.", false);
        else SetState("Bridge stopped", "Start the bridge with " + Provider.Name + ".", false);
    }

    private void SetListeningState() => SetState(Provider.Name, ProviderInstructions, true);

    private void ShowNotice(string message, bool error = false)
    {
        DismissNotice();
        GlobalNoticeText.Text = message;
        GlobalNotice.SetResourceReference(Border.BackgroundProperty, error ? "ErrorBackground" : "NoticeBackground");
        GlobalNotice.SetResourceReference(Border.BorderBrushProperty, error ? "ErrorBorder" : "NoticeBorder");
        GlobalNoticeText.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorInk" : "NoticeInk");
        GlobalNotice.Visibility = Visibility.Visible;
        Motion.Transition(GlobalNotice, _motionEnabled);
        if (!error) { _noticeTimer.Interval = TimeSpan.FromSeconds(4); _noticeTimer.Start(); }
    }

    private void NoticeTimer_Tick(object? sender, EventArgs e)
    {
        if (_noticeFading || !_motionEnabled) { DismissNotice(); return; }
        _noticeFading = true;
        _noticeTimer.Interval = TimeSpan.FromMilliseconds(180);
        GlobalNotice.BeginAnimation(OpacityProperty, new DoubleAnimation(0, _noticeTimer.Interval));
    }

    private void DismissNotice()
    {
        _noticeTimer.Stop();
        _noticeFading = false;
        GlobalNotice.Visibility = Visibility.Collapsed;
        Motion.Transition(GlobalNotice, false);
    }

    private void DismissNotice_Click(object sender, RoutedEventArgs e) => DismissNotice();

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
        _sessionRelease = release;
        var inputPort = InputPortFor(release);
        ValidateConnection(_settings, release);
        var executable = _workerExecutable;
        if (!File.Exists(executable)) throw new FileNotFoundException("The runtime folder is missing. Extract the entire portable app ZIP before starting.", executable);
        var start = new ProcessStartInfo(executable) {
            WorkingDirectory = Path.GetDirectoryName(executable)!, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        start.ArgumentList.Add("--module"); start.ArgumentList.Add(Path.Combine(release.DirectoryPath, release.DllFileName));
        start.ArgumentList.Add("--data"); start.ArgumentList.Add(_root);
        start.ArgumentList.Add("--kind"); start.ArgumentList.Add(release.Kind.ToString());
        if (release.Kind is ModuleKind.BabbleHeadsetRuntime or ModuleKind.BaballoniaDesktop) { start.ArgumentList.Add("--input-port"); start.ArgumentList.Add(inputPort.ToString()); }
        start.ArgumentList.Add("--parent"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        if (!_useDiscovery) start.ArgumentList.Add("--no-discovery");
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
            HeadsetState.Text = Provider.Name + " · stopped";
            OutputRate.Text = "0 msgs/s"; SenderCadence.Text = "Bridge idle"; RefreshButtons();
        }));
        SetState("Starting bridge", $"Loading {Provider.Name} {release.Version}…", false);
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
        _sessionRelease = null;
        OutputRate.Text = "0 msgs/s"; SenderCadence.Text = "Bridge idle";
        UpdateSourceLabels();
        TrackingRoute.Text = "Bridge stopped";
        if (_store.Current is null)
            SetState("Add your tracking module", "Choose a supported module ZIP to start tracking.", false);
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
                case "ready": _ready?.TrySetResult(true); Log(Provider.Name + " loaded. " + ProviderInstructions); break;
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
        HeadsetState.Text = Provider.Name + (linked ? " · connected" : " · ready");
        OutputRate.Text = $"{Number(item, "rate"):N0} msgs/s";
        SenderCadence.Text = $"Tracking processed at {Number(item, "trackingRate"):N0} Hz";
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
            if (Provider.Kind == ModuleKind.BabbleHeadsetRuntime)
                SetState(linked ? "Headset connected" : "Listening for headset",
                    receiving ? "Receiving expressions from the headset runtime." : linked ? "Connected. Waiting for expression data from the headset." : ProviderInstructions, true);
            else SetState(Provider.Name, Bool(item, "receivingKnown") && receiving ? "Receiving tracking data. Your expressions are connected." : ProviderInstructions, true);
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
        else ShowNotice("Add a supported module ZIP before starting the bridge.");
    });

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Add tracking module ZIP", Filter = "Module ZIP (*.zip)|*.zip", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) await UpdateModule(picker.FileName);
    }

    private async Task UpdateModule(string zipPath) => await RunOperation(async () => {
        SetState("Checking module ZIP", "Validating the package before switching modules…", false);
        var release = await Task.Run(() => _store.Prepare(zipPath));
        if (_closing) return;
        _store.Register(release);
        if (release.SupportsHeadset)
        {
            var companion = await Task.Run(() => _store.CreateCompanion(release));
            _store.Register(companion);
            // Preserve a desktop user's selected transport when updating its shared Babble package.
            if (_store.Current?.Kind == companion.Kind) release = companion;
        }
        RefreshModule();
        await SwitchModule(release);
    });

    private async Task SwitchModule(ModuleRelease release)
    {
        var old = _store.Current;
        var wasRunning = _running;
        _pendingRestart = false;
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
            InputSource = _settings.InputSource,
            AutoStart = AutoStartBox.IsChecked == true };
        ValidateConnection(updated, _store.Current);
        var previous = _settings;
        var wasRunning = _running;
        updated.Save(_root); _settings = updated;
        try
        {
            if (wasRunning) { await StopWorker(); await StartWorker(_store.Current!); }
        }
        catch (Exception failure)
        {
            await StopWorker();
            previous.Save(_root); _settings = previous;
            HostBox.Text = previous.OutputHost; PortBox.Text = previous.OutputPort.ToString(); ReceiveBox.Text = previous.ReceivePort.ToString();
            if (wasRunning && _store.Current is { } current)
            {
                try { await StartWorker(current); }
                catch (Exception restoreFailure) { Log("Previous connection could not restart: " + restoreFailure.Message); }
            }
            throw new InvalidOperationException("The new connection could not start. Your previous connection settings were restored. " + failure.Message);
        }
        TargetLabel.Text = $"{updated.OutputHost} : {updated.OutputPort}";
        Log("Connection settings saved.");
        if (!wasRunning) SetStoppedState();
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
        else Log("Choose one tracking module ZIP to import.");
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
            "modules" => ("Modules", "Your headset. Your choice of module."),
            "settings" => ("Connection", "Keep your expressions flowing."),
            "activity" => ("Activity log", "A clear view of your tracking session."),
            _ => ("Babble Bridge", "A little more you, in VR.")
        };
    }

    private void ShowActivity_Click(object sender, RoutedEventArgs e) { ActivityNav.IsChecked = true; }
    private void Attribution_Click(object sender, RoutedEventArgs e) {
        var notices = Path.Combine(AppContext.BaseDirectory, "NOTICE.txt");
        if (File.Exists(notices)) Process.Start(new ProcessStartInfo(notices) { UseShellExecute = true });
    }
    private void Website_Click(object sender, RoutedEventArgs e) => OpenLink("https://github.com/ClassicWasTaken/BabbleBridge");
    private static void OpenLink(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private async void ClosingAsync(object? sender, CancelEventArgs e) {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; IsEnabled = false;
        await StopWorker();
        _allowClose = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
