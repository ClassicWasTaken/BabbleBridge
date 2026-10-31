// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using System.Security.Cryptography;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
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
    private readonly ModuleDownloader _moduleDownloader;
    private readonly bool _ownsModuleDownloader;
    private readonly Func<bool, Task>? _configureSteamVrAutoLaunch;
    private CancellationTokenSource? _downloadCancellation;
    private Task? _downloadTask;
    private Task? _startupPreferenceTask;
    private Task? _initializationTask;
    private Func<bool>? _ensureSystemTrayIcon;
    private bool _exitRequested;
    private WindowState _restoreWindowState = WindowState.Normal;
    private bool _syncingStartupPreferences;
    private bool _syncingKittySound;
    private AppSettings _settings;
    private Process? _worker;
    private Task? _stopTask;
    private ModuleRelease? _sessionRelease;
    private string? _pendingRemovalId;
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
    private bool _awaitingVirtualDesktop;
    private sealed record BaballoniaMetadataCache(long Length, DateTime Modified, bool AlphaCompatible);
    private static readonly Dictionary<string, BaballoniaMetadataCache> BaballoniaMetadata = new(StringComparer.OrdinalIgnoreCase);
    private static readonly byte[][] AlphaEyeAddresses = new[] { "/leftEyeX", "/leftEyeY", "/rightEyeX", "/rightEyeY", "/leftEyeLid", "/rightEyeLid" }
        .Select(Encoding.Unicode.GetBytes).ToArray();

    public event EventHandler? StartupPreferencesChanged;
    public bool StartWithSteamVr => _settings.StartWithSteamVr;
    public bool CloseWithSteamVr => _settings.CloseWithSteamVr;
    public bool StartMinimized => _settings.StartMinimized;

    public MainWindow(string root, bool useDiscovery = true, string? workerExecutable = null, ModuleDownloader? moduleDownloader = null, Func<bool, Task>? configureSteamVrAutoLaunch = null, KittyVoice? kittyVoice = null)
    {
        InitializeComponent();
        _noticeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _noticeTimer.Tick += NoticeTimer_Tick;
        BuildLabel.Text = "INDEPENDENT BUILD  /  v" + typeof(App).Assembly.GetName().Version?.ToString(3);
        SelectPage("overview");
        _root = root;
        _useDiscovery = useDiscovery;
        _workerExecutable = workerExecutable ?? Path.Combine(AppContext.BaseDirectory, "runtime", "BabbleFrameBridge.Worker.exe");
        _ownsModuleDownloader = moduleDownloader is null;
        _moduleDownloader = moduleDownloader ?? new ModuleDownloader();
        _configureSteamVrAutoLaunch = configureSteamVrAutoLaunch;
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
        SyncStartupPreferences();
        KittyFace.Source = HologramKittyVisual.CreateHeadIcon();
        if (kittyVoice is not null) KittyOverlay.SetVoice(kittyVoice);
        KittyOverlay.SoundsEnabled = _settings.KittySoundsEnabled;
        KittySoundsBox.IsChecked = _settings.KittySoundsEnabled;
        KittyOverlay.Attach(this);
        KittyOverlay.CanSmackAt = point => FindKittyTarget(point) is not null;
        KittyOverlay.ImpactRequested = (point, strength) => { if (FindKittyTarget(point) is { } target) Motion.PetWobble(target, strength); };
        KittyOverlay.AnimationSuspended += (_, _) => Motion.ResetPetWobbles();
        KittyOverlay.StateChanged += (_, _) => UpdateKittyButton();
        ApplyKittyPreference();
        ApplyMotionPreference();
        SystemParameters.StaticPropertyChanged += MotionPreference_Changed;
        Closed += (_, _) => { KittyOverlay.Dispose(); Motion.ResetPetWobbles(); SystemParameters.StaticPropertyChanged -= MotionPreference_Changed; _noticeTimer.Stop(); if (_ownsModuleDownloader) _moduleDownloader.Dispose(); };
        Loaded += async (_, _) => await InitializeBridgeAsync();
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) _restoreWindowState = WindowState; };
        Closing += ClosingAsync;
    }

    // Starting in the tray must initialize tracking even before the window has
    // ever been shown. The Loaded event reuses the same task on the first Open.
    public Task InitializeBridgeAsync() => _initializationTask ??= Dispatcher.InvokeAsync(() => RunOperation(async () => {
            _initialized = true;
            Motion.Transition(_currentPage ?? OverviewPage, _motionEnabled);
            RefreshModule();
            TargetLabel.Text = $"{_settings.OutputHost} : {_settings.OutputPort}";
            if (_store.Current is { } current)
            {
                SetStoppedState();
                if (_settings.AutoStart && current.IsAvailable) await StartWorker(current);
            }
            else
            {
                SetNoModuleState();
                Log(_store.Installed.Count == 0 ? "No tracking module installed. Choose a supported module ZIP or drop it onto this window." : "Choose an installed tracking module in Modules.");
            }
        })).Task.Unwrap();

    public void ConfigureSystemTray(Func<bool> ensureIcon) => _ensureSystemTrayIcon = ensureIcon;

    public bool HideToSystemTray()
    {
        if (_ensureSystemTrayIcon is null || _exitRequested || _closing) return false;
        try { if (!_ensureSystemTrayIcon()) return false; }
        catch (Exception error) { Log("System tray is unavailable: " + error.Message); return false; }
        Hide();
        return true;
    }

    public void RestoreFromSystemTray()
    {
        if (_closing || _allowClose) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = _restoreWindowState;
        Activate();
    }

    public void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    public void AllowSystemExit() => _exitRequested = true;
    private void Quit_Click(object sender, RoutedEventArgs e) => RequestExit();

    private void KittyToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_closing) return;
        var previous = _settings.KittyEnabled;
        try
        {
            _settings.KittyEnabled = !previous;
            _settings.Save(_root);
        }
        catch (Exception error)
        {
            _settings.KittyEnabled = previous;
            ShowNotice("The kitty preference could not be saved. " + error.Message, true);
        }
        ApplyKittyPreference();
    }

    private void ApplyKittyPreference()
    {
        KittyOverlay.SetActive(_settings.KittyEnabled);
        if (!_settings.KittyEnabled) Motion.ResetPetWobbles();
        UpdateKittyButton();
    }

    private void KittySounds_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _syncingKittySound || _closing) return;
        var previous = _settings.KittySoundsEnabled;
        try
        {
            _settings.KittySoundsEnabled = KittySoundsBox.IsChecked == true;
            _settings.Save(_root);
        }
        catch (Exception error)
        {
            _settings.KittySoundsEnabled = previous;
            _syncingKittySound = true;
            KittySoundsBox.IsChecked = previous;
            _syncingKittySound = false;
            ShowNotice("The kitty sound preference could not be saved. " + error.Message, true);
        }
        KittyOverlay.SoundsEnabled = _settings.KittySoundsEnabled;
    }

    private void UpdateKittyButton()
    {
        KittyActiveDot.Visibility = _settings.KittyEnabled ? Visibility.Visible : Visibility.Collapsed;
        KittyToggle.ToolTip = _settings.KittyEnabled ? "Relay · " + KittyOverlay.Status + " · click to hide kitty" : "Wake Relay, your holographic kitty";
        AutomationProperties.SetName(KittyToggle, _settings.KittyEnabled ? "Hide holographic kitty" : "Wake holographic kitty");
        KittyToggle.SetResourceReference(Control.BackgroundProperty, _settings.KittyEnabled ? "NavSelected" : "SidebarBackground");
        KittyToggle.SetResourceReference(Control.ForegroundProperty, "SidebarInk");
    }

    private FrameworkElement? FindKittyTarget(Point point)
    {
        DependencyObject? hit = null;
        VisualTreeHelper.HitTest(RootSurface, item => ReferenceEquals(item, KittyOverlay)
            ? HitTestFilterBehavior.ContinueSkipSelfAndChildren : HitTestFilterBehavior.Continue,
            result => { hit = result.VisualHit; return HitTestResultBehavior.Stop; }, new PointHitTestParameters(point));
        Border? panel = null;
        for (var node = hit; node is not null && !ReferenceEquals(node, RootSurface); node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, KittyToggle)) return null;
            if (node is ButtonBase { IsVisible: true } button) return button;
            if (panel is null && node is Border { IsVisible: true } border && border.CornerRadius.TopLeft >= 8
                && border.ActualWidth >= 150 && border.ActualHeight >= 55) panel = border;
        }
        return panel;
    }

    private async Task RunOperation(Func<Task> action)
    {
        if (_busy || _closing) return;
        DismissNotice();
        _pendingRemovalId = null;
        _busy = true; RefreshButtons();
        try { await action(); }
        catch (Exception ex) { Log("ERROR: " + ex.Message); SetState("Needs attention", ex.Message, false, true); ShowNotice(ex.Message, true); }
        finally { _busy = false; RefreshButtons(); if (_pendingRestart && !_closing) { _pendingRestart = false; _ = RestartForHeadset(); } }
    }

    private void RefreshButtons()
    {
        StartButton.IsEnabled = !_busy && _initialized && (_running || _store.Current is { IsAvailable: true });
        ImportButton.IsEnabled = !_busy && _initialized;
        RollbackButton.IsEnabled = !_busy && _store.Previous is { IsAvailable: true };
        SaveButton.IsEnabled = !_busy && _initialized;
        AutoStartBox.IsEnabled = !_busy && _initialized;
        StartWithSteamVrBox.IsEnabled = !_busy && _initialized && _configureSteamVrAutoLaunch is not null;
        CloseWithSteamVrBox.IsEnabled = !_busy && _initialized;
        StartMinimizedBox.IsEnabled = !_busy && _initialized;
        StartButton.Content = _running ? "Stop bridge" : "Start bridge";
        RefreshLibrary();
        RefreshDownloadChoices();
    }

    private void RefreshModule()
    {
        var current = _store.Current;
        ModuleLabel.Text = current is null ? "No module selected" : $"{NameFor(current)}  /  {current.Version}";
        HashLabel.Text = "";
        ImportButton.Content = current is null ? "Add a module →" : IsOlderBaballoniaModule(current) ? "Update Baballonia →" : "Manage modules →";
        PackageDetail.Text = current is null ? _store.Installed.Count > 0 ? "Choose an installed module in Modules." : "Open Modules to download a module or import a DLL or ZIP."
            : !current.IsAvailable ? "Module files are missing. Restore or re-import the module, or remove this profile."
            : IsOlderBaballoniaModule(current) ? $"{ConnectionFor(current)} · Expressions Alpha needs module 3.2.0 or newer. Update in Modules."
            : current.Kind == ModuleKind.Custom ? "Module-managed input · follow this module’s setup instructions."
            : $"{ConnectionFor(current)} · input selected automatically";
        ProviderTag.Text = current is null ? "MODULES" : NameFor(current).ToUpperInvariant();
        RefreshButtons();
    }

    private sealed record ModuleCard(ModuleRelease Release, string Name, string Detail, string Hint, string Action, bool CanSelect, bool CanRemove, Visibility UpdateVisibility, bool CanUpdate, Visibility ConfirmationVisibility, string RemovalMessage);
    private void RefreshLibrary()
    {
        ModuleLibrary.ItemsSource = _store.Installed.OrderBy(release => release.Kind switch {
            ModuleKind.BabbleHeadsetRuntime => 0, ModuleKind.BaballoniaDesktop => 1, ModuleKind.SteamLink => 2, _ => 3
        }).Select(release => new ModuleCard(release,
            NameFor(release), release.IsAvailable ? $"Version {release.Version} · {ConnectionFor(release)}" : "Module files missing",
            release.IsAvailable ? InstructionsFor(release) + (IsOlderBaballoniaModule(release) ? "\nExpressions Alpha needs module 3.2.0 or newer for eye tracking." : "") : "Restore or re-import this module, or remove its profile. Your other modules are available.",
            !release.IsAvailable ? "Unavailable" : release.Id == _store.Current?.Id ? "Selected ✓" : "Use module",
            !_busy && _initialized && release.IsAvailable && release.Id != _store.Current?.Id,
            !_busy && _initialized,
            release.IsAvailable && IsOlderBaballoniaModule(release) ? Visibility.Visible : Visibility.Collapsed,
            !_busy && _initialized && release.IsAvailable && IsOlderBaballoniaModule(release),
            release.Id == _pendingRemovalId ? Visibility.Visible : Visibility.Collapsed,
            release.Id == _store.Current?.Id ? "Remove this profile and its module settings? The bridge will stop." : "Remove this profile and its module settings?")).ToArray();
    }

    private sealed record DownloadCard(ModuleKind Kind, string Name, string Detail, string OfficialUrl, bool CanDownload)
    {
        public string DownloadLabel => "Download " + Name;
    }
    private void RefreshDownloadChoices()
    {
        var available = !_busy && _initialized;
        var installedKinds = _store.Installed.Where(release => release.IsAvailable).Select(release => release.Kind).ToHashSet();
        var choices = new[] {
            new DownloadCard(ModuleKind.BabbleHeadsetRuntime, "Headset Runtime / BFR", "Baballonia module 4.0.0 · headset input", "https://github.com/Project-Babble/Baballonia/releases/tag/vrcft-module-4.0.0", available),
            new DownloadCard(ModuleKind.BaballoniaDesktop, "Baballonia", "Expressions Alpha-compatible · module 3.2.0", "https://github.com/Project-Babble/Baballonia/releases/tag/vrcft-module-3.2.0", available),
            new DownloadCard(ModuleKind.SteamLink, "Steam Link", "LinkFT module 1.0.6", "https://github.com/ykeara/LinkFT/releases/tag/1.0.6", available),
            new DownloadCard(ModuleKind.VirtualDesktop, "Virtual Desktop", "Official compiled module ZIP", "https://download.vrdesktop.net/files/VirtualDesktop.VRCFaceTracking.zip", available)
        }.Where(choice => choice.Kind == ModuleKind.BaballoniaDesktop
            ? !_store.Installed.Any(release => release.IsAvailable && !release.SupportsHeadset && IsAlphaCompatibleBaballoniaModule(release))
            : !installedKinds.Contains(choice.Kind)).ToArray();
        DownloadChoices.ItemsSource = choices;
        DownloadChoicesEmptyText.Visibility = choices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void DownloadModule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ModuleKind kind }) await DownloadModule(kind);
    }

    private Task DownloadModule(ModuleKind kind)
    {
        if (_busy || _closing) return Task.CompletedTask;
        return _downloadTask = DownloadModuleCore(kind);
    }

    private static Version? BaballoniaVersion(ModuleRelease release)
    {
        if (release.Kind != ModuleKind.BaballoniaDesktop || !Version.TryParse(release.Version.TrimStart('v', 'V'), out var version)) return null;
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    private static bool IsOlderBaballoniaModule(ModuleRelease release) => BaballoniaVersion(release) is { Major: >= 3 } version && version < new Version(3, 2, 0, 0);
    private static bool IsAlphaCompatibleBaballoniaModule(ModuleRelease release)
    {
        if (release.Kind != ModuleKind.BaballoniaDesktop) return false;
        if (BaballoniaVersion(release) is { Major: >= 3 } version) return version >= new Version(3, 2, 0, 0);
        // Official bare DLLs can report assembly version 1.0 even when the ZIP is
        // module 3.2. Inspect its bounded metadata without loading executable code.
        try
        {
            var path = Path.GetFullPath(Path.Combine(release.DirectoryPath, release.DllFileName));
            var file = new FileInfo(path);
            if (!file.Exists) return false;
            if (BaballoniaMetadata.TryGetValue(path, out var cached) && cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc)
                return cached.AlphaCompatible;
            var compatible = false;
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var pe = new PEReader(stream))
            {
                const int maximumMetadata = 8 * 1024 * 1024;
                if (pe.HasMetadata && pe.PEHeaders.CorHeader?.MetadataDirectory.Size is > 0 and <= maximumMetadata)
                {
                    var metadata = pe.GetMetadata();
                    if (metadata.Length <= maximumMetadata)
                    {
                        var bytes = metadata.GetContent().AsSpan();
                        compatible = true;
                        foreach (var address in AlphaEyeAddresses)
                            if (bytes.IndexOf(address) < 0) { compatible = false; break; }
                    }
                }
            }
            if (BaballoniaMetadata.Count >= 256) BaballoniaMetadata.Clear();
            BaballoniaMetadata[path] = new(file.Length, file.LastWriteTimeUtc, compatible);
            return compatible;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException)
        {
            return false;
        }
    }

    private async void UpdateBaballonia_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ModuleRelease release }) await UpdateBaballonia(release);
    }

    private Task UpdateBaballonia(ModuleRelease release)
    {
        if (_busy || _closing || !_initialized || !release.IsAvailable || !IsOlderBaballoniaModule(release)) return Task.CompletedTask;
        var activate = _store.Current?.Id == release.Id;
        // Reuse a compatible installed profile only when it receives the same input.
        // Other profiles' saved connections must never change as a side effect.
        var compatible = _store.Installed.Where(item => item.IsAvailable && IsAlphaCompatibleBaballoniaModule(item)
                && SameBaballoniaConnection(item, release))
            .OrderByDescending(BaballoniaVersion).ThenByDescending(item => item.InstalledAt).FirstOrDefault();
        return _downloadTask = compatible is not null
            ? RunOperation(async () =>
            {
                if (activate) await SwitchModule(compatible);
                else ShowNotice("A compatible Baballonia module is already in your library. Choose Use module when you’re ready.");
            })
            : DownloadModuleCore(ModuleKind.BaballoniaDesktop, release, activate);
    }

    private static bool SameBaballoniaConnection(ModuleRelease first, ModuleRelease second)
    {
        static (string Host, int Port)? Read(ModuleRelease release)
        {
            try
            {
                var path = Path.Combine(release.DirectoryPath, "BabbleConfig.json");
                if (!File.Exists(path)) return ("127.0.0.1", 8888);
                using var config = JsonDocument.Parse(File.ReadAllText(path));
                if (config.RootElement.ValueKind != JsonValueKind.Object) return null;
                var host = config.RootElement.TryGetProperty("Host", out var hostNode) && hostNode.ValueKind == JsonValueKind.String
                    ? hostNode.GetString() ?? "127.0.0.1" : "127.0.0.1";
                var port = config.RootElement.TryGetProperty("Port", out var portNode) && portNode.ValueKind == JsonValueKind.Number
                    && portNode.TryGetInt32(out var value) && value is > 0 and <= 65535 ? value : 8888;
                return (host, port);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
        }
        return Read(first) is { } firstConnection && Read(second) is { } secondConnection && firstConnection == secondConnection;
    }

    private async Task DownloadModuleCore(ModuleKind kind, ModuleRelease? upgradeFrom = null, bool activateUpgrade = false) => await RunOperation(async () =>
    {
        using var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        var name = ModuleCatalog.Get(kind).Name;
        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadCancelButton.IsEnabled = true;
        DownloadProgressBar.IsIndeterminate = true;
        DownloadProgressBar.Value = 0;
        DownloadStatus.Text = "Downloading " + name + "…";
        var acceptingProgress = true;
        var progress = new Progress<ModuleDownloadProgress>(update =>
        {
            if (!acceptingProgress || _downloadCancellation != cancellation || cancellation.IsCancellationRequested || _closing) return;
            var total = update.TotalBytes;
            DownloadProgressBar.IsIndeterminate = total is null or <= 0;
            DownloadProgressBar.Value = total > 0 ? Math.Clamp(update.ReceivedBytes / (double)total, 0, 1) : 0;
            DownloadStatus.Text = total > 0 ? $"Downloading {name}… {DownloadProgressBar.Value:P0}" : $"Downloading {name}… {update.ReceivedBytes / 1024:N0} KB";
        });
        try
        {
            var zip = await _moduleDownloader.DownloadAsync(kind, _root, progress, cancellation.Token);
            acceptingProgress = false;
            cancellation.Token.ThrowIfCancellationRequested();
            DownloadCancelButton.IsEnabled = false;
            DownloadStatus.Text = "Checking " + name + "…";
            DownloadProgressBar.IsIndeterminate = true;
            await ImportModulePackage(zip, kind, activate: upgradeFrom is not null && activateUpgrade, upgradeFrom: upgradeFrom);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Log("Module download cancelled.");
            if (!_closing) ShowNotice("Module download cancelled.");
        }
        finally
        {
            acceptingProgress = false;
            _downloadCancellation = null;
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
        }
    });

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _downloadCancellation?.Cancel();
    private void ModuleSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url }) OpenLink(url);
    }
    private void RuntimeSetup_Click(object sender, RoutedEventArgs e) => OpenLink("https://github.com/Project-Babble/Babble-Frame-Runtime/releases/latest");

    private async void SelectModule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ModuleRelease release })
            await RunOperation(() => SwitchModule(release));
    }

    private void RemoveModule_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing || !_initialized || sender is not Button { Tag: ModuleRelease release }) return;
        _pendingRemovalId = release.Id;
        DismissNotice();
        RefreshLibrary();
    }

    private void CancelRemoveModule_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _pendingRemovalId = null;
        RefreshLibrary();
    }

    private async void ConfirmRemoveModule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ModuleRelease release } && _pendingRemovalId == release.Id)
            await RunOperation(() => RemoveModule(release));
    }

    private void RemoveConfirmation_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border { Visibility: Visibility.Visible } border) Motion.Transition(border, _motionEnabled);
    }

    private async Task RemoveModule(ModuleRelease release)
    {
        var selected = _store.Current?.Id == release.Id;
        var wasRunning = _running;
        _pendingRemovalId = null;
        if (selected) { _pendingRestart = false; await StopWorker(); }
        try
        {
            await Task.Run(() => _store.Remove(release));
        }
        catch
        {
            if (selected && wasRunning && _store.Current?.Id == release.Id)
            {
                try { await StartWorker(release); }
                catch (Exception restoreFailure) { Log("The module could not restart after removal failed: " + restoreFailure.Message); }
            }
            RefreshModule();
            throw;
        }
        RefreshModule();
        if (selected) SetStoppedState();
        var message = NameFor(release) + " removed.";
        Log(message);
        ShowNotice(message);
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
                throw new InvalidOperationException($"{NameFor(release)} uses input port {port}. Choose different VRChat ports in Connection.");
    }
    private string ConnectionFor(ModuleRelease release) => release.Kind == ModuleKind.Custom ? "Module-managed input"
        : InputPortFor(release) is var port && port > 0 ? $"UDP {port}" : "Shared memory · Virtual Desktop Streamer";
    private string InstructionsFor(ModuleRelease release) => release.Kind == ModuleKind.BaballoniaDesktop
        ? $"Enable Baballonia’s VRCFaceTracking output to 127.0.0.1:{InputPortFor(release)}."
            + (release.SupportsHeadset ? " This profile uses the BFR package. The dedicated Baballonia desktop download uses module 3.2.0." : "")
        : ModuleCatalog.Get(release.Kind).Instructions;
    private string ProviderInstructions => (_sessionRelease ?? _store.Current) is { } release ? InstructionsFor(release) : Provider.Instructions;
    private static string NameFor(ModuleRelease release) => release.Kind == ModuleKind.Custom ? release.ModuleName : ModuleCatalog.Get(release.Kind).Name;
    private string ProviderName => (_sessionRelease ?? _store.Current) is { } release ? NameFor(release) : Provider.Name;
    private ModuleDefinition Provider => ModuleCatalog.Get((_sessionRelease ?? _store.Current)?.Kind ?? ModuleKind.BabbleHeadsetRuntime);

    private void SetState(string title, string detail, bool live, bool error = false)
    {
        StateTitle.Text = title; StateDetail.Text = detail;
        StateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, error ? "ErrorInk" : live ? "Accent" : "HeroMuted");
    }

    private void UpdateSourceLabels()
    {
        if (!_running && _store.Current is { IsAvailable: false }) HeadsetState.Text = ProviderName + " · unavailable";
        else HeadsetState.Text = _sessionRelease is null && _store.Current is null
            ? _store.Installed.Count > 0 ? "Choose a module" : "Waiting for module" : ProviderName + " · ready";
    }

    private void SetNoModuleState()
    {
        var hasModules = _store.Installed.Count > 0;
        SetState(hasModules ? "Choose your tracking module" : "Add your tracking module",
            hasModules ? "Open Modules and select the module you want to use." : "Open Modules to download a module or import a DLL or ZIP.", false);
        TrackingRoute.Text = hasModules ? "Choose a module to begin" : "Add a module to begin";
    }

    private void SetStoppedState()
    {
        UpdateSourceLabels();
        if (_store.Current is null) SetNoModuleState();
        else if (_store.Current is { IsAvailable: false })
            SetState("Module files missing", "Open Modules to restore, re-import, or remove this profile. Your other modules are available.", false, true);
        else SetState("Bridge stopped", "Start the bridge with " + ProviderName + ".", false);
    }

    private void SetListeningState()
    {
        if (Provider.Kind == ModuleKind.VirtualDesktop)
        {
            SetVirtualDesktopWaitingState();
            return;
        }
        SetState(ProviderName, ProviderInstructions, true);
    }

    private void SetVirtualDesktopWaitingState()
    {
        SetState(_awaitingVirtualDesktop ? "Waiting for Virtual Desktop" : "Waiting for tracking data",
            "Open Virtual Desktop Streamer, connect your headset through Virtual Desktop, and enable Forward tracking data to PC. The bridge will connect automatically.", false);
        HeadsetState.Text = "Virtual Desktop · waiting";
        if (_awaitingVirtualDesktop)
        {
            SenderCadence.Text = "Waiting for tracking source";
            TrackingRoute.Text = "Waiting for Virtual Desktop tracking data";
        }
    }

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
        if (!release.IsAvailable) throw new InvalidOperationException("This module’s DLL is missing. Restore or re-import it, or select another installed module.");
        if (_worker is not null) await StopWorker();
        _sessionRelease = release;
        _awaitingVirtualDesktop = false;
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
        start.ArgumentList.Add("--module-name"); start.ArgumentList.Add(NameFor(release));
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
            _awaitingVirtualDesktop = false;
            _ready?.TrySetException(new InvalidOperationException("The tracking runtime exited. Check the activity log for details."));
            SetState("Runtime stopped", "Check the activity log, then start the bridge again.", false, true);
            ShowNotice("The runtime stopped. Check the activity log, then start the bridge again.", true);
            HeadsetState.Text = ProviderName + " · stopped";
            OutputRate.Text = "0 msgs/s"; SenderCadence.Text = "Bridge idle"; RefreshButtons();
        }));
        SetState("Starting bridge", $"Loading {ProviderName} {release.Version}…", false);
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

    private Task StopWorker()
    {
        if (_stopTask is not { IsCompleted: false }) _stopTask = StopWorkerCore();
        return _stopTask;
    }

    private async Task StopWorkerCore()
    {
        var process = _worker;
        var wasRunning = _running;
        var session = _sessionRelease;
        var wasAwaiting = _awaitingVirtualDesktop;
        _worker = null; _running = false;
        if (_closing) _ready?.TrySetCanceled();
        if (process is not null)
        {
            try {
                if (!process.HasExited) {
                    try { await process.StandardInput.WriteLineAsync("stop"); await process.StandardInput.FlushAsync(); }
                    catch (Exception e) when (e is IOException or InvalidOperationException) { }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
                    catch (TimeoutException) { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
                }
            } catch (InvalidOperationException) { }
            catch (Exception failure)
            {
                var stillRunning = true;
                try { stillRunning = !process.HasExited; }
                catch (InvalidOperationException) { stillRunning = false; }
                catch (Win32Exception) { }
                if (stillRunning)
                {
                    // Keep ownership if Windows could not confirm shutdown. Changing or
                    // deleting a module must not strand a worker that still holds its ports.
                    _worker = process; _running = wasRunning; _sessionRelease = session; _awaitingVirtualDesktop = wasAwaiting;
                    throw new InvalidOperationException("The tracking runtime could not stop. Try stopping the bridge again before changing modules.", failure);
                }
            }
            finally { if (_worker != process) process.Dispose(); }
        }
        _sessionRelease = null;
        _awaitingVirtualDesktop = false;
        OutputRate.Text = "0 msgs/s"; SenderCadence.Text = "Bridge idle";
        UpdateSourceLabels();
        TrackingRoute.Text = "Bridge stopped";
        if (_store.Current is null)
            SetNoModuleState();
        else
            SetStoppedState();
        RefreshButtons();
    }

    private static string Str(JsonElement e, string name, string fallback = "") => e.TryGetProperty(name, out var x) ? x.ToString() : fallback;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.True;
    private static double Number(JsonElement e, string name) => e.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out var value) && double.IsFinite(value) ? value : 0;

    private void HandleWorkerLine(string line)
    {
        try {
            using var document = JsonDocument.Parse(line);
            var item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object) { Log(line); return; }
            switch (Str(item, "type")) {
                case "waiting":
                    if (Provider.Kind != ModuleKind.VirtualDesktop || Str(item, "source") != "VirtualDesktop") break;
                    _awaitingVirtualDesktop = true;
                    _ready?.TrySetResult(true);
                    Log(Str(item, "message", "Waiting for Virtual Desktop tracking data."));
                    SetVirtualDesktopWaitingState();
                    break;
                case "ready":
                    _awaitingVirtualDesktop = false;
                    _ready?.TrySetResult(true);
                    Log(ProviderName + " loaded. " + ProviderInstructions);
                    if (_running) SetListeningState();
                    break;
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
        var sourceConnected = Provider.Kind == ModuleKind.BabbleHeadsetRuntime ? linked : Bool(item, "receivingKnown") && Bool(item, "receiving");
        HeadsetState.Text = ProviderName + (sourceConnected ? " · connected" : " · ready");
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
            else if (Provider.Kind == ModuleKind.VirtualDesktop)
            {
                if (_awaitingVirtualDesktop || !receiving) SetVirtualDesktopWaitingState();
                else SetState("Virtual Desktop connected", "Receiving tracking data from Virtual Desktop.", true);
            }
            else SetState(ProviderName, Bool(item, "receivingKnown") && receiving ? "Receiving tracking data. Your expressions are connected." : ProviderInstructions, true);
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
        var picker = new OpenFileDialog { Title = "Import VRCFaceTracking module", Filter = "Module DLL or ZIP (*.dll;*.zip)|*.dll;*.zip|Module DLL (*.dll)|*.dll|Module ZIP (*.zip)|*.zip", CheckFileExists = true };
        if (picker.ShowDialog(this) == true) await UpdateModule(picker.FileName);
    }

    private async Task UpdateModule(string zipPath) => await RunOperation(() => ImportModulePackage(zipPath));

    private async Task ImportModulePackage(string zipPath, ModuleKind? requestedKind = null, bool activate = true, ModuleRelease? upgradeFrom = null)
    {
        if (activate) SetState("Checking module", "Validating the module before switching…", false);
        if (!activate && upgradeFrom is null && requestedKind is { } kind)
        {
            // Repeated downloads should not clutter the library or replace the
            // user's configured copy. Explicit repairs may need a new connection.
            using var archive = File.OpenRead(zipPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(archive)).ToLowerInvariant();
            if (_store.Installed.Any(item => item.IsAvailable && item.Kind == kind && item.Sha256 == hash))
            {
                ShowNotice(ModuleCatalog.Get(kind).Name + " is already in your library.");
                return;
            }
        }
        var release = await Task.Run(() => _store.Prepare(zipPath, requestedKind));
        if (_closing) return;
        if (upgradeFrom is not null) PreserveBaballoniaConnection(upgradeFrom, release);
        _store.Register(release);
        // A catalog download adds exactly the requested profile. Manual imports
        // retain both transports for existing ZIP-update and rollback workflows.
        if (release.SupportsHeadset && requestedKind is null)
        {
            var companion = await Task.Run(() => _store.CreateCompanion(release));
            _store.Register(companion);
            // Manual ZIP updates retain the current Babble transport.
            if (requestedKind is null && _store.Current?.Kind == companion.Kind) release = companion;
        }
        RefreshModule();
        if (activate) await SwitchModule(release);
        else
        {
            var message = NameFor(release) + " added. Choose Use module when you’re ready.";
            Log(message); ShowNotice(message);
            if (!_running) SetStoppedState();
        }
    }

    private static void PreserveBaballoniaConnection(ModuleRelease previous, ModuleRelease next)
    {
        if (previous.Kind != ModuleKind.BaballoniaDesktop || next.Kind != ModuleKind.BaballoniaDesktop) return;
        var previousPath = Path.Combine(previous.DirectoryPath, "BabbleConfig.json");
        if (!File.Exists(previousPath)) return;
        var previousConfig = JsonNode.Parse(File.ReadAllText(previousPath)) as JsonObject
            ?? throw new InvalidDataException("The previous Baballonia connection settings could not be read. Check its BabbleConfig.json before updating.");
        var nextPath = Path.Combine(next.DirectoryPath, "BabbleConfig.json");
        var nextConfig = File.Exists(nextPath) ? JsonNode.Parse(File.ReadAllText(nextPath)) as JsonObject : new JsonObject();
        if (nextConfig is null) throw new InvalidDataException("The downloaded Baballonia connection settings are invalid.");
        // Preserve this profile's input connection in the newly staged copy only.
        // The previous module and all other installed profiles remain unchanged.
        foreach (var key in new[] { "Host", "Port" })
            if (previousConfig[key] is { } value) nextConfig[key] = value.DeepClone();
        File.WriteAllText(nextPath, nextConfig.ToJsonString(new() { WriteIndented = true }));
    }

    private async Task SwitchModule(ModuleRelease release)
    {
        if (!release.IsAvailable) throw new InvalidOperationException("This module’s DLL is missing. Restore or re-import it, or select another installed module.");
        var old = _store.Current;
        var wasRunning = _running;
        _pendingRestart = false;
        await StopWorker();
        try {
            await StartWorker(release);
            _store.Activate(release);
            RefreshModule();
            var activated = _awaitingVirtualDesktop ? "Virtual Desktop selected. Waiting for your headset connection."
                : old is null ? $"Module {release.Version} is active. Your tracking source is ready."
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
            StartWithSteamVr = _settings.StartWithSteamVr,
            CloseWithSteamVr = _settings.CloseWithSteamVr,
            StartMinimized = _settings.StartMinimized,
            KittyEnabled = _settings.KittyEnabled,
            KittySoundsEnabled = _settings.KittySoundsEnabled,
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
            previous.KittyEnabled = _settings.KittyEnabled;
            previous.KittySoundsEnabled = _settings.KittySoundsEnabled;
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

    private void SyncStartupPreferences()
    {
        _syncingStartupPreferences = true;
        try
        {
            StartWithSteamVrBox.IsChecked = _settings.StartWithSteamVr;
            CloseWithSteamVrBox.IsChecked = _settings.CloseWithSteamVr;
            StartMinimizedBox.IsChecked = _settings.StartMinimized;
        }
        finally { _syncingStartupPreferences = false; }
    }

    private async void SteamVrAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _syncingStartupPreferences || _busy || _closing) return;
        var requested = StartWithSteamVrBox.IsChecked == true;
        var previous = _settings.StartWithSteamVr;
        if (requested == previous) return;
        await (_startupPreferenceTask = RunOperation(async () =>
        {
            var applied = false;
            try
            {
                if (_configureSteamVrAutoLaunch is null)
                    throw new InvalidOperationException("SteamVR startup is unavailable in this app session.");
                await _configureSteamVrAutoLaunch(requested);
                applied = true;
                _settings.StartWithSteamVr = requested;
                _settings.Save(_root);
            }
            catch (Exception failure)
            {
                _settings.StartWithSteamVr = previous;
                SyncStartupPreferences();
                var message = "The SteamVR startup preference could not be saved. " + failure.Message;
                if (applied && _configureSteamVrAutoLaunch is not null)
                {
                    try { await _configureSteamVrAutoLaunch(previous); }
                    catch (Exception rollbackFailure)
                    {
                        message += " SteamVR registration could not be restored. " + rollbackFailure.Message;
                    }
                }
                throw new InvalidOperationException(message, failure);
            }
            StartupPreferencesChanged?.Invoke(this, EventArgs.Empty);
            ShowNotice(requested ? "The app will start with SteamVR." : "SteamVR automatic startup is off.");
        }));
    }

    private async void StartupWindowSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _syncingStartupPreferences || _busy || _closing) return;
        var previousClose = _settings.CloseWithSteamVr;
        var previousMinimized = _settings.StartMinimized;
        await (_startupPreferenceTask = RunOperation(() =>
        {
            try
            {
                _settings.CloseWithSteamVr = CloseWithSteamVrBox.IsChecked == true;
                _settings.StartMinimized = StartMinimizedBox.IsChecked == true;
                _settings.Save(_root);
            }
            catch
            {
                _settings.CloseWithSteamVr = previousClose;
                _settings.StartMinimized = previousMinimized;
                SyncStartupPreferences();
                throw;
            }
            StartupPreferencesChanged?.Invoke(this, EventArgs.Empty);
            ShowNotice("Startup and window preferences saved.");
            return Task.CompletedTask;
        }));
    }
    private void MotionPreference_Changed(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.BeginInvoke(new Action(ApplyMotionPreference));
    }

    private void ApplyMotionPreference() {
        _motionEnabled = SystemParameters.ClientAreaAnimation;
        Motion.Enabled = _motionEnabled;
        HeroParticles.AnimationsEnabled = _motionEnabled;
        if (!_motionEnabled) {
            foreach (var page in new FrameworkElement[] { OverviewPage, ModulesPage, SettingsPage, PreferencesPage, ActivityPage, GlobalNotice }) Motion.Transition(page, false);
        }
    }

    private void Hero_SizeChanged(object sender, SizeChangedEventArgs e) {
        if (sender is FrameworkElement surface && surface.ActualWidth > 0 && surface.ActualHeight > 0)
            surface.Clip = new RectangleGeometry(new Rect(0, 0, surface.ActualWidth, surface.ActualHeight), 21, 21);
    }
    private void Window_DragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Window_Drop(object sender, DragEventArgs e) {
        if (_busy || !_initialized) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length == 1 && (Path.GetExtension(files[0]).Equals(".zip", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(files[0]).Equals(".dll", StringComparison.OrdinalIgnoreCase))) await UpdateModule(files[0]);
        else Log("Choose one module DLL or ZIP to import.");
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
        if (_pendingRemovalId is not null) { _pendingRemovalId = null; RefreshLibrary(); }
        if (_initialized) RefreshModule();
        OverviewPage.Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
        ModulesPage.Visibility = page == "modules" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PreferencesPage.Visibility = page == "preferences" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = page == "activity" ? Visibility.Visible : Visibility.Collapsed;
        FrameworkElement selected = page switch { "modules" => ModulesPage, "settings" => SettingsPage, "preferences" => PreferencesPage, "activity" => ActivityPage, _ => OverviewPage };
        if (_currentPage != selected) { _currentPage = selected; Motion.Transition(selected, _motionEnabled); }
        (PageTitle.Text, PageSubtitle.Text) = page switch {
            "modules" => ("Modules", "Your headset. Your choice of module."),
            "settings" => ("Connection", "Keep your expressions flowing."),
            "preferences" => ("Settings", "Startup and your system tray."),
            "activity" => ("Activity log", "A clear view of your tracking session."),
            _ => ("Face Tracking Bridge", "Your expressions, connected.")
        };
    }

    private void ShowModules_Click(object sender, RoutedEventArgs e) { ModulesNav.IsChecked = true; }
    private void ShowActivity_Click(object sender, RoutedEventArgs e) { ActivityNav.IsChecked = true; }
    private void Attribution_Click(object sender, RoutedEventArgs e) {
        var notices = Path.Combine(AppContext.BaseDirectory, "NOTICE.txt");
        if (File.Exists(notices)) Process.Start(new ProcessStartInfo(notices) { UseShellExecute = true });
    }
    private void Website_Click(object sender, RoutedEventArgs e) => OpenLink("https://github.com/ClassicWasTaken/FaceTrackingBridge");
    private static void OpenLink(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private async void ClosingAsync(object? sender, CancelEventArgs e) {
        if (_allowClose) return;
        // Ordinary title-bar X keeps tracking alive. Explicit Quit, SteamVR
        // session exit and Windows session ending use the real shutdown path.
        if (!_exitRequested && !_closing && HideToSystemTray()) { e.Cancel = true; return; }
        e.Cancel = true;
        if (_closing) return;
        _closing = true; IsEnabled = false;
        _downloadCancellation?.Cancel();
        _ready?.TrySetCanceled();
        try
        {
            if (_downloadTask is { IsCompleted: false } download) await download;
            if (_startupPreferenceTask is { IsCompleted: false } startupPreference) await startupPreference;
            if (_initializationTask is { IsCompleted: false } initialization) await initialization;
            await StopWorker();
        }
        catch (Exception failure)
        {
            _closing = false; IsEnabled = true;
            _exitRequested = false;
            RestoreFromSystemTray();
            Log("Could not close the tracking runtime: " + failure.Message);
            ShowNotice(failure.Message, true);
            RefreshButtons();
            return;
        }
        _allowClose = true;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
