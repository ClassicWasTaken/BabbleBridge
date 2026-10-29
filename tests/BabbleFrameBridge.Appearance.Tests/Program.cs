// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
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
                    File.WriteAllText(desktopConfig, "42");
                    Method(window, "RefreshModule").Invoke(window, []);
                    Check(Named<TextBlock>(window, "PackageDetail").Text.Contains("8888"),
                        "Scalar module settings crashed library refresh or failed to use the fallback input port.");
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

    private static MainWindow CreateWindow(string profile, string? workerExecutable = null)
    {
        var result = new MainWindow(profile, useDiscovery: false, workerExecutable: workerExecutable)
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

    private static void CheckLibraryLayout()
    {
        window!.UpdateLayout();
        foreach (var button in Descendants(Named<ItemsControl>(window, "ModuleLibrary")).OfType<Button>()
            .Where(candidate => candidate.Tag is ModuleRelease))
        {
            var grid = (Grid)VisualTreeHelper.GetParent(button);
            var details = grid.Children.OfType<StackPanel>().Single();
            var buttonBounds = button.TransformToAncestor(grid).TransformBounds(new Rect(button.RenderSize));
            var detailsBounds = details.TransformToAncestor(grid).TransformBounds(new Rect(details.RenderSize));
            Check(!buttonBounds.IntersectsWith(detailsBounds), "A module selection button overlaps its name or connection details.");
        }
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

    private static void InvokeModuleSelection(ModuleRelease release)
    {
        Named<RadioButton>(window!, "ModulesNav").IsChecked = true;
        Pump(); window!.UpdateLayout();
        var button = Descendants(Named<ItemsControl>(window, "ModuleLibrary")).OfType<Button>()
            .Single(candidate => candidate.Tag is ModuleRelease module && module.Id == release.Id);
        Check(button.IsEnabled, "An inactive installed module could not be selected.");
        var started = false;
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            started = true;
        }));
        PumpUntil(() => started && !Field<bool>(window, "_busy"), "module selection completed", TimeSpan.FromSeconds(90));
    }

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
