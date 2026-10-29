// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Xml.Linq;
using BabbleFrameBridge.App;
using BabbleFrameBridge.Shared;

internal static class Program
{
    private static int passed, failed;
    private static MainWindow? window;

    [STAThread]
    public static int Main(string[] args)
    {
        string? Argument(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : null;
        }

        var suppliedZip = Argument("--module-zip");
        var published = Argument("--publish-dir");
        var virtualDesktopZip = Argument("--virtual-desktop-zip");
        var steamLinkZip = Argument("--steam-link-zip");
        var customModuleDll = Argument("--custom-module-dll");
        var renderDirectory = Argument("--render-dir");
        var testParent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "work", "appearance-tests"));
        Directory.CreateDirectory(testParent);
        var root = Path.Combine(testParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources = LoadAppResources();
        using var themes = new SystemThemeManager(app);
        try
        {
            var emptyProfile = Path.Combine(root, "empty-profile");
            window = CreateWindow(emptyProfile);
            themes.AttachWindow(window);
            window.Show();
            PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"),
                "fresh profile finished loading");
            PumpUntil(() => Named<StackPanel>(window, "OverviewPage").Opacity >= .999,
                "startup transition finished before preview capture");

            Test("fresh profile requires an imported module even when auto-start is enabled", () =>
            {
                Check(AppSettings.Load(emptyProfile).AutoStart, "Fixture did not exercise default auto-start.");
                Check(AppSettings.Load(emptyProfile).InputSource == TrackingInputSource.HeadsetRuntime,
                    "First launch did not save Headset Runtime as the initial source.");
                Check(new ModuleStore(emptyProfile).Installed.Count == 0,
                    "An empty profile created an installed module library entry.");
                Check(new ModuleStore(emptyProfile).Current is null, "First run installed a module.");
                Check(!Directory.EnumerateFiles(emptyProfile, "*.dll", SearchOption.AllDirectories).Any(),
                    "First run copied module payload into the profile.");
                Check(Field<object?>(window, "_worker") is null, "First run launched a worker without a module.");
                Check(!Named<Button>(window, "StartButton").IsEnabled, "Start is enabled without a module.");
                Check(Named<Button>(window, "ImportButton").IsEnabled, "Import is disabled on an empty profile.");
                Check(!Named<Button>(window, "RollbackButton").IsEnabled, "Restore is enabled without a previous module.");
                Check(Named<TextBlock>(window, "ModuleLabel").Text.Contains("No module", StringComparison.OrdinalIgnoreCase),
                    "First run does not explain that no module is installed.");
                BeginButtonClick(Named<Button>(window, "ImportButton"));
                PumpUntil(() => Named<StackPanel>(window, "ModulesPage").Visibility == Visibility.Visible,
                    "the overview Add a module button opened the module page");
                Check(Descendants(Named<ItemsControl>(window, "DownloadChoices")).OfType<Button>()
                    .Count(button => button.Tag is ModuleKind) == 4,
                    "The overview Add a module action did not reveal all four in-app download choices.");
                Named<RadioButton>(window, "OverviewNav").IsChecked = true;
                PumpUntil(() => Named<StackPanel>(window, "OverviewPage").Visibility == Visibility.Visible,
                    "overview restored after checking the Add a module route");
            });

            Test("official module download choices have the requested order, labels and source links", () =>
            {
                Named<RadioButton>(window, "ModulesNav").IsChecked = true;
                Pump(); window.UpdateLayout();
                var choices = Named<ItemsControl>(window, "DownloadChoices");
                var downloads = Descendants(choices).OfType<Button>().Where(button => button.Tag is ModuleKind).ToArray();
                var expectedKinds = new[] { ModuleKind.BabbleHeadsetRuntime, ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink, ModuleKind.VirtualDesktop };
                var labels = new[] { "Download Headset Runtime / BFR", "Download Baballonia", "Download Steam Link", "Download Virtual Desktop" };
                var sourceLinks = new[]
                {
                    "https://github.com/Project-Babble/Baballonia/releases/tag/vrcft-module-4.0.0",
                    "https://github.com/Project-Babble/Baballonia",
                    "https://github.com/ykeara/LinkFT/releases/tag/1.0.6",
                    "https://download.vrdesktop.net/files/VirtualDesktop.VRCFaceTracking.zip"
                };
                Check(downloads.Select(button => (ModuleKind)button.Tag).SequenceEqual(expectedKinds),
                    "Download choices are missing or are not ordered BFR, Baballonia, Steam Link, Virtual Desktop.");
                Check(downloads.Select(AutomationProperties.GetName).SequenceEqual(labels),
                    "Download choice accessibility labels do not identify the correct modules.");
                var sources = Descendants(choices).OfType<Button>()
                    .Where(button => AutomationProperties.GetName(button) == "Official module source").ToArray();
                Check(sources.Select(button => button.Tag as string).SequenceEqual(sourceLinks),
                    "A download choice links to the wrong official module source.");
                var priorTheme = themes.CurrentIsDark;
                foreach (var dark in new[] { false, true })
                {
                    themes.ApplyTheme(dark); Pump(); CheckDownloadLayout();
                    SavePreview(window, renderDirectory, dark ? "download-choices-dark.png" : "download-choices-light.png");
                }
                themes.ApplyTheme(priorTheme);
                Named<RadioButton>(window, "OverviewNav").IsChecked = true;
                Pump();
            });

            Test("the existing window changes its page, card, text and input surfaces live", () =>
            {
                var changes = 0;
                themes.ThemeChanged += (_, _) => changes++;
                themes.ApplyTheme(false);
                Pump();
                var light = DisplayedColors(window);
                SavePreview(window, renderDirectory, "light.png");
                themes.ApplyTheme(true);
                Pump();
                var dark = DisplayedColors(window);
                SavePreview(window, renderDirectory, "dark.png");
                Check(themes.CurrentIsDark, "Dark preview was not applied.");
                foreach (var key in light.Keys)
                    Check(light[key] != dark[key], $"Displayed {key} kept its light color after switching to dark.");
                Check(Brightness(dark["page"]) < Brightness(light["page"]), "Dark page is not darker than light.");
                Check(Contrast(dark["page"], dark["page text"]) >= 7, "Dark main text is difficult to read.");
                Check(Contrast(light["page"], light["page text"]) >= 7, "Light main text is difficult to read.");
                themes.ApplyTheme(false);
                Pump();
                foreach (var (key, value) in DisplayedColors(window))
                    Check(value == light[key], $"Displayed {key} did not return to its original light color.");
                Check(changes >= 2, "Theme changes did not notify listeners.");
            });

            Test("visible success and error notices follow a later theme change", () =>
            {
                foreach (var error in new[] { false, true })
                {
                    themes.ApplyTheme(false);
                    Method(window, "ShowNotice").Invoke(window, ["Appearance test", error]);
                    Pump();
                    var notice = Named<Border>(window, "GlobalNotice");
                    var noticeText = Named<TextBlock>(window, "GlobalNoticeText");
                    var lightBackground = ColorOf(notice.Background);
                    var lightText = ColorOf(noticeText.Foreground);
                    themes.ApplyTheme(true);
                    Pump();
                    Check(ColorOf(notice.Background) != lightBackground, "An open notice kept its light background.");
                    Check(ColorOf(noticeText.Foreground) != lightText, "An open notice kept its light foreground.");
                    Check(Contrast(ColorOf(notice.Background), ColorOf(noticeText.Foreground)) >= 4.5,
                        "Notice contrast is too low in dark mode.");
                }
            });

            Test("a Windows preference notification restores automatic mode on the existing window", () =>
            {
                var expected = SystemThemeManager.IsSystemDark();
                themes.ApplyTheme(!expected);
                // WM_SETTINGCHANGE is a synchronous message (it can carry a string pointer).
                SendMessage(new WindowInteropHelper(window).Handle, 0x001A, IntPtr.Zero, IntPtr.Zero);
                PumpUntil(() => themes.CurrentIsDark == expected, "Windows app mode applied after notification");
                Check(ColorOf(window.Background) == ColorOf((Brush)app.FindResource("Cream")),
                    "Displayed window did not follow the restored automatic palette.");
            });

            Test("the interface uses its module library without obsolete source controls or sidebar cards", () =>
            {
                foreach (var name in new[] { "HeadsetSourceButton", "DesktopSourceButton", "TrackingInputPort", "MotionBox" })
                    Check(window.FindName(name) is null, "An obsolete control remains: " + name);
                var text = string.Join("\n", Descendants(window).OfType<TextBlock>().Select(block => block.Text));
                foreach (var removed in new[] { "Made for your headset", "Appearance follows Windows", "Updates, with a way back", "Keep VRCFaceTracking closed" })
                    Check(!text.Contains(removed, StringComparison.OrdinalIgnoreCase), "An obsolete information card remains: " + removed);
                Check(Named<ItemsControl>(window, "ModuleLibrary").Items.Count == 0,
                    "The empty library displays a module that was not imported.");
            });

            Test("interrupted page and button motion settle cleanly and reduced motion cancels transforms", () =>
            {
                var previous = Motion.Enabled;
                var page = Named<StackPanel>(window, "OverviewPage");
                var button = Named<Button>(window, "ImportButton");
                try
                {
                    Motion.Enabled = true;
                    Motion.Transition(page, true);
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        PumpFor(TimeSpan.FromMilliseconds(65));
                        var displayed = page.Opacity;
                        Motion.Transition(page, true);
                        Check(page.Opacity >= displayed - .025, "Restarting a transition flashed the page back to zero opacity.");
                        PumpUntil(() => page.Opacity >= .999, "interrupted page settled");
                        PumpFor(TimeSpan.FromMilliseconds(80));
                        Motion.Transition(page, true);
                        PumpFor(TimeSpan.FromMilliseconds(35));
                        Check(page.Opacity < .98, "Returning to a completed page did not play its entrance again.");
                        PumpUntil(() => page.Opacity >= .999, "revisited page settled");
                        Motion.SetPressed(button, true);
                        PumpFor(TimeSpan.FromMilliseconds(70));
                        Check(button.RenderTransform.Value.M11 < .999, "Press feedback did not animate on the render transform.");
                        var width = button.ActualWidth;
                        Motion.SetPressed(button, false);
                        PumpUntil(() => button.RenderTransform.Value.M11 >= .999, "interrupted press feedback settled");
                        Check(Math.Abs(button.ActualWidth - width) < .001,
                            "Press feedback changed layout instead of its render transform.");
                    }
                    Motion.Enabled = false;
                    Motion.Transition(page, true);
                    Motion.SetPressed(button, true);
                    Check(Math.Abs(page.Opacity - 1) < .001 && Math.Abs(page.RenderTransform.Value.OffsetY) < .001,
                        "Reduced motion left a partially faded or displaced page.");
                    Check(Math.Abs(button.RenderTransform.Value.M11 - 1) < .001
                        && Math.Abs(button.RenderTransform.Value.OffsetY) < .001,
                        "Reduced motion left press or hover transforms active.");
                }
                finally
                {
                    button.ClearValue(Motion.PressedProperty);
                    Motion.Enabled = previous;
                    Motion.Transition(page, false);
                }
            });

            Test("particle motion follows display time and pauses on hidden pages, minimized windows and reduced motion", () =>
            {
                var particles = Named<ParticleField>(window, "HeroParticles");
                var preference = particles.AnimationsEnabled;
                try
                {
                    Named<RadioButton>(window, "OverviewNav").IsChecked = true;
                    Pump();
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        PumpUntil(() => Field<bool>(particles, "attached"), "visible particle animation attached");
                        if (Field<TimeSpan?>(particles, "lastFrame") is null) RenderParticlesAt(particles, TimeSpan.Zero);
                        var last = Field<TimeSpan?>(particles, "lastFrame")!.Value;
                        var before = Field<double>(particles, "seconds");
                        var next = last + TimeSpan.FromSeconds(1.0 / 144);
                        RenderParticlesAt(particles, next);
                        var advanced = Field<double>(particles, "seconds");
                        RenderParticlesAt(particles, next);
                        Check(Math.Abs(advanced - before - 1.0 / 144) < .00002,
                            "High-refresh motion did not use elapsed display time.");
                        Check(Field<double>(particles, "seconds") == advanced,
                            "A duplicate display tick advanced the particle simulation twice.");
                        Named<RadioButton>(window, "SettingsNav").IsChecked = true;
                        PumpUntil(() => !Field<bool>(particles, "attached"), "hidden page detached particle rendering");
                        var paused = Field<double>(particles, "seconds");
                        PumpFor(TimeSpan.FromMilliseconds(80));
                        Check(Field<double>(particles, "seconds") == paused, "Hidden particles continued animating.");
                        Named<RadioButton>(window, "OverviewNav").IsChecked = true;
                        PumpUntil(() => Field<bool>(particles, "attached"), "visible page resumed particles");
                        window.WindowState = WindowState.Minimized;
                        PumpUntil(() => !Field<bool>(particles, "attached"), "minimized window detached particle rendering");
                        window.WindowState = WindowState.Normal;
                        PumpUntil(() => Field<bool>(particles, "attached"), "restored window resumed particles");
                    }
                    particles.AnimationsEnabled = false;
                    Check(!Field<bool>(particles, "attached"), "Reduced motion retained its rendering callback.");
                    var stopped = Field<double>(particles, "seconds");
                    PumpFor(TimeSpan.FromMilliseconds(60));
                    Check(Field<double>(particles, "seconds") == stopped, "Reduced-motion particles advanced.");
                }
                finally { particles.AnimationsEnabled = preference; }
            });

            Test("Save connection confirmation dismisses itself after its readable display time", () =>
            {
                Method(window, "Save_Click").Invoke(window, [window, new RoutedEventArgs()]);
                PumpUntil(() => !Field<bool>(window, "_busy"), "connection save completed");
                Check(Named<Border>(window, "GlobalNotice").Visibility == Visibility.Visible,
                    "Saving did not show its confirmation.");
                Check(Named<TextBlock>(window, "GlobalNoticeText").Text.Contains("saved", StringComparison.OrdinalIgnoreCase),
                    "The save confirmation was replaced by an unexpected message.");
                PumpUntil(() => Named<Border>(window, "GlobalNotice").Visibility == Visibility.Collapsed,
                    "save confirmation automatically dismissed", TimeSpan.FromSeconds(6));
            });

            Test("new notices survive an older timeout and errors remain until dismissed", () =>
            {
                Method(window, "ShowNotice").Invoke(window, ["Old confirmation", false]);
                Field<DispatcherTimer>(window, "_noticeTimer").Interval = TimeSpan.FromMilliseconds(60);
                Method(window, "ShowNotice").Invoke(window, ["New confirmation", false]);
                PumpFor(TimeSpan.FromMilliseconds(250));
                Check(Named<Border>(window, "GlobalNotice").Visibility == Visibility.Visible
                    && Named<TextBlock>(window, "GlobalNoticeText").Text == "New confirmation",
                    "The earlier timeout dismissed a newer confirmation.");
                Field<DispatcherTimer>(window, "_noticeTimer").Interval = TimeSpan.FromMilliseconds(60);
                Method(window, "ShowNotice").Invoke(window, ["New error", true]);
                PumpFor(TimeSpan.FromSeconds(4.4));
                Check(Named<Border>(window, "GlobalNotice").Visibility == Visibility.Visible
                    && Named<TextBlock>(window, "GlobalNoticeText").Text == "New error",
                    "A success timeout dismissed the newer error.");
                Named<Button>(window, "GlobalNoticeDismiss").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(() => Named<Border>(window, "GlobalNotice").Visibility == Visibility.Collapsed,
                    "manual notification dismissal");
            });

            Test("a later launch restores connection preferences without a tracking source selector", () =>
            {
                Named<CheckBox>(window, "AutoStartBox").IsChecked = false;
                Named<TextBox>(window, "HostBox").Text = "127.0.0.2";
                Named<TextBox>(window, "PortBox").Text = "19000";
                Named<TextBox>(window, "ReceiveBox").Text = "19001";
                Method(window, "Save_Click").Invoke(window, [window, new RoutedEventArgs()]);
                PumpUntil(() => !Field<bool>(window, "_busy"), "connection preferences saved");
                var noticeTimer = Field<DispatcherTimer>(window, "_noticeTimer");
                CloseWindow();
                Check(!noticeTimer.IsEnabled, "Closing the window left its notification timer running.");
                window = CreateWindow(emptyProfile);
                themes.AttachWindow(window);
                window.Show();
                PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "saved profile reopened");
                var restored = AppSettings.Load(emptyProfile);
                Check(restored.OutputHost == "127.0.0.2" && restored.OutputPort == 19000
                    && restored.ReceivePort == 19001 && !restored.AutoStart,
                    "Reopening changed saved connection preferences.");
                Check(Named<TextBox>(window, "PortBox").Text == "19000"
                    && Named<TextBox>(window, "ReceiveBox").Text == "19001",
                    "The visible VRChat ports did not restore their saved values.");
                Check(Field<object?>(window, "_worker") is null, "Connection edits launched a worker without a module.");
                Named<RadioButton>(window, "SettingsNav").IsChecked = true;
                PumpUntil(() => Named<StackPanel>(window, "SettingsPage").Opacity >= .999,
                    "settings entrance finished before preview capture");
                themes.ApplyTheme(false); Pump(); SavePreview(window, renderDirectory, "connection-light.png");
                themes.ApplyTheme(true); Pump(); SavePreview(window, renderDirectory, "connection-dark.png");
            });

            CloseWindow();
            if (suppliedZip is not null)
            {
                Test("an explicitly imported module survives restart and enables Start without running it", () =>
                {
                    Check(File.Exists(suppliedZip), "The explicit module fixture does not exist.");
                    var profile = Path.Combine(root, "existing-profile");
                    new AppSettings { AutoStart = false, LinkPort = 23456 }.Save(profile);
                    var expected = new ModuleStore(profile).Install(suppliedZip);
                    var configPath = Path.Combine(expected.DirectoryPath, "BabbleConfig.json");
                    var moduleConfig = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
                    moduleConfig["Port"] = 42424;
                    File.WriteAllText(configPath, moduleConfig.ToJsonString());
                    window = CreateWindow(profile);
                    themes.AttachWindow(window);
                    window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"),
                        "existing imported module finished loading");
                    Check(new ModuleStore(profile).Current == expected, "The existing imported module changed on startup.");
                    Check(Named<Button>(window, "StartButton").IsEnabled, "Imported module did not enable Start.");
                    Check(Named<Button>(window, "ImportButton").IsEnabled, "Imported module disabled future imports.");
                    Check(Field<object?>(window, "_worker") is null, "AutoStart=false launched a worker.");
                    Check(expected.Kind == ModuleKind.BabbleHeadsetRuntime,
                        "The first compatible Babble import did not default to headset runtime.");
                    Check(Named<TextBlock>(window, "PackageDetail").Text.Contains("23456"), "Custom headset input port was not shown.");
                    PumpUntil(() => Named<StackPanel>(window, "OverviewPage").Opacity >= .999,
                        "module overview entrance finished before preview capture");
                    SavePreview(window, renderDirectory, "module-ready.png");
                    CloseWindow();
                    var store = new ModuleStore(profile);
                    var desktop = store.CreateCompanion(expected);
                    store.Register(desktop);
                    store.Activate(desktop);
                    window = CreateWindow(profile);
                    themes.AttachWindow(window);
                    window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "desktop library selection reopened");
                    Check(new ModuleStore(profile).Current == desktop, "Reopening did not restore the selected desktop module.");
                    Check(Field<object?>(window, "_worker") is null, "Restoring the selected library entry ignored AutoStart=false.");
                    Check(Named<TextBlock>(window, "PackageDetail").Text.Contains("42424"), "Custom desktop module input port was not shown.");
                    Check(JsonNode.Parse(File.ReadAllText(configPath))!["Port"]!.GetValue<int>() == 42424,
                        "Selecting the cloned desktop profile changed the original module's configured port.");
                    Check(desktop.DirectoryPath != expected.DirectoryPath,
                        "Desktop and headset selections shared their mutable module directory.");
                    Named<RadioButton>(window, "ModulesNav").IsChecked = true;
                    PumpUntil(() => Named<StackPanel>(window, "ModulesPage").Opacity >= .999,
                        "module page entrance finished before preview capture");
                    Check(Named<ItemsControl>(window, "ModuleLibrary").Items.Count == 2,
                        "The saved library did not retain both Babble connection profiles.");
                    var cards = string.Join("\n", Descendants(Named<ItemsControl>(window, "ModuleLibrary"))
                        .OfType<TextBlock>().Select(block => block.Text));
                    Check(cards.Contains("UDP 23456") && cards.Contains("UDP 42424"),
                        "The library did not show the distinct input ports of both profiles.");
                    CheckLibraryLayout();
                    SavePreview(window, renderDirectory, "modules.png");
                    var desktopConfig = Path.Combine(desktop.DirectoryPath, "BabbleConfig.json");
                    foreach (var malformed in new[] { "42", "null", "[]", "{\"Port\":\"8888\"}", "{\"Port\":null}", "{\"Port\":true}", "{\"Port\":-1}", "{\"Port\":65536}" })
                    {
                        File.WriteAllText(desktopConfig, malformed);
                        Method(window, "RefreshModule").Invoke(window, []);
                        Check(Named<TextBlock>(window, "PackageDetail").Text.Contains("8888"),
                            "Malformed module settings crashed library refresh or failed to use the fallback input port: " + malformed);
                    }
                    CloseWindow();
                });
            }

            if (suppliedZip is not null && virtualDesktopZip is not null && steamLinkZip is not null)
            {
                Test("all four imported connection profiles show their own input and retain their selected entry", () =>
                {
                    var profile = Path.Combine(root, "provider-library-profile");
                    new AppSettings { AutoStart = false }.Save(profile);
                    var store = new ModuleStore(profile);
                    var headset = store.Prepare(suppliedZip);
                    var desktop = store.CreateCompanion(headset);
                    var virtualDesktop = store.Prepare(virtualDesktopZip);
                    var steamLink = store.Prepare(steamLinkZip);
                    var releases = new[] { headset, desktop, virtualDesktop, steamLink };
                    foreach (var release in releases) store.Register(release);
                    foreach (var release in releases)
                    {
                        store.Activate(release);
                        window = CreateWindow(profile);
                        themes.AttachWindow(window);
                        window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "provider library reopened");
                        Check(new ModuleStore(profile).Current == release, "The selected provider changed on reopening.");
                        var expectedInput = release.Kind switch
                        {
                            ModuleKind.BabbleHeadsetRuntime => "6767",
                            ModuleKind.BaballoniaDesktop => "8888",
                            ModuleKind.VirtualDesktop => "Shared memory",
                            ModuleKind.SteamLink => "9015",
                            _ => throw new InvalidOperationException()
                        };
                        Check(Named<TextBlock>(window, "PackageDetail").Text.Contains(expectedInput),
                            "The selected provider displayed the wrong input: " + release.Kind);
                        Check(Named<TextBox>(window, "PortBox").Text == "9000"
                            && Named<TextBox>(window, "ReceiveBox").Text == "9001",
                            "Selecting a module overwrote the separate VRChat ports.");
                        Check(Named<ItemsControl>(window, "ModuleLibrary").Items.Count == 4,
                            "Reopening lost entries from the four-provider library.");
                        Named<RadioButton>(window, "ModulesNav").IsChecked = true;
                        Pump();
                        CheckLibraryLayout();
                        if (release.Kind == ModuleKind.SteamLink) SavePreview(window, renderDirectory, "all-modules.png");
                        CloseWindow();
                    }
                });
            }

            if (suppliedZip is not null && published is not null)
            {
                Test("module ZIP updates activate only after startup, recover from failure, and roll back across relaunch", () =>
                {
                    var executable = Path.Combine(published, "runtime", "BabbleFrameBridge.Worker.exe");
                    Check(File.Exists(executable), "The explicitly supplied published worker does not exist.");
                    var profile = Path.Combine(root, "module-update-profile");
                    var fixtures = Path.Combine(root, "module-update-fixtures");
                    Directory.CreateDirectory(fixtures);
                    using var output = ReservePort();
                    using var occupied = ReservePort();
                    var ports = Enumerable.Range(0, 3).Select(_ => ReservePort()).ToArray();
                    var receivePort = PortOf(ports[0]);
                    var linkPort = PortOf(ports[1]);
                    var inputPort = PortOf(ports[2]);
                    foreach (var reservation in ports) reservation.Dispose();
                    new AppSettings
                    {
                        AutoStart = false, OutputPort = PortOf(output), ReceivePort = receivePort,
                        LinkPort = linkPort, InputSource = TrackingInputSource.HeadsetRuntime
                    }.Save(profile);
                    var firstZip = ModuleFixture(suppliedZip, fixtures, "4.0.0", inputPort, linkPort);
                    var updateZip = ModuleFixture(suppliedZip, fixtures, "4.0.1", inputPort, linkPort);
                    var rejectedZip = ModuleFixture(suppliedZip, fixtures, "4.0.2", PortOf(occupied), linkPort);
                    try
                    {
                        window = CreateWindow(profile, executable);
                        themes.AttachWindow(window);
                        window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "update profile loaded");
                        InvokeModuleImport(firstZip);
                        var original = new ModuleStore(profile).Current;
                        Check(original?.Version == "4.0.0" && Field<bool>(window, "_running"),
                            "The first module did not activate after the real worker initialized.");
                        Check(Field<Process>(window, "_worker").StartInfo.ArgumentList.Contains("--no-discovery"),
                            "The isolated worker could discover a live VRChat session.");
                        var originalPid = Field<Process>(window, "_worker").Id;
                        var settingsPath = Path.Combine(profile, "settings.json");
                        var savedConnection = File.ReadAllText(settingsPath);
                        Named<TextBox>(window, "PortBox").Text = inputPort.ToString();
                        InvokeSaveConnection();
                        Check(Field<bool>(window, "_running") && Field<Process>(window, "_worker").Id == originalPid,
                            "Rejecting a tracking-input/VRChat-port collision stopped the active worker.");
                        Check(File.ReadAllText(settingsPath) == savedConnection,
                            "An invalid connection was persisted before validation.");
                        Named<TextBox>(window, "PortBox").Text = PortOf(output).ToString();
                        Named<TextBox>(window, "ReceiveBox").Text = PortOf(occupied).ToString();
                        InvokeSaveConnection();
                        Check(Field<bool>(window, "_running") && File.ReadAllText(settingsPath) == savedConnection,
                            "An occupied new feedback port did not restore the saved connection and live worker.");
                        Check(Named<TextBox>(window, "ReceiveBox").Text == receivePort.ToString(),
                            "Failed connection startup left the invalid feedback port in the interface.");
                        Check(Named<TextBlock>(window, "GlobalNoticeText").Text.Contains("previous connection settings were restored"),
                            "Failed connection startup did not explain that the previous settings were restored.");
                        Check(original!.Kind == ModuleKind.BabbleHeadsetRuntime,
                            "The first real Babble import did not select headset runtime.");
                        var library = new ModuleStore(profile).Installed;
                        var desktop = library.Single(release => release.Kind == ModuleKind.BaballoniaDesktop);
                        Check(library.Count == 2 && desktop.DirectoryPath != original.DirectoryPath,
                            "The imported Babble ZIP did not add isolated headset and desktop profiles.");
                        InvokeModuleSelection(desktop);
                        Check(new ModuleStore(profile).Current == desktop && Field<bool>(window, "_running"),
                            "Selecting the desktop library entry did not activate its worker.");
                        Check(WorkerArgument("--kind") == ModuleKind.BaballoniaDesktop.ToString()
                            && WorkerArgument("--input-port") == inputPort.ToString(),
                            "Desktop hot swap started the wrong input transport or port.");
                        Check(Field<Process>(window, "_worker").Id != originalPid,
                            "Hot swap left the old worker running.");
                        InvokeModuleSelection(original);
                        Check(new ModuleStore(profile).Current == original && Field<bool>(window, "_running"),
                            "Switching back did not restore the headset module.");
                        Check(WorkerArgument("--kind") == ModuleKind.BabbleHeadsetRuntime.ToString()
                            && WorkerArgument("--input-port") == linkPort.ToString(),
                            "Returning to headset runtime did not restore its input port.");

                        InvokeModuleImport(updateZip);
                        var updated = new ModuleStore(profile).Current;
                        Check(updated?.Version == "4.0.1" && new ModuleStore(profile).Previous == original,
                            "A successful update did not preserve the previous module.");
                        Check(Field<bool>(window, "_running") && Field<Process>(window, "_worker").Id != originalPid,
                            "Updating did not replace the active worker.");

                        // The candidate is a structurally valid package. Its own reserved test port
                        // makes startup fail after the prior worker has stopped, exercising recovery.
                        InvokeModuleImport(rejectedZip);
                        var recovered = new ModuleStore(profile);
                        Check(recovered.Current == updated && recovered.Previous == original,
                            "A failed startup changed the saved active or rollback module.");
                        Check(Field<bool>(window, "_running"), "The previous worker did not resume after the failed update.");
                        Check(Named<Border>(window, "GlobalNotice").Visibility == Visibility.Visible
                            && Named<TextBlock>(window, "GlobalNoticeText").Text.Contains("The new module could not start."),
                            "The failed update did not leave a clear error notification.");
                        Check(Named<TextBox>(window, "LogBox").Text.Contains("Restored the previous active module."),
                            "The UI did not confirm recovery of the previous worker.");

                        InvokeRollback();
                        Check(new ModuleStore(profile).Current == original && new ModuleStore(profile).Previous == updated,
                            "Rollback did not switch to the saved original module.");
                        Check(Field<bool>(window, "_running"), "Rollback did not restart the original module.");
                        CloseWindow();
                        window = CreateWindow(profile, executable);
                        themes.AttachWindow(window);
                        window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "rolled-back profile reopened");
                        Check(new ModuleStore(profile).Current == original && new ModuleStore(profile).Previous == updated,
                            "Relaunch lost the active and rollback module selections.");
                        Check(Field<object?>(window, "_worker") is null && Named<Button>(window, "StartButton").IsEnabled,
                            "Relaunch did not retain the saved AutoStart=false preference and module availability.");
                    }
                    finally { CloseWindow(); }
                });
            }

            if (suppliedZip is not null)
            {
                Test("red module trash buttons ask inline and cancel without changing the library in either theme", () =>
                {
                    var profile = Path.Combine(root, "module-removal-confirm-profile");
                    new AppSettings { AutoStart = false }.Save(profile);
                    var store = new ModuleStore(profile);
                    var headset = store.Install(suppliedZip);
                    var desktop = store.CreateCompanion(headset);
                    store.Register(desktop);
                    var savedState = File.ReadAllText(Path.Combine(profile, "modules", "module-state.json"));
                    var originalZipHash = HashFile(suppliedZip);
                    try
                    {
                        window = CreateWindow(profile);
                        themes.AttachWindow(window);
                        window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "removal confirmation profile loaded");
                        Named<RadioButton>(window, "ModulesNav").IsChecked = true;
                        foreach (var dark in new[] { false, true })
                        {
                            themes.ApplyTheme(dark); Pump();
                            var trash = CardButton(headset, "Remove module");
                            var color = ColorOf(trash.Foreground);
                            Check(color.R > color.G + 20 && color.R > color.B + 10,
                                "The module trash icon does not use a distinct red color.");
                            CheckLibraryLayout();
                            InvokeCardButton(headset, "Remove module");
                            Check(CardButton(headset, "Confirm remove module").IsVisible
                                && CardButton(headset, "Cancel removal").IsVisible,
                                "The trash button did not reveal inline confirmation and cancel actions.");
                            Check(new ModuleStore(profile).Installed.Count == 2 && Directory.Exists(headset.DirectoryPath),
                                "Opening confirmation removed the module before confirmation.");
                            PumpFor(TimeSpan.FromMilliseconds(260));
                            CheckLibraryLayout();
                            SavePreview(window, renderDirectory, dark ? "module-remove-confirm-dark.png" : "module-remove-confirm-light.png");
                            InvokeCardButton(headset, "Cancel removal");
                            Check(!CardButtons(headset).Any(button => button.IsVisible
                                && AutomationProperties.GetName(button) == "Confirm remove module"),
                                "Cancel left a destructive confirmation visible.");
                            Check(File.ReadAllText(Path.Combine(profile, "modules", "module-state.json")) == savedState,
                                "Cancel changed the saved module library.");
                            Check(new ModuleStore(profile).Current == headset && Directory.Exists(desktop.DirectoryPath),
                                "Cancel changed the selected module or removed its companion.");
                        }
                        Check(HashFile(suppliedZip) == originalZipHash, "The original supplied ZIP changed during confirmation/cancel.");
                    }
                    finally { CloseWindow(); }
                });
            }

            if (suppliedZip is not null && published is not null)
            {
                Test("confirmed removal preserves an inactive worker, clears an active selection, and persists an empty library", () =>
                {
                    var executable = Path.Combine(published, "runtime", "BabbleFrameBridge.Worker.exe");
                    var profile = Path.Combine(root, "module-removal-worker-profile");
                    var fixtures = Path.Combine(root, "module-removal-worker-fixtures");
                    Directory.CreateDirectory(fixtures);
                    using var output = ReservePort();
                    var reservations = Enumerable.Range(0, 3).Select(_ => ReservePort()).ToArray();
                    var receivePort = PortOf(reservations[0]);
                    var linkPort = PortOf(reservations[1]);
                    var inputPort = PortOf(reservations[2]);
                    foreach (var reservation in reservations) reservation.Dispose();
                    new AppSettings { AutoStart = false, OutputPort = PortOf(output), ReceivePort = receivePort, LinkPort = linkPort }.Save(profile);
                    var firstZip = ModuleFixture(suppliedZip, fixtures, "4.0.0", inputPort, linkPort);
                    var updateZip = ModuleFixture(suppliedZip, fixtures, "4.0.1", inputPort, linkPort);
                    var originalZipHash = HashFile(suppliedZip);
                    var fixtureZipHash = HashFile(firstZip);
                    try
                    {
                        window = CreateWindow(profile, executable);
                        themes.AttachWindow(window);
                        window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "removal worker profile loaded");
                        InvokeModuleImport(firstZip);
                        var installed = new ModuleStore(profile);
                        var selected = installed.Current!;
                        var inactive = installed.Installed.Single(release => release.Kind == ModuleKind.BaballoniaDesktop);
                        var originalPid = Field<Process>(window, "_worker").Id;
                        InvokeCardButton(selected, "Remove module");
                        InvokeCardButton(selected, "Cancel removal");
                        Check(Field<bool>(window, "_running") && Field<Process>(window, "_worker").Id == originalPid,
                            "Canceling active-module removal interrupted tracking.");
                        using (var lockedBackup = new FileStream(Path.Combine(profile, "modules", "module-state.json.backup"),
                            FileMode.Open, FileAccess.Read, FileShare.None))
                        {
                            InvokeCardButton(selected, "Remove module");
                            InvokeCardButton(selected, "Confirm remove module");
                            Check(new ModuleStore(profile).Current == selected && Directory.Exists(selected.DirectoryPath),
                                "A blocked removal changed the selected module or moved its installed payload.");
                            Check(Field<bool>(window, "_running") && Field<Process>(window, "_worker").Id != originalPid,
                                "A failed active-module removal did not restore its tracking worker.");
                            Check(Named<Border>(window, "GlobalNotice").Visibility == Visibility.Visible,
                                "A failed removal did not leave an error notification.");
                        }
                        originalPid = Field<Process>(window, "_worker").Id;
                        InvokeCardButton(inactive, "Remove module");
                        InvokeCardButton(inactive, "Confirm remove module");
                        var afterInactive = new ModuleStore(profile);
                        Check(afterInactive.Current == selected && afterInactive.Installed.Count == 1,
                            "Removing an inactive entry changed the active selection or removed another module.");
                        Check(Field<bool>(window, "_running") && Field<Process>(window, "_worker").Id == originalPid,
                            "Removing an inactive entry stopped or restarted the active tracking worker.");
                        Check(!Directory.Exists(inactive.DirectoryPath) && Directory.Exists(selected.DirectoryPath),
                            "Inactive removal did not limit file deletion to the requested installed entry.");
                        Check(HashFile(firstZip) == fixtureZipHash && HashFile(suppliedZip) == originalZipHash,
                            "Removing an installed entry deleted or changed a source ZIP.");

                        InvokeModuleImport(updateZip);
                        var updated = new ModuleStore(profile).Current!;
                        Check(updated.Id != selected.Id && new ModuleStore(profile).Installed.Count == 3,
                            "The update fixture did not retain the earlier module and add the new profiles.");
                        InvokeCardButton(updated, "Remove module");
                        InvokeCardButton(updated, "Confirm remove module");
                        var afterActive = new ModuleStore(profile);
                        Check(afterActive.Current is null && afterActive.Installed.Count == 2,
                            "Removing the selected module automatically selected another entry or lost the remaining library.");
                        Check(!Field<bool>(window, "_running") && Field<object?>(window, "_worker") is null,
                            "Removing the active module left its worker running.");
                        Check(!Named<Button>(window, "StartButton").IsEnabled && !Directory.Exists(updated.DirectoryPath),
                            "The removed active module can still start or its installed payload was retained.");
                        CheckLibraryReferences(afterActive);
                        Named<CheckBox>(window, "AutoStartBox").IsChecked = true;
                        CloseWindow();
                        window = CreateWindow(profile, executable);
                        themes.AttachWindow(window);
                        window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "removed active selection reopened");
                        Check(new ModuleStore(profile).Current is null && new ModuleStore(profile).Installed.Count == 2
                            && Field<object?>(window, "_worker") is null,
                            "Reopening restored a deleted selection, launched a worker, or lost remaining entries.");
                        foreach (var remaining in new ModuleStore(profile).Installed.ToArray())
                        {
                            InvokeCardButton(remaining, "Remove module");
                            InvokeCardButton(remaining, "Confirm remove module");
                            CheckLibraryReferences(new ModuleStore(profile));
                        }
                        var empty = new ModuleStore(profile);
                        Check(empty.Installed.Count == 0 && empty.Current is null && empty.Previous is null,
                            "Removing the final entry left an installed selection or rollback reference.");
                        Check(!Named<Button>(window, "StartButton").IsEnabled && !Named<Button>(window, "RollbackButton").IsEnabled,
                            "Start or Restore remains enabled after the final entry was removed.");
                        Check(HashFile(firstZip) == fixtureZipHash && HashFile(suppliedZip) == originalZipHash && File.Exists(updateZip),
                            "Library removal affected a source module ZIP.");
                        CloseWindow();
                        window = CreateWindow(profile, executable);
                        themes.AttachWindow(window);
                        window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "empty removed library reopened");
                        Check(new ModuleStore(profile).Installed.Count == 0 && Named<ItemsControl>(window, "ModuleLibrary").Items.Count == 0,
                            "The empty module library did not persist across relaunch.");
                        Check(Named<Button>(window, "ImportButton").IsEnabled && !Named<Button>(window, "StartButton").IsEnabled,
                            "An empty reopened library did not offer import with tracking stopped.");
                    }
                    finally { CloseWindow(); }
                });
            }

            Test("failed and canceled in-app downloads leave an empty profile unchanged without network access", () =>
            {
                var profile = Path.Combine(root, "download-empty-failure-profile");
                using var handler = new FixtureDownloadHandler(new Dictionary<Uri, byte[]>());
                using var client = new HttpClient(handler);
                using var downloader = new ModuleDownloader(client);
                try
                {
                    window = CreateWindow(profile, moduleDownloader: downloader);
                    themes.AttachWindow(window); window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "empty download profile loaded");
                    handler.SetMode(DownloadResponse.Failure);
                    InvokeDownload(ModuleKind.SteamLink);
                    Check(handler.Requests.Single() == ModuleDownloader.SourceFor(ModuleKind.SteamLink).DownloadUri,
                        "The in-app downloader did not request the selected official ZIP.");
                    Check(new ModuleStore(profile).Installed.Count == 0 && new ModuleStore(profile).Current is null
                        && Field<object?>(window, "_worker") is null,
                        "An HTTP failure added or selected a module or started tracking.");
                    Check(Named<Border>(window, "GlobalNotice").Visibility == Visibility.Visible,
                        "A failed download did not display a useful error notification.");
                    Check(Named<Border>(window, "DownloadProgressPanel").Visibility == Visibility.Collapsed,
                        "Download progress remained open after an HTTP failure.");
                    handler.SetMode(DownloadResponse.WaitForCancellation);
                    BeginButtonClick(DownloadButton(ModuleKind.VirtualDesktop));
                    PumpUntil(() => handler.RequestStarted.Task.IsCompleted && Field<bool>(window, "_busy"), "cancellable download began");
                    Check(Named<Border>(window, "DownloadProgressPanel").Visibility == Visibility.Visible
                        && Named<Button>(window, "DownloadCancelButton").IsEnabled,
                        "An in-flight download has no visible enabled cancellation action.");
                    Named<Border>(window, "DownloadProgressPanel").BringIntoView(); Pump();
                    SavePreview(window, renderDirectory, "download-progress.png");
                    BeginButtonClick(Named<Button>(window, "DownloadCancelButton"));
                    PumpUntil(() => !Field<bool>(window, "_busy"), "canceled download completed");
                    Check(handler.ObservedCancellation, "The cancel button did not cancel the injected HTTP request.");
                    Check(new ModuleStore(profile).Installed.Count == 0 && Field<object?>(window, "_worker") is null,
                        "Canceling a download added a module or started tracking.");
                    Check(DownloadArchives(profile).Length == 0 && !Directory.EnumerateFiles(profile, "*.partial", SearchOption.AllDirectories).Any(),
                        "A failed or canceled download left a completed or partial ZIP behind.");
                    Check(Named<Border>(window, "DownloadProgressPanel").Visibility == Visibility.Collapsed
                        && DownloadButton(ModuleKind.BabbleHeadsetRuntime).IsEnabled,
                        "Canceling left progress visible or prevented another download.");
                }
                finally { CloseWindow(); }
            });

            Test("closing during a module transfer awaits cancellation and removes the partial download before the window disappears", () =>
            {
                var profile = Path.Combine(root, "download-close-mid-transfer-profile");
                byte[] payload;
                using (var bytes = new MemoryStream())
                {
                    using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
                    using (var writer = new StreamWriter(archive.CreateEntry("synthetic-transfer.txt").Open()))
                        writer.Write("A harmless partial-transfer fixture that is canceled before any module import.");
                    payload = bytes.ToArray();
                }
                using var handler = new FixtureDownloadHandler(new Dictionary<Uri, byte[]>
                {
                    [ModuleDownloader.SourceFor(ModuleKind.BabbleHeadsetRuntime).DownloadUri] = payload
                });
                using var client = new HttpClient(handler);
                using var downloader = new ModuleDownloader(client);
                try
                {
                    handler.SetMode(DownloadResponse.WaitDuringTransfer);
                    window = CreateWindow(profile, moduleDownloader: downloader);
                    themes.AttachWindow(window); window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "mid-transfer closing profile loaded");
                    BeginButtonClick(DownloadButton(ModuleKind.BabbleHeadsetRuntime));
                    PumpUntil(() => handler.TransferWaiting.Task.IsCompleted
                        && Directory.EnumerateFiles(profile, "*.partial", SearchOption.AllDirectories).Any(),
                        "an initial module chunk was written before closing");
                    var closing = window;
                    closing.Dispatcher.BeginInvoke(new Action(closing.Close));
                    PumpUntil(() => !closing.IsVisible, "window waited for transfer cancellation before closing", TimeSpan.FromSeconds(10));
                    window = null;
                    Check(handler.ObservedCancellation && Field<Task>(closing, "_downloadTask").IsCompleted,
                        "The window closed before its active download had finished canceling.");
                    Check(!Directory.EnumerateFiles(profile, "*.partial", SearchOption.AllDirectories).Any()
                        && DownloadArchives(profile).Length == 0
                        && !Directory.EnumerateDirectories(Path.Combine(profile, "downloads")).Any(),
                        "Closing mid-transfer left a partial ZIP or orphaned download folder.");
                    Check(new ModuleStore(profile).Current is null && new ModuleStore(profile).Installed.Count == 0,
                        "A canceled closing transfer selected or imported a module.");
                    Check(Named<Border>(closing, "DownloadProgressPanel").Visibility == Visibility.Collapsed,
                        "The finished closing download did not complete its progress cleanup.");
                }
                finally { CloseWindow(); }
            });

            if (suppliedZip is not null && virtualDesktopZip is not null && steamLinkZip is not null)
            {
                Test("all four in-app downloads import genuine ZIPs without activating modules on a fresh auto-start profile", () =>
                {
                    var profile = Path.Combine(root, "download-four-provider-profile");
                    var packages = new Dictionary<Uri, byte[]>
                    {
                        [ModuleDownloader.SourceFor(ModuleKind.BabbleHeadsetRuntime).DownloadUri] = File.ReadAllBytes(suppliedZip),
                        [ModuleDownloader.SourceFor(ModuleKind.SteamLink).DownloadUri] = File.ReadAllBytes(steamLinkZip),
                        [ModuleDownloader.SourceFor(ModuleKind.VirtualDesktop).DownloadUri] = File.ReadAllBytes(virtualDesktopZip)
                    };
                    using var handler = new FixtureDownloadHandler(packages);
                    using var client = new HttpClient(handler);
                    using var downloader = new ModuleDownloader(client);
                    try
                    {
                        window = CreateWindow(profile, moduleDownloader: downloader);
                        themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "four-provider download profile loaded");
                        Check(AppSettings.Load(profile).AutoStart, "The download test did not cover the new-profile auto-start preference.");
                        foreach (var kind in new[] { ModuleKind.BabbleHeadsetRuntime, ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink, ModuleKind.VirtualDesktop })
                        {
                            var priorArchives = DownloadArchives(profile).ToHashSet(StringComparer.OrdinalIgnoreCase);
                            InvokeDownload(kind);
                            var store = new ModuleStore(profile);
                            Check(store.Installed.Any(release => release.Kind == kind), "Download did not add the selected provider: " + kind);
                            if (kind == ModuleKind.BabbleHeadsetRuntime)
                                Check(store.Installed.Count == 2 && store.Installed.Any(release => release.Kind == ModuleKind.BaballoniaDesktop),
                                    "The headset download did not add independent headset and desktop profiles.");
                            Check(store.Current is null && Field<object?>(window, "_worker") is null
                                && !Named<Button>(window, "StartButton").IsEnabled,
                                "Downloading a module automatically selected or started it.");
                            var archive = DownloadArchives(profile).Single(path => !priorArchives.Contains(path));
                            Check(Path.GetFileName(archive) == ModuleDownloader.SourceFor(kind).FileName,
                                "The cached download did not retain its official ZIP filename.");
                            Check(HashFile(archive) == Convert.ToHexString(SHA256.HashData(packages[ModuleDownloader.SourceFor(kind).DownloadUri])),
                                "The completed cached ZIP differs from the genuine response fixture.");
                            Check(handler.Requests.Last() == ModuleDownloader.SourceFor(kind).DownloadUri,
                                "The download button requested a different provider's URL.");
                        }
                        Check(handler.Requests.Count == 4 && DownloadArchives(profile).Length == 4,
                            "Each requested module did not retain its own completed ZIP.");
                        Check(new ModuleStore(profile).Installed.Count == 4,
                            "Choosing both Babble download options duplicated their shared package profiles.");
                        var orderedCards = Descendants(Named<ItemsControl>(window, "ModuleLibrary")).OfType<Button>()
                            .Where(button => button.Tag is ModuleRelease && button.Content is string action
                                && (action == "Use module" || action.StartsWith("Selected", StringComparison.Ordinal)))
                            .Select(button => ((ModuleRelease)button.Tag).Kind);
                        Check(orderedCards.SequenceEqual(new[] { ModuleKind.BabbleHeadsetRuntime, ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink, ModuleKind.VirtualDesktop }),
                            "The installed library does not follow the requested provider order.");
                        CloseWindow();
                        window = CreateWindow(profile, moduleDownloader: downloader);
                        themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "downloaded library reopened");
                        Check(new[] { ModuleKind.BabbleHeadsetRuntime, ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink, ModuleKind.VirtualDesktop }
                            .All(kind => new ModuleStore(profile).Installed.Any(release => release.Kind == kind))
                            && new ModuleStore(profile).Current is null && Field<object?>(window, "_worker") is null,
                            "Reopening lost downloaded providers or activated a module without an explicit selection.");
                    }
                    finally { CloseWindow(); }
                });
            }

            if (suppliedZip is not null && published is not null)
            {
                Test("in-app download success, failure and cancellation preserve a running module and its connection", () =>
                {
                    var profile = Path.Combine(root, "download-running-profile");
                    var fixtures = Path.Combine(root, "download-running-fixtures");
                    Directory.CreateDirectory(fixtures);
                    using var output = ReservePort();
                    var reservations = Enumerable.Range(0, 3).Select(_ => ReservePort()).ToArray();
                    var receivePort = PortOf(reservations[0]); var linkPort = PortOf(reservations[1]); var inputPort = PortOf(reservations[2]);
                    foreach (var reservation in reservations) reservation.Dispose();
                    new AppSettings { AutoStart = false, OutputPort = PortOf(output), ReceivePort = receivePort, LinkPort = linkPort }.Save(profile);
                    var firstZip = ModuleFixture(suppliedZip, fixtures, "4.0.0", inputPort, linkPort);
                    using var handler = new FixtureDownloadHandler(new Dictionary<Uri, byte[]>
                    {
                        [ModuleDownloader.SourceFor(ModuleKind.BabbleHeadsetRuntime).DownloadUri] = File.ReadAllBytes(suppliedZip)
                    });
                    using var client = new HttpClient(handler);
                    using var downloader = new ModuleDownloader(client);
                    try
                    {
                        window = CreateWindow(profile, Path.Combine(published, "runtime", "BabbleFrameBridge.Worker.exe"), downloader);
                        themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "running download profile loaded");
                        InvokeModuleImport(firstZip);
                        var original = new ModuleStore(profile).Current;
                        var originalIds = new ModuleStore(profile).Installed.Select(release => release.Id).ToHashSet();
                        var originalPid = Field<Process>(window, "_worker").Id;
                        var settings = File.ReadAllText(Path.Combine(profile, "settings.json"));
                        void CheckActiveUnchanged()
                        {
                            Check(new ModuleStore(profile).Current == original && Field<bool>(window!, "_running")
                                && Field<Process>(window!, "_worker").Id == originalPid,
                                "A module download interrupted or changed the active tracking worker.");
                            Check(File.ReadAllText(Path.Combine(profile, "settings.json")) == settings,
                                "A module download changed the saved VRChat connection.");
                        }
                        InvokeDownload(ModuleKind.BabbleHeadsetRuntime);
                        CheckActiveUnchanged();
                        var afterSuccess = new ModuleStore(profile).Installed;
                        Check(afterSuccess.Count == originalIds.Count + 2, "The successful Babble download did not add both available profiles.");
                        var cache = DownloadArchives(profile).Single();
                        var cacheHash = HashFile(cache);
                        var downloadedHeadset = afterSuccess.Single(release => !originalIds.Contains(release.Id)
                            && release.Kind == ModuleKind.BabbleHeadsetRuntime);
                        var downloadedDesktop = afterSuccess.Single(release => !originalIds.Contains(release.Id)
                            && release.Kind == ModuleKind.BaballoniaDesktop);
                        var configuredPath = Path.Combine(downloadedHeadset.DirectoryPath, "BabbleConfig.json");
                        var configured = JsonNode.Parse(File.ReadAllText(configuredPath))!.AsObject();
                        configured["Port"] = 45678;
                        File.WriteAllText(configuredPath, configured.ToJsonString());
                        var configuredHash = HashFile(configuredPath);
                        var initialDownloadedIds = afterSuccess.Select(release => release.Id).ToHashSet();
                        foreach (var repeated in new[] { ModuleKind.BabbleHeadsetRuntime, ModuleKind.BaballoniaDesktop })
                        {
                            InvokeDownload(repeated);
                            CheckActiveUnchanged();
                            Check(new ModuleStore(profile).Installed.Select(release => release.Id).ToHashSet().SetEquals(initialDownloadedIds)
                                && HashFile(configuredPath) == configuredHash,
                                "A repeated Babble download duplicated profiles or overwrote configured module settings.");
                        }
                        InvokeCardButton(downloadedDesktop, "Remove module");
                        InvokeCardButton(downloadedDesktop, "Confirm remove module");
                        CheckActiveUnchanged();
                        Check(File.Exists(cache) && HashFile(cache) == cacheHash, "Removing a downloaded profile altered its cached source ZIP.");
                        InvokeDownload(ModuleKind.BaballoniaDesktop);
                        CheckActiveUnchanged();
                        var restoredProfiles = new ModuleStore(profile).Installed;
                        Check(restoredProfiles.Count == afterSuccess.Count
                            && restoredProfiles.Any(release => release.Id == downloadedHeadset.Id)
                            && restoredProfiles.All(release => release.Id != downloadedDesktop.Id)
                            && HashFile(configuredPath) == configuredHash,
                            "Re-downloading a removed Babble profile duplicated its existing companion or reset companion settings.");
                        var state = File.ReadAllText(Path.Combine(profile, "modules", "module-state.json"));
                        var completedArchives = DownloadArchives(profile).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        handler.SetMode(DownloadResponse.Failure);
                        InvokeDownload(ModuleKind.SteamLink);
                        CheckActiveUnchanged();
                        Check(File.ReadAllText(Path.Combine(profile, "modules", "module-state.json")) == state,
                            "An HTTP download failure changed the module library.");
                        handler.SetMode(DownloadResponse.WaitForCancellation);
                        BeginButtonClick(DownloadButton(ModuleKind.VirtualDesktop));
                        PumpUntil(() => handler.RequestStarted.Task.IsCompleted && Field<bool>(window!, "_busy"), "running cancellable download began");
                        CheckActiveUnchanged();
                        BeginButtonClick(Named<Button>(window, "DownloadCancelButton"));
                        PumpUntil(() => !Field<bool>(window, "_busy"), "running download canceled");
                        Check(handler.ObservedCancellation, "Canceling an in-app download did not cancel HTTP work.");
                        CheckActiveUnchanged();
                        Check(File.ReadAllText(Path.Combine(profile, "modules", "module-state.json")) == state
                            && DownloadArchives(profile).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(completedArchives)
                            && !Directory.EnumerateFiles(profile, "*.partial", SearchOption.AllDirectories).Any(),
                            "A failed or canceled download altered the library or left extra download files.");
                    }
                    finally { CloseWindow(); }
                });
            }

            if (suppliedZip is not null)
            {
                Test("file drops stage bare Babble DLLs and manifest-free ZIPs with their known profiles and input ports", () =>
                {
                    var fixtures = Path.Combine(root, "module-file-format-fixtures");
                    Directory.CreateDirectory(fixtures);
                    var bareDll = Path.Combine(fixtures, "Baballonia standalone (2).dll");
                    var zipWithoutManifest = Path.Combine(fixtures, "Babble-without-manifest.zip");
                    using (var original = ZipFile.OpenRead(suppliedZip))
                    {
                        var module = original.Entries.Single(entry => Path.GetFileName(entry.FullName)
                            .Equals(ModuleStore.BaballoniaDll, StringComparison.OrdinalIgnoreCase));
                        using (var input = module.Open())
                        using (var output = File.Create(bareDll)) input.CopyTo(output);
                        using var target = ZipFile.Open(zipWithoutManifest, ZipArchiveMode.Create);
                        foreach (var entry in original.Entries)
                        {
                            if (Path.GetFileName(entry.FullName).Equals("module.json", StringComparison.OrdinalIgnoreCase)) continue;
                            var copy = target.CreateEntry(entry.FullName);
                            copy.ExternalAttributes = entry.ExternalAttributes;
                            if (entry.FullName.EndsWith('/')) continue;
                            using var input = entry.Open(); using var output = copy.Open(); input.CopyTo(output);
                        }
                    }
                    foreach (var path in new[] { bareDll, zipWithoutManifest })
                    {
                        var profile = Path.Combine(root, "module-format-" + Path.GetExtension(path).TrimStart('.'));
                        new AppSettings { AutoStart = false, LinkPort = 23456 }.Save(profile);
                        var originalHash = HashFile(path);
                        try
                        {
                            // Deliberately omit a worker: this checks the import/drop route
                            // without executing an external DLL or binding its default ports.
                            window = CreateWindow(profile, Path.Combine(root, "not-a-worker.exe"));
                            themes.AttachWindow(window); window.Show();
                            PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "file-format profile loaded");
                            var drop = ModuleDrop(path);
                            drop.RoutedEvent = DragDrop.DragOverEvent;
                            Method(window, "Window_DragOver").Invoke(window, [window, drop]);
                            Check(drop.Effects == DragDropEffects.Copy, "A supported module file was not accepted as a file drop.");
                            InvokeModuleDrop(path);
                            var store = new ModuleStore(profile);
                            var headset = store.Installed.Single(release => release.Kind == ModuleKind.BabbleHeadsetRuntime);
                            Check(headset.SupportsHeadset && store.Installed.Count == 2
                                && store.Installed.Any(release => release.Kind == ModuleKind.BaballoniaDesktop),
                                "A valid bare/manifest-free Babble import did not add the two known connection profiles.");
                            Check(Field<object?>(window, "_worker") is null && store.Current is null,
                                "The format-only check executed a module or activated a failed worker.");
                            Check(HashFile(path) == originalHash, "Importing a module changed its original DLL or ZIP.");
                            CloseWindow();
                            store.Activate(headset);
                            window = CreateWindow(profile, Path.Combine(root, "not-a-worker.exe"));
                            themes.AttachWindow(window); window.Show();
                            PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "staged file-format selection reopened");
                            Check(Named<TextBlock>(window, "ModuleLabel").Text.Contains("Babble Headset Runtime")
                                && Named<TextBlock>(window, "PackageDetail").Text.Contains("23456"),
                                "A bare/manifest-free known module lost its display name or automatic headset port.");
                            Named<RadioButton>(window, "ModulesNav").IsChecked = true; Pump();
                            var cards = string.Join("\n", Descendants(Named<ItemsControl>(window, "ModuleLibrary")).OfType<TextBlock>().Select(block => block.Text));
                            Check(cards.Contains("UDP 23456") && cards.Contains("UDP 8888"),
                                "A bare/manifest-free import did not retain the known per-profile input ports.");
                            CheckLibraryLayout();
                        }
                        finally { CloseWindow(); }
                    }
                });
            }

            if (customModuleDll is not null)
            {
                Test("an additional SDK DLL shows its actual name and module-managed input and preserves its source on removal", () =>
                {
                    var profile = Path.Combine(root, "custom-sdk-card-profile");
                    new AppSettings { AutoStart = false }.Save(profile);
                    var sourceHash = HashFile(customModuleDll);
                    var store = new ModuleStore(profile);
                    var custom = store.Prepare(customModuleDll);
                    Check(custom.Kind == ModuleKind.Custom, "The additional SDK fixture was mistaken for a built-in provider.");
                    Check(custom.ModuleName == "BabbleBridge.CustomModule.Fixture",
                        "A bare SDK module did not retain its actual assembly name for display.");
                    store.Activate(custom);
                    try
                    {
                        window = CreateWindow(profile);
                        themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "additional SDK card profile loaded");
                        Check(Field<object?>(window, "_worker") is null, "An AutoStart=false custom module executed on opening.");
                        Check(Named<TextBlock>(window, "ModuleLabel").Text.Contains(custom.ModuleName)
                            && Named<TextBlock>(window, "PackageDetail").Text.Contains("Module-managed input"),
                            "The additional SDK module lost its actual name or was assigned a built-in input transport.");
                        Named<RadioButton>(window, "ModulesNav").IsChecked = true; Pump();
                        var text = string.Join("\n", Descendants(Named<ItemsControl>(window, "ModuleLibrary")).OfType<TextBlock>().Select(block => block.Text));
                        Check(text.Contains(custom.ModuleName) && text.Contains("Module-managed input"),
                            "The custom library card does not show its actual name and module-managed input.");
                        CheckLibraryLayout(); SavePreview(window, renderDirectory, "custom-sdk-module.png");
                        InvokeCardButton(custom, "Remove module");
                        InvokeCardButton(custom, "Confirm remove module");
                        Check(new ModuleStore(profile).Installed.Count == 0 && new ModuleStore(profile).Current is null,
                            "The additional SDK module could not be removed through its library card.");
                        Check(File.Exists(customModuleDll) && HashFile(customModuleDll) == sourceHash,
                            "Removing an imported SDK module changed or removed its original DLL.");
                    }
                    finally { CloseWindow(); }
                });
            }

            if (published is not null)
            {
                Test("the published app contains no bundled tracking module archive, DLL or developer symbols", () =>
                {
                    Check(Directory.Exists(published), "Published app directory does not exist.");
                    var externalDlls = ModuleCatalog.All.Select(module => module.DllFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var payload = Directory.EnumerateFiles(published, "*", SearchOption.AllDirectories)
                        .Where(path => externalDlls.Contains(Path.GetFileName(path))
                            || Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                            || Path.GetExtension(path).Equals(".pdb", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    Check(payload.Length == 0, "Published payload includes a module: " + string.Join(", ", payload));
                });
            }
        }
        catch (Exception error)
        {
            failed++;
            Console.Error.WriteLine("FAIL appearance harness: " + error);
        }
        finally
        {
            CloseWindow();
            themes.Dispose();
            app.Shutdown();
            // This is a GUID directory created above, under a task-owned test workspace.
            var absolute = Path.GetFullPath(root);
            if (absolute.StartsWith(testParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(absolute).Length == 32)
                Directory.Delete(absolute, recursive: true);
        }
        Console.WriteLine($"Appearance: {passed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private static MainWindow CreateWindow(string profile, string? workerExecutable = null, ModuleDownloader? moduleDownloader = null)
    {
        var result = new MainWindow(profile, useDiscovery: false, workerExecutable: workerExecutable, moduleDownloader: moduleDownloader)
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000
        };
        return result;
    }

    private static void RenderParticlesAt(ParticleField particles, TimeSpan timestamp)
    {
        var frame = (RenderingEventArgs)Activator.CreateInstance(typeof(RenderingEventArgs),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [timestamp], null)!;
        Method(particles, "OnRendering").Invoke(particles, [null, frame]);
    }

    private static UdpClient ReservePort()
    {
        var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.ExclusiveAddressUse = true;
        socket.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return socket;
    }

    private static int PortOf(UdpClient socket) => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;

    private static string ModuleFixture(string source, string directory, string version, int inputPort, int linkPort)
    {
        var path = Path.Combine(directory, "module-" + version + ".zip");
        using var original = ZipFile.OpenRead(source);
        using var fixture = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in original.Entries)
        {
            var entry = fixture.CreateEntry(item.FullName);
            entry.ExternalAttributes = item.ExternalAttributes;
            if (item.FullName.EndsWith('/')) continue;
            using var input = item.Open();
            using var output = entry.Open();
            var name = Path.GetFileName(item.FullName);
            if (name is "module.json" or "BabbleConfig.json")
            {
                var json = JsonNode.Parse(input)!.AsObject();
                if (name == "module.json") json["Version"] = version;
                else
                {
                    json["Host"] = "127.0.0.1";
                    json["Port"] = inputPort;
                    json["LinkPort"] = linkPort;
                    json["IsEyeSupported"] = true;
                    json["IsFaceSupported"] = true;
                }
                using var writer = new StreamWriter(output, leaveOpen: true);
                writer.Write(json.ToJsonString());
            }
            else input.CopyTo(output);
        }
        return path;
    }

    private static DragEventArgs ModuleDrop(string path)
    {
        var data = new DataObject(DataFormats.FileDrop, new[] { path });
        var drop = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [data, DragDropKeyStates.None, DragDropEffects.Copy, window!, new Point()], null)!;
        drop.RoutedEvent = DragDrop.DropEvent;
        return drop;
    }

    private static void InvokeModuleDrop(string path)
    {
        var started = false;
        window!.Dispatcher.BeginInvoke(new Action(() =>
        {
            Method(window, "Window_Drop").Invoke(window, [window, ModuleDrop(path)]);
            started = true;
        }));
        PumpUntil(() => started && !Field<bool>(window, "_busy"), "module file drop completed", TimeSpan.FromSeconds(30));
    }

    private static void InvokeModuleImport(string path)
    {
        Task? operation = null;
        window!.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { operation = (Task)Method(window, "UpdateModule").Invoke(window, [path])!; }
            catch (Exception error) { operation = Task.FromException(error); }
        }));
        PumpUntil(() => operation?.IsCompleted == true, "module import transaction completed", TimeSpan.FromSeconds(90));
        operation!.GetAwaiter().GetResult();
        PumpUntil(() => !Field<bool>(window, "_busy"), "module import operation finished");
    }

    private static void CheckLibraryLayout() => CheckCardActionLayout(Named<ItemsControl>(window!, "ModuleLibrary"));
    private static void CheckDownloadLayout() => CheckCardActionLayout(Named<ItemsControl>(window!, "DownloadChoices"));

    private static void CheckCardActionLayout(ItemsControl library)
    {
        window!.UpdateLayout();
        var buttons = Descendants(library).OfType<Button>()
            .Where(candidate => candidate.Tag is not null && candidate.IsVisible).ToArray();
        foreach (var button in buttons)
        {
            var bounds = button.TransformToAncestor(library).TransformBounds(new Rect(button.RenderSize));
            foreach (var text in Descendants(library).OfType<TextBlock>()
                .Where(text => text.IsVisible && ReferenceEquals(text.DataContext, button.DataContext) && !HasButtonAncestor(text)))
            {
                var textBounds = text.TransformToAncestor(library).TransformBounds(new Rect(text.RenderSize));
                Check(!bounds.IntersectsWith(textBounds), "A module action overlaps its name, details or confirmation text.");
            }
            foreach (var other in buttons.Where(other => !ReferenceEquals(other, button)))
            {
                var otherBounds = other.TransformToAncestor(library).TransformBounds(new Rect(other.RenderSize));
                Check(!bounds.IntersectsWith(otherBounds), "Module action buttons overlap each other.");
            }
        }
    }

    private static bool HasButtonAncestor(DependencyObject element)
    {
        for (var ancestor = VisualTreeHelper.GetParent(element); ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
            if (ancestor is Button) return true;
        return false;
    }

    private static string HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private static void CheckLibraryReferences(ModuleStore store)
    {
        var ids = store.Installed.Select(release => release.Id).ToHashSet();
        Check(store.Current is null || ids.Contains(store.Current.Id), "The active selection refers to a removed module.");
        Check(store.Previous is null || ids.Contains(store.Previous.Id), "Rollback refers to a removed module.");
    }

    private static IEnumerable<Button> CardButtons(ModuleRelease release) =>
        Descendants(Named<ItemsControl>(window!, "ModuleLibrary")).OfType<Button>()
            .Where(candidate => candidate.Tag is ModuleRelease module && module.Id == release.Id);

    private static Button CardButton(ModuleRelease release, string action)
    {
        Named<RadioButton>(window!, "ModulesNav").IsChecked = true;
        Pump(); window!.UpdateLayout();
        return CardButtons(release).SingleOrDefault(candidate => candidate.IsVisible
            && (AutomationProperties.GetName(candidate) == action || candidate.Content is string content && content == action))
            ?? throw new InvalidOperationException("Missing module action: " + action + " for " + release.Kind);
    }

    private static void InvokeCardButton(ModuleRelease release, string action)
    {
        var button = CardButton(release, action);
        Check(button.IsEnabled, "Module action is disabled: " + action);
        var started = false;
        window!.Dispatcher.BeginInvoke(new Action(() =>
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            started = true;
        }));
        PumpUntil(() => started && !Field<bool>(window, "_busy"), "module action completed: " + action, TimeSpan.FromSeconds(90));
    }

    private static string[] DownloadArchives(string profile)
    {
        var downloads = Path.Combine(profile, "downloads");
        return Directory.Exists(downloads) ? Directory.GetFiles(downloads, "*.zip", SearchOption.AllDirectories) : [];
    }

    private static Button DownloadButton(ModuleKind kind)
    {
        Named<RadioButton>(window!, "ModulesNav").IsChecked = true;
        Pump(); window!.UpdateLayout();
        return Descendants(Named<ItemsControl>(window, "DownloadChoices")).OfType<Button>()
            .Single(button => button.Tag is ModuleKind candidate && candidate == kind);
    }

    private static void BeginButtonClick(Button button)
    {
        Check(button.IsEnabled, "The requested interface action is disabled: " + AutomationProperties.GetName(button));
        var started = false;
        window!.Dispatcher.BeginInvoke(new Action(() =>
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); started = true;
        }));
        PumpUntil(() => started, "interface action began");
    }

    private static void InvokeDownload(ModuleKind kind)
    {
        BeginButtonClick(DownloadButton(kind));
        PumpUntil(() => !Field<bool>(window!, "_busy"), "module download completed: " + kind, TimeSpan.FromSeconds(30));
    }

    private enum DownloadResponse { Success, Failure, WaitForCancellation, WaitDuringTransfer }

    private sealed class FixtureDownloadHandler(Dictionary<Uri, byte[]> packages) : HttpMessageHandler
    {
        private readonly List<Uri> requests = [];
        private DownloadResponse mode;
        public IReadOnlyList<Uri> Requests { get { lock (requests) return requests.ToArray(); } }
        public TaskCompletionSource<bool> RequestStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> TransferWaiting { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ObservedCancellation { get; private set; }
        public void SetMode(DownloadResponse response)
        {
            mode = response; ObservedCancellation = false;
            RequestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TransferWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("The download request has no URI.");
            lock (requests) requests.Add(uri);
            RequestStarted.TrySetResult(true);
            if (mode == DownloadResponse.WaitForCancellation)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { ObservedCancellation = true; throw; }
            }
            if (mode == DownloadResponse.Failure)
                return new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("Synthetic download failure"), RequestMessage = request };
            if (!packages.TryGetValue(uri, out var payload))
                throw new InvalidOperationException("The interface requested an unexpected module download URI.");
            HttpContent content = mode == DownloadResponse.WaitDuringTransfer
                ? new StreamContent(new PauseAfterChunkStream(payload, () => TransferWaiting.TrySetResult(true), () => ObservedCancellation = true))
                : new ByteArrayContent(payload);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
            response.Content.Headers.ContentLength = payload.Length;
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            return response;
        }
    }

    private sealed class PauseAfterChunkStream(byte[] payload, Action waiting, Action canceled) : Stream
    {
        private int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position == 0)
            {
                var count = Math.Min(buffer.Length, Math.Min(64, payload.Length - 1));
                payload.AsMemory(0, count).CopyTo(buffer);
                position = count;
                return ValueTask.FromResult(count);
            }
            waiting();
            return new ValueTask<int>(WaitUntilCanceled(cancellationToken));
        }
        private async Task<int> WaitUntilCanceled(CancellationToken token)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
            catch (OperationCanceledException)
            {
                canceled();
                // Keep cleanup pending for a display tick, so closing immediately
                // rather than awaiting the download cannot accidentally pass.
                await Task.Delay(100);
                throw;
            }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string? WorkerArgument(string name)
    {
        var arguments = Field<Process>(window!, "_worker").StartInfo.ArgumentList.ToArray();
        var index = Array.IndexOf(arguments, name);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }

    private static void InvokeSaveConnection()
    {
        var started = false;
        window!.Dispatcher.BeginInvoke(new Action(() =>
        {
            Method(window, "Save_Click").Invoke(window, [window, new RoutedEventArgs()]);
            started = true;
        }));
        PumpUntil(() => started && !Field<bool>(window, "_busy"), "connection save completed", TimeSpan.FromSeconds(90));
    }

    private static void InvokeModuleSelection(ModuleRelease release) => InvokeCardButton(release, "Use module");

    private static void InvokeRollback()
    {
        var started = false;
        window!.Dispatcher.BeginInvoke(new Action(() =>
        {
            Method(window, "Rollback_Click").Invoke(window, [window, new RoutedEventArgs()]);
            started = true;
        }));
        PumpUntil(() => started && !Field<bool>(window, "_busy"), "module rollback transaction completed", TimeSpan.FromSeconds(50));
    }

    private static Dictionary<string, Color> DisplayedColors(MainWindow target)
    {
        var cardStyle = (Style)Application.Current.FindResource("Card");
        var card = Descendants(target).OfType<Border>().First(border => border.Style == cardStyle);
        return new()
        {
            ["page"] = ColorOf(target.Background),
            ["page text"] = ColorOf(Named<TextBlock>(target, "PageTitle").Foreground),
            ["card"] = ColorOf(card.Background),
            ["card border"] = ColorOf(card.BorderBrush),
            ["input"] = ColorOf(Named<TextBox>(target, "HostBox").Background),
            ["input text"] = ColorOf(Named<TextBox>(target, "HostBox").Foreground),
            ["body text"] = ColorOf(Named<TextBlock>(target, "AvatarLabel").Foreground)
        };
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void SavePreview(MainWindow target, string? directory, string name)
    {
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        // Capture the final page layout rather than the first tick of its entrance animation.
        if (Field<FrameworkElement?>(target, "_currentPage") is { } page) Motion.Transition(page, false);
        target.UpdateLayout();
        var content = (FrameworkElement)target.Content;
        var image = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
            (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var surface = new DrawingVisual();
        using (var drawing = surface.RenderOpen())
        {
            var bounds = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
            drawing.DrawRectangle(target.Background, null, bounds);
            drawing.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None }, null, bounds);
        }
        image.Render(surface);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, name));
        png.Save(output);
    }

    private static T Named<T>(MainWindow target, string name) where T : FrameworkElement =>
        target.FindName(name) as T ?? throw new InvalidOperationException("Missing named control: " + name);
    private static T Field<T>(object target, string name) => (T)(target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)!);
    private static MethodInfo Method(object target, string name) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Missing method: " + name);
    private static Color ColorOf(Brush brush) => brush is SolidColorBrush solid ? solid.Color
        : throw new InvalidOperationException("Expected a solid theme brush.");
    private static double Brightness(Color color) => .2126 * color.R + .7152 * color.G + .0722 * color.B;
    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var scaled = value / 255.0;
            return scaled <= .04045 ? scaled / 12.92 : Math.Pow((scaled + .055) / 1.055, 2.4);
        }
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }
    private static double Contrast(Color first, Color second)
    {
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error.GetBaseException().Message); }
    }
    private static void Pump()
    {
        Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void PumpUntil(Func<bool> predicate, string description, TimeSpan? timeout = null)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            Pump();
            if (clock.Elapsed > (timeout ?? TimeSpan.FromSeconds(5))) throw new TimeoutException(description);
            System.Threading.Thread.Sleep(1);
        }
        Pump();
    }
    private static void PumpFor(TimeSpan duration)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration)
        {
            Pump();
            System.Threading.Thread.Sleep(5);
        }
    }
    private static void CloseWindow()
    {
        if (window is null) return;
        window.Close();
        PumpUntil(() => !window.IsVisible, "test window closed");
        window = null;
    }

    private static ResourceDictionary LoadAppResources()
    {
        // Read production styles rather than copying their implementation. A plain
        // Application deliberately avoids the real App's queued profile/mutex startup.
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var dictionary = new XElement(document.Root!.Element(presentation + "Application.Resources")!
            .Element(presentation + "ResourceDictionary")!);
        foreach (var declaration in document.Root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
        {
            var value = declaration.Value.StartsWith("clr-namespace:", StringComparison.Ordinal)
                && !declaration.Value.Contains(";assembly=", StringComparison.Ordinal)
                ? declaration.Value + ";assembly=BabbleFrameBridge" : declaration.Value;
            dictionary.SetAttributeValue(declaration.Name, value);
        }
        var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/BabbleFrameBridge;component/") };
        var result = (ResourceDictionary)XamlReader.Parse(dictionary.ToString(), context);
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
