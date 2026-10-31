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
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Linq;
using BabbleFrameBridge.App;
using BabbleFrameBridge.Shared;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    private static int passed, failed;
    private static MainWindow? window;
    private static readonly ModuleKind[] DownloadOrder =
        [ModuleKind.BabbleHeadsetRuntime, ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink, ModuleKind.VirtualDesktop];
    private static readonly ModuleKind[] AdditionalDownloadOrder = [ModuleKind.SRanipal, ModuleKind.Beyond, ModuleKind.EyeTrackVR];

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
        var workerExecutable = Argument("--worker-exe") ?? (published is null ? null : Path.Combine(published, "runtime", "BabbleFrameBridge.Worker.exe"));
        var virtualDesktopZip = Argument("--virtual-desktop-zip");
        var steamLinkZip = Argument("--steam-link-zip");
        var baballoniaAlphaZip = Argument("--baballonia-alpha-zip");
        var baballoniaLegacyZip = Argument("--baballonia-legacy-zip");
        var customModuleDll = Argument("--custom-module-dll");
        var additionalModuleDirectory = Argument("--additional-module-dir");
        var renderDirectory = Argument("--render-dir");
        if (workerExecutable is not null && !File.Exists(workerExecutable))
        {
            Console.Error.WriteLine("The explicitly supplied worker executable does not exist: " + workerExecutable);
            return 2;
        }
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
                    .Count(button => button.Tag is ModuleKind) == 7,
                    "The overview Add a module action did not reveal all seven in-app download choices.");
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
                var expectedKinds = DownloadOrder.Concat(AdditionalDownloadOrder).ToArray();
                var labels = new[] { "Download Headset Runtime / BFR", "Download Baballonia", "Download Steam Link", "Download Virtual Desktop",
                    "Download SRanipalTrackingModule", "Download BeyondTrackingModule", "Download ETVR Tracking Module" };
                var sourceLinks = new[]
                {
                    "https://github.com/Project-Babble/Baballonia/releases/tag/vrcft-module-4.0.0",
                    "https://github.com/Project-Babble/Baballonia/releases/tag/vrcft-module-3.2.0",
                    "https://github.com/ykeara/LinkFT/releases/tag/1.0.6",
                    "https://download.vrdesktop.net/files/VirtualDesktop.VRCFaceTracking.zip",
                    "https://github.com/VRCFaceTracking/SRanipalTrackingModule/releases/tag/1.5",
                    "https://github.com/BigscreenVR/VRCFT-Beyond/releases/tag/v1.1.0",
                    "https://github.com/EyeTrackVR/ETVRTrackingModule/releases/tag/0.0.5"
                };
                Check(downloads.Select(button => (ModuleKind)button.Tag).SequenceEqual(expectedKinds),
                    "Download choices are missing or do not preserve the requested provider order.");
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
                CheckThemeTextContrast();
                SavePreview(window, renderDirectory, "light.png");
                themes.ApplyTheme(true);
                Pump();
                var dark = DisplayedColors(window);
                CheckThemeTextContrast();
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
                Check(window.Title == "Face Tracking Bridge", "The window still uses the previous product name.");
                Check(Named<TextBlock>(window, "PageTitle").Text == "Face Tracking Bridge",
                    "The overview heading does not use the new product name.");
                var wordmark = Named<TextBlock>(window, "BrandWordmark");
                Check(wordmark.Text == "Face Tracking Bridge", "The sidebar still uses the previous product name.");
                var previousWidth = window.Width;
                var previousTheme = themes.CurrentIsDark;
                try
                {
                    window.Width = window.MinWidth;
                    window.UpdateLayout();
                    var wordmarkBounds = wordmark.TransformToAncestor(window).TransformBounds(new Rect(wordmark.RenderSize));
                    var navigation = Named<RadioButton>(window, "OverviewNav");
                    var navigationBounds = navigation.TransformToAncestor(window).TransformBounds(new Rect(navigation.RenderSize));
                    Check(wordmarkBounds.Right <= navigationBounds.Right + .5 && wordmarkBounds.Bottom < navigationBounds.Top,
                        "The longer product name overlaps navigation or exceeds the sidebar.");
                    var firstLine = new FormattedText("Face Tracking", System.Globalization.CultureInfo.InvariantCulture,
                        wordmark.FlowDirection, new Typeface(wordmark.FontFamily, wordmark.FontStyle, wordmark.FontWeight, wordmark.FontStretch),
                        wordmark.FontSize, wordmark.Foreground, VisualTreeHelper.GetDpi(wordmark).PixelsPerDip);
                    Check(firstLine.WidthIncludingTrailingWhitespace <= wordmark.ActualWidth + .5,
                        "The spaced brand name cannot fit cleanly on two sidebar lines.");
                    var heading = Named<TextBlock>(window, "PageTitle");
                    var titleText = new FormattedText(heading.Text, System.Globalization.CultureInfo.InvariantCulture,
                        heading.FlowDirection, new Typeface(heading.FontFamily, heading.FontStyle, heading.FontWeight, heading.FontStretch),
                        heading.FontSize, heading.Foreground, VisualTreeHelper.GetDpi(heading).PixelsPerDip);
                    Check(titleText.WidthIncludingTrailingWhitespace <= heading.ActualWidth + .5,
                        "The new overview title does not fit at the minimum window width.");
                    foreach (var dark in new[] { false, true })
                    {
                        themes.ApplyTheme(dark); Pump();
                        SavePreview(window, renderDirectory, dark ? "minimum-width-dark.png" : "minimum-width-light.png");
                    }
                }
                finally
                {
                    window.Width = previousWidth;
                    themes.ApplyTheme(previousTheme);
                    Pump();
                }
                foreach (var name in new[] { "HeadsetSourceButton", "DesktopSourceButton", "TrackingInputPort", "MotionBox" })
                    Check(window.FindName(name) is null, "An obsolete control remains: " + name);
                var text = string.Join("\n", Descendants(window).OfType<TextBlock>().Select(block => block.Text));
                Check(!text.Contains("Babble Bridge", StringComparison.OrdinalIgnoreCase)
                    && !text.Contains("FaceTracking Bridge", StringComparison.Ordinal),
                    "The interface still displays the previous product name.");
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
                        PumpUntil(() => !EntranceActive(page) && page.Opacity >= .999,
                            "interrupted entrance completed");
                        // FillBehavior.Stop can retain a stopped clock. Remove that
                        // completed opacity clock so replay is observable publicly.
                        page.BeginAnimation(UIElement.OpacityProperty, null);
                        Check(!page.HasAnimatedProperties, "The completed page kept an opacity animation after detachment.");
                        Motion.Transition(page, true);
                        // Check clock lifecycle rather than sampling a brief rendered
                        // opacity: a busy dispatcher can finish before a pump returns,
                        // and a newly attached WPF clock has not necessarily ticked yet.
                        Check(page.HasAnimatedProperties,
                            "Returning to a completed page did not attach a new entrance clock.");
                        PumpUntil(() => !EntranceActive(page), "revisited entrance completed");
                        Check(page.Opacity >= .999 && Math.Abs(page.RenderTransform.Value.OffsetY) < .05,
                            "The revisited page did not settle at full opacity and its final position.");
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

            Test("a stopped bridge keeps its tracking particles dim and completely still", () =>
            {
                var particles = Named<ParticleField>(window, "HeroParticles");
                Check(!Field<bool>(window, "_running"), "The stopped-particle fixture unexpectedly started tracking.");
                Method(window, "UpdateBridgeParticles").Invoke(window, null);
                PumpUntil(() => Math.Abs(particles.Opacity - .24) < .001, "stopped particles dimmed to their resting opacity");
                PumpUntil(() => particles.MotionSpeed == 0 && !Field<bool>(particles, "attached"),
                    "stopped particles finished coasting");
                Check(!particles.AnimationsEnabled,
                    "A stopped bridge retained animated tracking particles.");
                var stopped = Field<double>(particles, "seconds");
                RenderParticlesAt(particles, TimeSpan.FromSeconds(100));
                RenderParticlesAt(particles, TimeSpan.FromSeconds(101));
                PumpFor(TimeSpan.FromMilliseconds(70));
                Check(Field<double>(particles, "seconds") == stopped,
                    "Stopped tracking particles advanced after manual display ticks or a dispatcher pump.");
            });

            Test("tracking particles follow display time, pause offscreen, and coast smoothly when tracking stops", () =>
            {
                var particles = Named<ParticleField>(window, "HeroParticles");
                var wasRunning = Field<bool>(window, "_running");
                try
                {
                    // Exercise rendering independently of a physical tracker;
                    // real worker startup/shutdown is checked by module fixtures below.
                    window.GetType().GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                    Method(window, "UpdateBridgeParticles").Invoke(window, null);
                    Named<RadioButton>(window, "OverviewNav").IsChecked = true;
                    Pump();
                    PumpUntil(() => particles.Opacity >= .999, "running tracking particles brightened");
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        PumpUntil(() => Field<bool>(particles, "attached"), "visible particle animation attached");
                        PumpUntil(() => particles.MotionSpeed >= .999, "running particles reached their normal travel speed");
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
                        PumpUntil(() => particles.MotionSpeed >= .999, "restored particle speed settled");
                    }
                    particles.AnimationsEnabled = false;
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        Check(particles.MotionSpeed > 0 && Field<bool>(particles, "attached"),
                            "Stopping tracking froze the particles before they could coast.");
                        var coasting = Field<double>(particles, "seconds");
                        var coastFrame = Field<TimeSpan?>(particles, "lastFrame")!.Value + TimeSpan.FromSeconds(1.0 / 144);
                        RenderParticlesAt(particles, coastFrame);
                        Check(particles.MotionSpeed is > 0 and < 1 && Field<double>(particles, "seconds") > coasting,
                            "The particle field did not decelerate while preserving forward motion.");
                    }
                    PumpUntil(() => particles.MotionSpeed == 0 && !Field<bool>(particles, "attached"),
                        "tracking particles coasted to rest", TimeSpan.FromSeconds(3));
                    var stopped = Field<double>(particles, "seconds");
                    PumpFor(TimeSpan.FromMilliseconds(60));
                    Check(Field<double>(particles, "seconds") == stopped, "Resting particles continued advancing.");
                    particles.AnimationsEnabled = true;
                    Check(Field<double>(particles, "seconds") == stopped,
                        "Restarting particle motion reset the displayed simulation frame.");
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        Check(particles.MotionSpeed < 1, "Restarting tracking jumped directly to full particle speed.");
                        PumpUntil(() => particles.MotionSpeed >= .999 && Field<double>(particles, "seconds") > stopped,
                            "particle motion accelerated smoothly from its resting frame");
                    }
                }
                finally
                {
                    window.GetType().GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, wasRunning);
                    Method(window, "RefreshButtons").Invoke(window, null);
                    PumpUntil(() => Math.Abs(particles.Opacity - (wasRunning ? 1 : .24)) < .001,
                        "particle fixture restored the bridge's real state");
                }
            });

            Test("module JSON log values and malformed numeric status do not crash or restart the interface", () =>
            {
                var worker = Field<object?>(window, "_worker");
                var restarts = Field<int>(window, "_restartCount");
                foreach (var line in new[] { "42", "[]", "true", "false", "null", "\"SDK log text\"" })
                {
                    Method(window, "HandleWorkerLine").Invoke(window, [line]);
                    Check(Named<TextBox>(window, "LogBox").Text.Contains(line), "A non-object module log value was not retained as text.");
                }
                Method(window, "HandleWorkerLine").Invoke(window, ["{\"type\":\"status\",\"rate\":90,\"trackingRate\":144,\"parameters\":20}"]);
                Check(Named<TextBlock>(window, "OutputRate").Text.Contains("90")
                    && Named<TextBlock>(window, "SenderCadence").Text.Contains("144"), "The valid numeric status control was not displayed.");
                foreach (var status in new[]
                {
                    "{\"type\":\"status\",\"rate\":\"fast\",\"trackingRate\":true,\"parameters\":[],\"outputPort\":{}}",
                    "{\"type\":\"status\",\"rate\":null,\"trackingRate\":[],\"parameters\":false,\"outputPort\":\"9000\"}",
                    "{\"type\":\"status\",\"rate\":1e999,\"trackingRate\":1e999}"
                })
                {
                    Method(window, "HandleWorkerLine").Invoke(window, [status]);
                    Check(Named<TextBlock>(window, "OutputRate").Text.StartsWith("0", StringComparison.Ordinal)
                        && Named<TextBlock>(window, "SenderCadence").Text.Contains("0 Hz"),
                        "An invalid numeric status produced a bad displayed rate.");
                }
                Check(ReferenceEquals(Field<object?>(window, "_worker"), worker)
                    && Field<int>(window, "_restartCount") == restarts && !Field<bool>(window, "_pendingRestart")
                    && !Field<bool>(window, "_running"),
                    "A module log or malformed status started or restarted a worker.");
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

            Test("the kitty is session-only, survives connection saves, and starts off on every launch", () =>
            {
                CloseWindow();
                var profile = Path.Combine(root, "kitty-preferences");
                new AppSettings { AutoStart = false }.Save(profile);
                try
                {
                    window = CreateWindow(profile);
                    themes.AttachWindow(window); window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "kitty profile opened");
                    var overlay = Named<HologramKittyOverlay>(window, "KittyOverlay");
                    var launcher = Named<Button>(window, "KittyToggle");
                    var surface = Named<Grid>(window, "RootSurface");
                    var location = launcher.TranslatePoint(default, surface);
                    Check(!AppSettings.Load(profile).KittyEnabled && !overlay.IsActive && !overlay.IsAnimating,
                        "A fresh profile started the optional companion.");
                    Check(location.X < 216 && location.Y > surface.ActualHeight * .55,
                        "The kitty launcher is not in the lower-left sidebar.");
                    Check(Named<Image>(window, "KittyFace").Source is DrawingImage,
                        "The kitty launcher is missing its original vector face.");
                    var settingsBeforeActivation = File.ReadAllText(Path.Combine(profile, "settings.json"));
                    launcher.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(() => overlay.IsActive && overlay.IsVisible
                        && overlay.IsAnimating == SystemParameters.ClientAreaAnimation, "kitty appeared and attached its visible rendering policy");
                    Check(overlay.IsActive && Named<System.Windows.Shapes.Ellipse>(window, "KittyActiveDot").Visibility == Visibility.Visible,
                        "Enabling the kitty did not show its active indicator.");
                    Check(File.ReadAllText(Path.Combine(profile, "settings.json")) == settingsBeforeActivation,
                        "Activating a session-only companion rewrote the saved connection settings.");
                    Check(overlay.IsAnimating == SystemParameters.ClientAreaAnimation,
                        "Kitty rendering does not honor the current Windows motion preference.");
                    InvokeSaveConnection();
                    Check(overlay.IsActive, "Saving a connection disabled the current session's kitty.");
                    Check(!AppSettings.Load(profile).KittyEnabled,
                        "A connection save persisted the current session's enabled companion.");
                    Method(window, "DismissNotice").Invoke(window, null);
                    foreach (var dark in new[] { false, true })
                    {
                        themes.ApplyTheme(dark); Pump();
                        Check(Contrast(ColorOf(launcher.Foreground), ColorOf(launcher.Background)) >= 4.5,
                            "The active kitty launcher text is unreadable after a theme change.");
                        SavePreview(window, renderDirectory, dark ? "kitty-dark.png" : "kitty-light.png");
                    }
                    CloseWindow();
                    Check(!overlay.IsActive && !overlay.IsAnimating, "Closing the window retained the kitty's rendering subscription.");
                    window = CreateWindow(profile); themes.AttachWindow(window); window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "saved kitty profile reopened");
                    overlay = Named<HologramKittyOverlay>(window, "KittyOverlay");
                    Check(!overlay.IsActive && !overlay.IsAnimating
                        && Named<System.Windows.Shapes.Ellipse>(window, "KittyActiveDot").Visibility == Visibility.Collapsed,
                        "Reopening automatically enabled the kitty instead of starting off.");
                    EnableKitty(window);
                    Named<Button>(window, "KittyToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!overlay.IsActive && !overlay.IsAnimating,
                        "The same launcher could not tuck the companion away.");
                }
                finally { CloseWindow(); }
            });

            Test("the text-only kitty ignores legacy sound preferences without losing saved settings", () =>
            {
                var profile = Path.Combine(root, "kitty-text-migration");
                Directory.CreateDirectory(profile);
                // Existing previews may have persisted both activation and audio.
                // Neither legacy opt-in may activate a kitty on the next launch.
                File.WriteAllText(Path.Combine(profile, "settings.json"),
                    "{\"AutoStart\":false,\"KittyEnabled\":true,\"KittySoundsEnabled\":true,\"OutputPort\":19180,\"ReceivePort\":19181}");
                try
                {
                    window = CreateWindow(profile); window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "legacy audio profile loaded silently");
                    var overlay = Named<HologramKittyOverlay>(window, "KittyOverlay");
                    Check(!overlay.IsActive && !overlay.IsAnimating,
                        "A saved legacy kitty flag enabled the companion on startup.");
                    Check(window.FindName("KittySoundsBox") is null,
                        "The removed sound preference is still shown in Settings.");
                    Check(typeof(MainWindow).Assembly.GetType("BabbleFrameBridge.App.KittyVoice") is null
                        && typeof(MainWindow).Assembly.GetType("BabbleFrameBridge.App.KittyVoiceSynthesizer") is null,
                        "The text-only build still contains a kitty audio runtime.");
                    EnableKitty(window);
                    InvokeSaveConnection();
                    var saved = AppSettings.Load(profile);
                    Check(overlay.IsActive && saved.OutputPort == 19180 && saved.ReceivePort == 19181,
                        "Saving after the silent migration reset the companion or connection.");
                    CloseWindow();
                    window = CreateWindow(profile); window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "silent kitty profile reopened");
                    Check(!Named<HologramKittyOverlay>(window, "KittyOverlay").IsActive
                        && Named<TextBox>(window, "PortBox").Text == "19180"
                        && window.FindName("KittySoundsBox") is null,
                        "Reopening lost settings, enabled the kitty, or restored the removed audio preference.");
                }
                finally { CloseWindow(); }
            });

            Test("kitty text vocalizations fit the viewport, stay click-through and clear when hidden", () =>
            {
                var profile = Path.Combine(root, "kitty-caption-placement");
                new AppSettings { AutoStart = false, KittyEnabled = true }.Save(profile);
                try
                {
                    window = CreateWindow(profile); themes.AttachWindow(window); window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "caption window loaded");
                    EnableKitty(window);
                    var overlay = Named<HologramKittyOverlay>(window, "KittyOverlay");
                    var position = Field<Point>(overlay, "_position");
                    var points = new[] { new Point(0, 0), new Point(Math.Max(0, overlay.ActualWidth - 144), 0),
                        new Point(Math.Max(0, overlay.ActualWidth - 144), Math.Max(0, overlay.ActualHeight - 144)), position };
                    foreach (var point in points)
                    {
                        Method(overlay, "ClearCaption").Invoke(overlay, null);
                        overlay.GetType().GetField("_position", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(overlay, point);
                        Method(overlay, "PlaceCat").Invoke(overlay, null);
                        Method(overlay, "ShowBubble").Invoke(overlay, ["mrrp~", 1.8]);
                        PumpUntil(() => overlay.CaptionVisible && !overlay.CaptionBounds.IsEmpty,
                            "caption established its viewport bounds");
                        var bounds = overlay.CaptionBounds;
                        Check(overlay.CaptionText == "mrrp~" && bounds.Width > 8 && bounds.Height > 8,
                            "A text vocalization was missing or had empty visual bounds.");
                        Check(bounds.Left >= -.5 && bounds.Top >= -.5 && bounds.Right <= overlay.ActualWidth + .5
                            && bounds.Bottom <= overlay.ActualHeight + .5,
                            "A vocalization was clipped beyond the application viewport.");
                        var corner = new Point(bounds.Left + 3, bounds.Top + 3);
                        // The caption itself never accepts input. If its corner
                        // overlaps the animated cat, another clear corner must pass through.
                        if (overlay.InputHitTest(corner) is not null)
                            corner = new Point(bounds.Right - 3, bounds.Top + 3);
                        Check(overlay.InputHitTest(corner) is null,
                            "The text bubble intercepted application input outside the cat.");
                    }
                    Method(window, "DismissNotice").Invoke(window, null);
                    PumpUntil(() => overlay.CaptionVisible && Field<KittyCaption>(overlay, "_caption").Opacity >= .99,
                        "caption finished its gentle entrance before preview capture");
                    foreach (var dark in new[] { false, true })
                    {
                        themes.ApplyTheme(dark); Pump();
                        SavePreview(window, renderDirectory, dark ? "kitty-caption-dark.png" : "kitty-caption-light.png");
                    }
                    window.Hide();
                    PumpUntil(() => !overlay.CaptionVisible && overlay.CaptionBounds.IsEmpty,
                        "hiding cleared the text vocalization");
                    window.Show();
                    PumpUntil(() => overlay.IsAnimating == SystemParameters.ClientAreaAnimation,
                        "caption window resumed its motion policy");
                    Check(!overlay.CaptionVisible, "Reopening replayed an old caption.");
                    Method(overlay, "ShowBubble").Invoke(overlay, ["zzz", 1.5]);
                    Check(overlay.CaptionVisible, "A visible companion could not show another text reaction.");
                    Named<Button>(window, "KittyToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!overlay.CaptionVisible && overlay.CaptionBounds.IsEmpty,
                        "Disabling the companion left a speech bubble visible.");
                }
                finally { CloseWindow(); }
            });

            Test("kitty hit testing covers its body while empty space and the launcher remain usable", () =>
            {
                var profile = Path.Combine(root, "kitty-hit-testing");
                new AppSettings { AutoStart = false, KittyEnabled = true }.Save(profile);
                try
                {
                    window = CreateWindow(profile); window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "kitty hit-testing window loaded");
                    EnableKitty(window);
                    var overlay = Named<HologramKittyOverlay>(window, "KittyOverlay");
                    var surface = Named<Grid>(window, "RootSurface");
                    var bounds = overlay.CatBounds;
                    var body = new Point(bounds.X + bounds.Width * .5, bounds.Y + bounds.Height * .5);
                    Check(ReferenceEquals(overlay.InputHitTest(body), overlay), "The visible kitty cannot receive interaction.");
                    Check(overlay.InputHitTest(new Point(overlay.ActualWidth - 8, 22)) is null,
                        "The transparent overlay intercepts controls outside the kitty.");
                    var launcher = Named<Button>(window, "KittyToggle");
                    var center = launcher.TranslatePoint(new Point(launcher.ActualWidth * .5, launcher.ActualHeight * .5), surface);
                    Check(overlay.InputHitTest(center) is null, "The kitty blocks its own hide button.");
                    var hit = surface.InputHitTest(center);
                    Check(ReferenceEquals(hit, launcher) || hit is Visual visual && launcher.IsAncestorOf(visual),
                        "The full-window layer intercepts the bottom-left launcher.");
                    Check(Method(window, "FindKittyTarget").Invoke(window, [center]) is null,
                        "The companion can swat or animate its own launcher.");
                    Check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= overlay.ActualWidth && bounds.Bottom <= overlay.ActualHeight,
                        "The companion was placed outside the application client area.");
                }
                finally { CloseWindow(); }
            });

            Test("kitty clicks stay playful while pickup follows the held point without clicking real controls", () =>
            {
                var profile = Path.Combine(root, "kitty-pickup");
                new AppSettings { AutoStart = false }.Save(profile);
                try
                {
                    window = CreateWindow(profile); window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "pickup window loaded");
                    var overlay = Named<HologramKittyOverlay>(window, "KittyOverlay");
                    Check(!overlay.BeginPickup(new Point(300, 300)), "A disabled kitty accepted a pickup.");
                    EnableKitty(window);
                    var capturedBefore = Mouse.Captured;
                    var clicks = 0;
                    var button = Named<Button>(window, "ImportButton");
                    button.Click += (_, _) => clicks++;
                    Check(!overlay.BeginPickup(new Point(-20, -20)), "Empty overlay space accepted a kitty pickup.");
                    var body = KittyBodyPoint(overlay);
                    var before = overlay.FeetPosition;
                    var stimulation = Field<KittyMind>(overlay, "_mind").Stimulation;
                    Check(overlay.BeginPickup(body), "Pressing the kitty did not begin a gesture.");
                    overlay.MovePickup(body + new Vector(2, 1));
                    Check(!overlay.IsDragging && (overlay.FeetPosition - before).Length < .01,
                        "A small ordinary click moved the companion.");
                    overlay.EndPickup(body + new Vector(2, 1));
                    Check(!overlay.IsDragging && Field<KittyMind>(overlay, "_mind").Stimulation > stimulation,
                        "Releasing an ordinary click did not pester the kitty.");

                    body = KittyBodyPoint(overlay);
                    before = overlay.FeetPosition;
                    stimulation = Field<KittyMind>(overlay, "_mind").Stimulation;
                    Check(overlay.BeginPickup(body), "The next kitty press was ignored.");
                    overlay.MovePickup(body + new Vector(90, -50));
                    Check(overlay.IsDragging && (overlay.FeetPosition - before - new Vector(90, -50)).Length < .01,
                        "Picking up the kitty lost the exact point held under the pointer.");
                    Check(Field<KittyMind>(overlay, "_mind").Stimulation == stimulation,
                        "A pickup was counted as an ordinary pester click.");
                    var overButton = button.TranslatePoint(new Point(button.ActualWidth * .5, button.ActualHeight * .5), overlay);
                    overlay.MovePickup(overButton);
                    overlay.EndPickup(overButton);
                    Check(!overlay.IsDragging && clicks == 0,
                        "Dropping the companion over a real button clicked that control.");
                    Check(ReferenceEquals(Mouse.Captured, capturedBefore),
                        "The logical gesture API changed real mouse capture.");

                    body = KittyBodyPoint(overlay);
                    Check(overlay.BeginPickup(body), "The dropped kitty could not be picked up again.");
                    overlay.MovePickup(new Point(-5000, overlay.ActualHeight + 5000));
                    var clamped = overlay.CatBounds;
                    Check(clamped.Left >= 0 && clamped.Top >= 0 && clamped.Right <= overlay.ActualWidth
                        && clamped.Bottom <= overlay.ActualHeight,
                        "Dragging outside the window stranded the companion outside its viewport.");
                    var launcher = Named<Button>(window, "KittyToggle");
                    var launcherCenter = launcher.TranslatePoint(new Point(launcher.ActualWidth * .5, launcher.ActualHeight * .5), overlay);
                    Check(overlay.InputHitTest(launcherCenter) is null,
                        "Dragging to the lower left blocked the kitty's own launcher.");
                    overlay.CancelPickup();
                }
                finally { CloseWindow(); }
            });

            Test("kitty perches on real panel tops and follows scrolling, resizing and page changes", () =>
            {
                var profile = Path.Combine(root, "kitty-panel-perches");
                new AppSettings { AutoStart = false }.Save(profile);
                try
                {
                    window = CreateWindow(profile); themes.AttachWindow(window); window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "panel perch window loaded");
                    EnableKitty(window);
                    PumpUntil(() => Named<StackPanel>(window, "OverviewPage").Opacity >= .999,
                        "overview panel positions settled before selecting a ledge");
                    var overlay = Named<HologramKittyOverlay>(window, "KittyOverlay");
                    var panel = KittyPerchSurface(overlay);
                    var panelPoint = new Point(panel.Left + panel.Width * .5, panel.Top + 8);
                    Check(Method(window, "FindKittyTarget").Invoke(window, [panelPoint]) is FrameworkElement,
                        "The supplied kitty ledge does not belong to a real visible panel.");
                    Check(!overlay.TryClimbTo("a-panel-that-does-not-exist"), "The kitty accepted a hidden or nonexistent climb target.");
                    Check(overlay.TryClimbTo(panel.Id), "The kitty could not start climbing a real higher panel.");
                    if (SystemParameters.ClientAreaAnimation)
                        Check(overlay.IsClimbing && !overlay.IsPerched, "Climbing teleported directly onto the edge without a climb pose.");
                    PumpUntil(() => overlay.IsPerched && overlay.CurrentPerchSurfaceId == panel.Id,
                        "kitty completed its climb onto the real panel", TimeSpan.FromSeconds(5));
                    DropKittyOn(overlay, panel);
                    Check(overlay.IsPerched && !overlay.IsDragging && overlay.CurrentPerchSurfaceId == panel.Id,
                        "Releasing the kitty near a panel did not seat it on that panel.");
                    Check(Math.Abs(overlay.FeetPosition.Y - panel.Top) < .05,
                        "The perched kitty's feet do not rest on the actual panel top edge.");
                    SavePreview(window, renderDirectory, "kitty-panel-perch.png");

                    window.Width = window.MinWidth; window.Height = window.MinHeight;
                    window.UpdateLayout();
                    PumpUntil(() => overlay.IsPerched && overlay.GetSurfaces!().Any(surface => surface.Id == panel.Id
                        && Math.Abs(surface.Top - overlay.FeetPosition.Y) < .05), "perch followed the resized panel");
                    Named<RadioButton>(window, "ModulesNav").IsChecked = true;
                    PumpUntil(() => Named<StackPanel>(window, "ModulesPage").Visibility == Visibility.Visible
                        && !overlay.IsPerched, "navigation released a perch on the hidden overview page");
                    PumpUntil(() => Named<StackPanel>(window, "ModulesPage").Opacity >= .999,
                        "module page settled before selecting a ledge");
                    panel = KittyPerchSurface(overlay);
                    DropKittyOn(overlay, panel);
                    var scroll = Descendants(window).OfType<ScrollViewer>()
                        .First(viewer => viewer.IsAncestorOf(Named<StackPanel>(window, "ModulesPage")));
                    Check(scroll.ScrollableHeight > 0, "The module-page fixture did not exercise scrolling.");
                    var beforeScroll = overlay.FeetPosition;
                    scroll.ScrollToVerticalOffset(Math.Min(28, scroll.ScrollableHeight));
                    PumpUntil(() => scroll.VerticalOffset > 0, "module panels scrolled");
                    PumpUntil(() => !overlay.IsPerched || overlay.GetSurfaces!().Any(surface => surface.Id == panel.Id
                        && Math.Abs(surface.Top - overlay.FeetPosition.Y) < .05), "kitty reconciled the scrolled panel edge");
                    if (overlay.IsPerched)
                        Check(overlay.FeetPosition.Y < beforeScroll.Y,
                            "The visible panel moved but its perched kitty remained at the previous screen position.");
                    Check(overlay.GetSurfaces!().All(surface => surface.Top >= 0 && surface.Top < overlay.ActualHeight),
                        "Scrolling exposed a hidden or offscreen panel as a climb target.");
                }
                finally { CloseWindow(); }
            });

            Test("kitty pickup cancels on Escape, capture loss, hide, minimize, disable and disposal", () =>
            {
                var profile = Path.Combine(root, "kitty-pickup-cancel");
                new AppSettings { AutoStart = false }.Save(profile);
                try
                {
                    window = CreateWindow(profile); window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "pickup cancellation window loaded");
                    EnableKitty(window);
                    var overlay = Named<HologramKittyOverlay>(window, "KittyOverlay");
                    BeginKittyDrag(overlay); overlay.CancelPickup(); overlay.CancelPickup();
                    Check(!overlay.IsDragging, "CancelPickup did not safely clear a carried companion.");
                    BeginKittyDrag(overlay);
                    overlay.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                        { RoutedEvent = Mouse.LostMouseCaptureEvent });
                    Check(!overlay.IsDragging, "Losing mouse capture left the companion carried.");
                    BeginKittyDrag(overlay);
                    window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
                        Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    Check(!overlay.IsDragging, "Escape did not release a picked-up companion.");
                    BeginKittyDrag(overlay); window.Hide();
                    PumpUntil(() => !overlay.IsDragging && !overlay.IsAnimating, "hiding released the carried companion");
                    window.Show();
                    PumpUntil(() => overlay.IsAnimating == SystemParameters.ClientAreaAnimation, "pickup window resumed");
                    BeginKittyDrag(overlay); window.WindowState = WindowState.Minimized;
                    PumpUntil(() => !overlay.IsDragging && !overlay.IsAnimating, "minimizing released the carried companion");
                    window.WindowState = WindowState.Normal;
                    PumpUntil(() => overlay.IsAnimating == SystemParameters.ClientAreaAnimation, "pickup window restored");
                    BeginKittyDrag(overlay);
                    Named<Button>(window, "KittyToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!overlay.IsDragging && !overlay.IsActive, "Turning the companion off left a live pickup gesture.");
                    EnableKitty(window); BeginKittyDrag(overlay); overlay.Dispose();
                    Check(!overlay.IsDragging && !overlay.IsAnimating && !overlay.BeginPickup(KittyBodyPoint(overlay)),
                        "A disposed companion retained its pickup or accepted new input.");
                }
                finally { CloseWindow(); }
            });

            Test("hiding, minimizing or disabling the kitty stops its clock and resets visual swats", () =>
            {
                var profile = Path.Combine(root, "kitty-suspension");
                new AppSettings { AutoStart = false, KittyEnabled = true }.Save(profile);
                try
                {
                    window = CreateWindow(profile); window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "kitty suspension window loaded");
                    EnableKitty(window);
                    var overlay = Named<HologramKittyOverlay>(window, "KittyOverlay");
                    var cat = Field<HologramKittyVisual>(overlay, "_cat");
                    PumpUntil(() => overlay.IsAnimating == SystemParameters.ClientAreaAnimation, "kitty suspension rendering policy established");
                    Check(cat.BodyPartCount >= 16, "The cat has no independently articulated visual parts.");
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        cat.UpdateRig(1d / 60, 350, 55);
                        cat.UpdateRig(1d / 60, 350, 55);
                        Check(cat.RigSnapshot.MotionEnergy > 0, "Movement did not excite the cat's spring rig.");
                    }
                    var button = Named<Button>(window, "ImportButton");
                    Motion.PetWobble(button);
                    window.Hide();
                    PumpUntil(() => !overlay.IsAnimating, "hidden kitty detached rendering");
                    var seconds = Field<double>(overlay, "_seconds");
                    PumpFor(TimeSpan.FromMilliseconds(100));
                    Check(Field<double>(overlay, "_seconds") == seconds && !Field<bool>(overlay, "_inputAttached"),
                        "A hidden companion kept advancing or listening to pointer motion.");
                    Check(TransformParts(button.RenderTransform).All(part => !part.HasAnimatedProperties),
                        "Hiding the companion left a panel/button swat animation running.");
                    Check(cat.RigSnapshot.MotionEnergy == 0, "Hiding the cat retained spring momentum.");
                    window.Show();
                    PumpUntil(() => overlay.IsAnimating == SystemParameters.ClientAreaAnimation, "visible kitty followed motion preference");
                    window.WindowState = WindowState.Minimized;
                    PumpUntil(() => !overlay.IsAnimating, "minimized kitty detached rendering");
                    window.WindowState = WindowState.Normal;
                    PumpUntil(() => overlay.IsAnimating == SystemParameters.ClientAreaAnimation, "restored kitty resumed rendering");
                    Motion.PetWobble(button);
                    Named<Button>(window, "KittyToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!overlay.IsAnimating && !Field<bool>(overlay, "_inputAttached") && overlay.Visibility == Visibility.Collapsed,
                        "Disabling the companion retained rendering or input subscriptions.");
                    Check(TransformParts(button.RenderTransform).All(part => !part.HasAnimatedProperties),
                        "Tucking the kitty away retained a swat transform.");
                }
                finally { CloseWindow(); }
            });

            Test("kitty panel wobbles preserve layout and button behavior, settle, and respect reduced motion", () =>
            {
                var profile = Path.Combine(root, "kitty-wobble");
                new AppSettings { AutoStart = false }.Save(profile);
                var previousMotion = Motion.Enabled;
                try
                {
                    window = CreateWindow(profile); window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "wobble fixture loaded");
                    var button = Named<Button>(window, "ImportButton");
                    var size = new Size(button.ActualWidth, button.ActualHeight);
                    var margin = button.Margin; var enabled = button.IsEnabled; var clicks = 0;
                    button.Click += (_, _) => clicks++;
                    Motion.Enabled = true;
                    Motion.PetWobble(button, 1);
                    if (SystemParameters.ClientAreaAnimation)
                    {
                        Check(TransformParts(button.RenderTransform).Any(part => part.HasAnimatedProperties),
                            "A kitty swat did not attach any render animation.");
                        // Allow the spring's clock to begin, then wait for its final
                        // matrix rather than asserting a particular display tick.
                        PumpFor(TimeSpan.FromMilliseconds(760));
                        PumpUntil(() => NearlyIdentity(button.RenderTransform.Value), "kitty spring settled at its resting transform");
                    }
                    Check(button.ActualWidth == size.Width && button.ActualHeight == size.Height
                        && button.Margin == margin && button.IsEnabled == enabled && clicks == 0,
                        "A visual kitty swat changed layout, disabled a button, or clicked it.");
                    Motion.PetWobble(button);
                    Motion.ResetPetWobbles();
                    Check(NearlyIdentity(button.RenderTransform.Value) && TransformParts(button.RenderTransform).All(part => !part.HasAnimatedProperties),
                        "Reset left a displaced or animated button.");
                    Motion.Enabled = false;
                    Motion.PetWobble(button);
                    Check(NearlyIdentity(button.RenderTransform.Value) && TransformParts(button.RenderTransform).All(part => !part.HasAnimatedProperties),
                        "Reduced motion allowed a new kitty wobble.");
                }
                finally { Motion.ResetPetWobbles(); Motion.Enabled = previousMotion; CloseWindow(); }
            });

            // Restore the original profile expected by the connection/startup checks.
            window = CreateWindow(emptyProfile); themes.AttachWindow(window); window.Show();
            PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "profile reopened after companion checks");

            Test("startup preferences have their own Settings page and clear system tray wording", () =>
            {
                Named<RadioButton>(window, "PreferencesNav").IsChecked = true;
                PumpUntil(() => Named<StackPanel>(window, "PreferencesPage").Visibility == Visibility.Visible,
                    "dedicated Settings page opened");
                Check(Named<TextBlock>(window, "PageTitle").Text == "Settings",
                    "The startup preferences page did not have the Settings heading.");
                var preferences = Named<StackPanel>(window, "PreferencesPage");
                var connection = Named<StackPanel>(window, "SettingsPage");
                Check(Descendants(preferences).Contains(Named<Border>(window, "StartupWindowCard"))
                    && Descendants(preferences).Contains(Named<CheckBox>(window, "AutoStartBox"))
                    && !Descendants(connection).Contains(Named<Border>(window, "StartupWindowCard"))
                    && !Descendants(connection).Contains(Named<CheckBox>(window, "AutoStartBox")),
                    "Startup preferences remained in Connection instead of Settings.");
                Check((string)Named<CheckBox>(window, "StartMinimizedBox").Content == "Start minimized to system tray"
                    && AutomationProperties.GetName(Named<CheckBox>(window, "StartMinimizedBox")) == "Start minimized to system tray",
                    "The minimized startup preference did not explicitly identify the system tray.");
                Check(Descendants(preferences).OfType<TextBlock>().Any(text => text.Text.Contains("X keeps tracking running")
                    && text.Text.Contains("Open and Quit")), "Settings did not explain close-to-tray and how to quit.");
                Named<RadioButton>(window, "SettingsNav").IsChecked = true;
                PumpUntil(() => connection.Visibility == Visibility.Visible, "Connection page restored");
                Check(Named<TextBlock>(window, "PageTitle").Text == "Connection"
                    && Descendants(connection).Contains(Named<Button>(window, "SaveButton"))
                    && preferences.Visibility == Visibility.Collapsed, "Connection no longer shows its own controls independently.");
            });

            CloseWindow();
            Test("startup preferences register SteamVR once and survive connection saves and relaunch", () =>
            {
                var profile = Path.Combine(root, "startup-preferences");
                new AppSettings { AutoStart = false, OutputPort = 19100, ReceivePort = 19101 }.Save(profile);
                var registrations = new List<bool>();
                Task Configure(bool enabled) { registrations.Add(enabled); return Task.CompletedTask; }
                try
                {
                    window = CreateWindow(profile, configureSteamVrAutoLaunch: Configure);
                    themes.AttachWindow(window);
                    window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "startup preferences loaded");
                    var changes = 0;
                    window.StartupPreferencesChanged += (_, _) => changes++;
                    Check(!window.StartWithSteamVr && !window.CloseWithSteamVr && !window.StartMinimized
                        && registrations.Count == 0, "A fresh profile enabled a startup preference or touched SteamVR.");
                    Named<CheckBox>(window, "StartWithSteamVrBox").IsChecked = true;
                    PumpUntil(() => !Field<bool>(window, "_busy"), "SteamVR automatic startup enabled");
                    Named<CheckBox>(window, "CloseWithSteamVrBox").IsChecked = true;
                    Named<CheckBox>(window, "StartMinimizedBox").IsChecked = true;
                    PumpUntil(() => !Field<bool>(window, "_busy"), "window startup preferences saved");
                    Check(window.WindowState == WindowState.Normal, "Changing next-launch minimization hid the current window.");
                    Check(changes == 3 && registrations.SequenceEqual(new[] { true }),
                        "Window preferences registered SteamVR unnecessarily or failed to notify the session monitor.");
                    var saved = AppSettings.Load(profile);
                    Check(saved.StartWithSteamVr && saved.CloseWithSteamVr && saved.StartMinimized && !saved.AutoStart,
                        "Startup preferences were not saved independently of bridge startup.");
                    Named<TextBox>(window, "PortBox").Text = "19200";
                    Named<TextBox>(window, "ReceiveBox").Text = "19201";
                    InvokeSaveConnection();
                    saved = AppSettings.Load(profile);
                    Check(saved.StartWithSteamVr && saved.CloseWithSteamVr && saved.StartMinimized
                        && saved.OutputPort == 19200 && saved.ReceivePort == 19201 && !saved.AutoStart,
                        "Saving a connection reset the startup preferences.");
                    Named<RadioButton>(window, "PreferencesNav").IsChecked = true;
                    PumpUntil(() => Named<StackPanel>(window, "PreferencesPage").Opacity >= .999, "startup settings entrance completed");
                    themes.ApplyTheme(false); Pump(); SavePreview(window, renderDirectory, "startup-light.png");
                    themes.ApplyTheme(true); Pump(); SavePreview(window, renderDirectory, "startup-dark.png");
                    CloseWindow();
                    window = CreateWindow(profile, configureSteamVrAutoLaunch: Configure);
                    themes.AttachWindow(window);
                    window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "startup preferences reopened");
                    Check(window.StartWithSteamVr && window.CloseWithSteamVr && window.StartMinimized
                        && Named<CheckBox>(window, "StartWithSteamVrBox").IsChecked == true
                        && Named<CheckBox>(window, "CloseWithSteamVrBox").IsChecked == true
                        && Named<CheckBox>(window, "StartMinimizedBox").IsChecked == true
                        && registrations.Count == 1, "Reopening lost saved startup choices or registered SteamVR again.");
                    Named<CheckBox>(window, "StartWithSteamVrBox").IsChecked = false;
                    PumpUntil(() => !Field<bool>(window, "_busy"), "SteamVR automatic startup disabled");
                    Check(registrations.SequenceEqual(new[] { true, false }) && !AppSettings.Load(profile).StartWithSteamVr,
                        "Turning off SteamVR startup did not unregister and save the preference.");
                }
                finally { CloseWindow(); }
            });

            Test("a SteamVR registration failure restores the checkbox and preserves the saved profile", () =>
            {
                var profile = Path.Combine(root, "startup-registration-failure");
                new AppSettings { AutoStart = false, OutputPort = 19300, ReceivePort = 19301 }.Save(profile);
                var calls = 0;
                try
                {
                    window = CreateWindow(profile, configureSteamVrAutoLaunch: _ =>
                    {
                        calls++;
                        return Task.FromException(new InvalidOperationException("SteamVR is not installed. Install SteamVR and try again."));
                    });
                    window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "registration failure profile loaded");
                    var changes = 0;
                    window.StartupPreferencesChanged += (_, _) => changes++;
                    Named<CheckBox>(window, "StartWithSteamVrBox").IsChecked = true;
                    PumpUntil(() => !Field<bool>(window, "_busy"), "registration failure handled");
                    var saved = AppSettings.Load(profile);
                    Check(calls == 1 && changes == 0 && !window.StartWithSteamVr
                        && Named<CheckBox>(window, "StartWithSteamVrBox").IsChecked == false && !saved.StartWithSteamVr,
                        "A failed SteamVR registration left startup enabled or notified the session monitor.");
                    Check(!saved.AutoStart && saved.OutputPort == 19300 && saved.ReceivePort == 19301,
                        "A failed registration changed existing connection preferences.");
                    Check(Named<Border>(window, "GlobalNotice").Visibility == Visibility.Visible
                        && Named<TextBlock>(window, "GlobalNoticeText").Text.Contains("Install SteamVR and try again"),
                        "A failed registration did not retain its actionable error.");
                    Check(Named<CheckBox>(window, "StartWithSteamVrBox").IsEnabled,
                        "A failed registration prevented another attempt.");
                }
                finally { CloseWindow(); }
            });

            Test("a settings write failure restores SteamVR registration and the visible preference", () =>
            {
                var profile = Path.Combine(root, "startup-save-failure");
                new AppSettings { AutoStart = false }.Save(profile);
                var registrations = new List<bool>();
                try
                {
                    window = CreateWindow(profile, configureSteamVrAutoLaunch: enabled =>
                    {
                        registrations.Add(enabled);
                        return Task.CompletedTask;
                    });
                    window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "startup save failure profile loaded");
                    using (var locked = new FileStream(Path.Combine(profile, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        Named<CheckBox>(window, "StartWithSteamVrBox").IsChecked = true;
                        PumpUntil(() => !Field<bool>(window, "_busy"), "startup save failure rolled back");
                    }
                    Check(registrations.SequenceEqual(new[] { true, false }) && !window.StartWithSteamVr
                        && Named<CheckBox>(window, "StartWithSteamVrBox").IsChecked == false
                        && !AppSettings.Load(profile).StartWithSteamVr,
                        "A failed settings write left SteamVR registered or the preference enabled.");
                    Check(Named<TextBlock>(window, "GlobalNoticeText").Text.Contains("could not be saved"),
                        "The settings write error was not explained.");
                }
                finally { CloseWindow(); }
            });

            Test("closing waits for an in-flight SteamVR registration to finish saving", () =>
            {
                var profile = Path.Combine(root, "startup-closing");
                new AppSettings { AutoStart = false }.Save(profile);
                var registration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try
                {
                    window = CreateWindow(profile, configureSteamVrAutoLaunch: _ => registration.Task);
                    window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "startup closing profile loaded");
                    window.Dispatcher.BeginInvoke(new Action(() => Named<CheckBox>(window, "StartWithSteamVrBox").IsChecked = true));
                    PumpUntil(() => Field<bool>(window, "_busy"), "asynchronous registration began on the UI dispatcher");
                    Check(Field<bool>(window, "_busy") && !Named<CheckBox>(window, "StartWithSteamVrBox").IsEnabled,
                        "The UI allowed another preference change during registration.");
                    var closing = window;
                    closing.Dispatcher.BeginInvoke(new Action(closing.Close));
                    PumpUntil(() => Field<bool>(closing, "_closing"), "close requested during registration");
                    Check(closing.IsVisible && Field<bool>(closing, "_closing"), "Closing abandoned the in-flight startup change.");
                    registration.SetResult();
                    PumpUntil(() => !closing.IsVisible, "window closed after startup registration settled");
                    Check(AppSettings.Load(profile).StartWithSteamVr, "Closing lost the completed startup registration preference.");
                }
                finally { registration.TrySetResult(); CloseWindow(); }
            });

            Test("closing X hides to the tray without closing the window or changing runtime state", () =>
            {
                var profile = Path.Combine(root, "close-to-tray");
                new AppSettings { AutoStart = false }.Save(profile);
                try
                {
                    window = CreateWindow(profile);
                    window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "close-to-tray profile loaded");
                    var ensured = 0; var closed = false;
                    window.ConfigureSystemTray(() => { ensured++; return true; });
                    window.Closed += (_, _) => closed = true;
                    var running = Field<bool>(window, "_running");
                    var worker = Field<object?>(window, "_worker");
                    var taskbarPreference = window.ShowInTaskbar;
                    window.Close();
                    PumpUntil(() => !window.IsVisible, "closing X hid the window");
                    Check(ensured > 0 && !closed && !Field<bool>(window, "_closing")
                        && Field<bool>(window, "_running") == running && ReferenceEquals(worker, Field<object?>(window, "_worker")),
                        "Closing X began shutdown or changed tracking runtime state instead of hiding.");
                    window.RestoreFromSystemTray();
                    PumpUntil(() => window.IsVisible && !IsIconic(new WindowInteropHelper(window).Handle), "tray restore reopened the window");
                    Check(window.ShowInTaskbar == taskbarPreference, "Restoring changed the window's taskbar preference.");
                    Check(window.HideToSystemTray() && !window.IsVisible, "Hiding an already initialized window failed.");
                    window.RequestExit();
                    PumpUntil(() => closed, "explicit Quit closed the hidden window");
                    Check(!Field<bool>(window, "_running") && Field<object?>(window, "_worker") is null,
                        "Explicit Quit left the runtime active.");
                }
                finally { CloseWindow(); }
            });

            Test("an unavailable system tray leaves the window accessible and allows an explicit Quit", () =>
            {
                var profile = Path.Combine(root, "unavailable-tray");
                new AppSettings { AutoStart = false }.Save(profile);
                try
                {
                    window = CreateWindow(profile);
                    window.Show();
                    PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "tray fallback profile loaded");
                    window.ConfigureSystemTray(() => false);
                    Check(!window.HideToSystemTray() && window.IsVisible,
                        "A failed tray registration hid the only accessible window.");
                    var closed = false;
                    window.Closed += (_, _) => closed = true;
                    window.RequestExit();
                    PumpUntil(() => closed, "Quit closed the visible fallback");
                }
                finally { CloseWindow(); }
            });

            if (published is not null)
            {
                Test("the published app starts in the system tray, restores, hides on X and quits cleanly", () =>
                {
                    var profile = Path.Combine(root, "published-minimized-startup");
                    new AppSettings { AutoStart = false, StartMinimized = true }.Save(profile);
                    Process Launch(params string[] arguments)
                    {
                        var start = new ProcessStartInfo(Path.Combine(published, "BabbleFrameBridge.exe")) { UseShellExecute = false };
                        foreach (var argument in arguments) start.ArgumentList.Add(argument);
                        start.ArgumentList.Add("--data");
                        start.ArgumentList.Add(profile);
                        return Process.Start(start) ?? throw new InvalidOperationException("The published app did not start.");
                    }
                    using var process = Launch();
                    try
                    {
                        IntPtr main = IntPtr.Zero, tray = IntPtr.Zero;
                        PumpUntil(() =>
                        {
                            main = FindProcessWindow(process.Id, "Face Tracking Bridge");
                            tray = FindProcessWindow(process.Id, "Face Tracking Bridge tray");
                            return process.HasExited || main != IntPtr.Zero && (HasTrayIcon(tray) || IsWindowVisible(main));
                        }, "published startup established a tray icon or visible fallback", TimeSpan.FromSeconds(15));
                        Check(!process.HasExited && main != IntPtr.Zero, "The app exited or did not create its main window.");
                        var hasTray = HasTrayIcon(tray);
                        Check(FindWindow("Shell_TrayWnd", null) == IntPtr.Zero || hasTray,
                            "Explorer's notification area exists, but the app did not register its system tray icon.");
                        Console.WriteLine(hasTray ? "  Native shell tray icon lifecycle exercised." : "  No notification area; visible fallback exercised.");
                        if (hasTray)
                            Check(!IsWindowVisible(main), "Start minimized left the main window visible instead of hiding it in the system tray.");
                        else
                            Check(IsWindowVisible(main) && !IsIconic(main), "An unavailable system tray stranded the app without a visible fallback.");

                        using (var reopen = Launch())
                            Check(reopen.WaitForExit(5000) && reopen.ExitCode == 0, "Reopening the app blocked instead of restoring the existing instance.");
                        PumpUntil(() =>
                        {
                            main = FindProcessWindow(process.Id, "Face Tracking Bridge");
                            return main != IntPtr.Zero && IsWindowVisible(main) && !IsIconic(main);
                        }, "a second launch restored the existing main window");
                        Check(GetWindow(main, 4) == IntPtr.Zero, "The restored window was owned instead of being available in the taskbar.");

                        if (hasTray)
                        {
                            PostMessage(main, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE, title-bar X
                            PumpUntil(() => !IsWindowVisible(main), "closing X hid the window to the system tray");
                            Check(!process.HasExited && HasTrayIcon(tray), "Closing X quit the process or removed its tray icon.");
                            PostMessage(tray, 0x8247, IntPtr.Zero, (IntPtr)((1 << 16) | 0x0400)); // v4 NIN_SELECT
                            PumpUntil(() =>
                            {
                                main = FindProcessWindow(process.Id, "Face Tracking Bridge");
                                return main != IntPtr.Zero && IsWindowVisible(main);
                            }, "tray activation reopened the window");
                            PostMessage(main, 0x0010, IntPtr.Zero, IntPtr.Zero);
                            PumpUntil(() => !IsWindowVisible(main), "the restored window hid to tray again");
                        }
                        using (var quit = Launch("--quit"))
                            Check(quit.WaitForExit(5000) && quit.ExitCode == 0, "Quit did not reach the existing app instance.");
                        Check(process.WaitForExit(5000) && process.ExitCode == 0, "The tray app did not stop cleanly after Quit.");
                        Check(!HasTrayIcon(tray), "Quitting left the native system tray icon registered.");
                    }
                    finally
                    {
                        if (!process.HasExited)
                        {
                            using var quit = Launch("--quit");
                            quit.WaitForExit(5000);
                            if (!process.WaitForExit(5000)) process.Kill(entireProcessTree: true);
                        }
                    }
                });

                Test("two simultaneous launches restore one tray instance without leaving a duplicate process", () =>
                {
                    var profile = Path.Combine(root, "published-simultaneous-startup");
                    new AppSettings { AutoStart = false, StartMinimized = true }.Save(profile);
                    Process Launch(params string[] arguments)
                    {
                        var start = new ProcessStartInfo(Path.Combine(published, "BabbleFrameBridge.exe")) { UseShellExecute = false };
                        foreach (var argument in arguments) start.ArgumentList.Add(argument);
                        start.ArgumentList.Add("--data");
                        start.ArgumentList.Add(profile);
                        return Process.Start(start) ?? throw new InvalidOperationException("The simultaneous app launch failed.");
                    }
                    using var first = Launch();
                    using var second = Launch();
                    try
                    {
                        Process? winner = null;
                        PumpUntil(() =>
                        {
                            if (first.HasExited == second.HasExited) return false;
                            winner = first.HasExited ? second : first;
                            var handle = FindProcessWindow(winner.Id, "Face Tracking Bridge");
                            return handle != IntPtr.Zero && IsWindowVisible(handle) && !IsIconic(handle);
                        }, "simultaneous launches restored the one surviving instance", TimeSpan.FromSeconds(15));
                        Check(winner is not null && (first.HasExited ? first.ExitCode : second.ExitCode) == 0,
                            "The duplicate launch failed instead of restoring the app.");
                        var tray = FindProcessWindow(winner!.Id, "Face Tracking Bridge tray");
                        Check(FindWindow("Shell_TrayWnd", null) == IntPtr.Zero || HasTrayIcon(tray),
                            "The surviving simultaneous instance lost its native tray icon.");
                        using (var quit = Launch("--quit"))
                            Check(quit.WaitForExit(5000) && quit.ExitCode == 0, "Quit did not reach the simultaneous-startup winner.");
                        Check(winner.WaitForExit(5000) && winner.ExitCode == 0,
                            "The simultaneous-startup winner did not close normally.");
                        Check(!HasTrayIcon(tray), "The simultaneous-startup winner left a tray icon behind.");
                    }
                    finally
                    {
                        if (!first.HasExited || !second.HasExited)
                        {
                            using var quit = Launch("--quit");
                            quit.WaitForExit(5000);
                            foreach (var process in new[] { first, second })
                                if (!process.HasExited && !process.WaitForExit(5000)) process.Kill(entireProcessTree: true);
                        }
                    }
                });
            }

            if (suppliedZip is not null)
            {
                if (workerExecutable is not null)
                {
                    Test("hidden startup initializes tracking before the window is ever shown and restores the same worker", () =>
                    {
                        var profile = Path.Combine(root, "hidden-tracking-startup");
                        var fixtures = Path.Combine(root, "hidden-startup-fixtures");
                        Directory.CreateDirectory(fixtures);
                        using var output = ReservePort();
                        var reservations = Enumerable.Range(0, 3).Select(_ => ReservePort()).ToArray();
                        var receivePort = PortOf(reservations[0]);
                        var linkPort = PortOf(reservations[1]);
                        var inputPort = PortOf(reservations[2]);
                        foreach (var reservation in reservations) reservation.Dispose();
                        new AppSettings
                        {
                            AutoStart = true, StartMinimized = true, OutputPort = PortOf(output), ReceivePort = receivePort,
                            LinkPort = linkPort, InputSource = TrackingInputSource.HeadsetRuntime
                        }.Save(profile);
                        var store = new ModuleStore(profile);
                        store.Install(ModuleFixture(suppliedZip, fixtures, "4.0.0", inputPort, linkPort));
                        try
                        {
                            window = CreateWindow(profile, workerExecutable);
                            themes.AttachWindow(window);
                            window.ConfigureSystemTray(() => true);
                            new WindowInteropHelper(window).EnsureHandle();
                            Task? initialization = null;
                            window.Dispatcher.BeginInvoke(new Action(() => initialization = window.InitializeBridgeAsync()));
                            PumpUntil(() => initialization?.IsCompleted == true, "hidden startup initialized its worker", TimeSpan.FromSeconds(30));
                            Check(initialization!.IsCompletedSuccessfully && Field<bool>(window, "_initialized")
                                && Field<bool>(window, "_running") && !window.IsVisible,
                                "Tracking did not start while the window remained hidden.");
                            var worker = Field<Process>(window, "_worker");
                            var pid = worker.Id;
                            using var observation = Process.GetProcessById(pid);
                            Check(!worker.HasExited && worker.StartInfo.ArgumentList.Contains("--no-discovery"),
                                "Hidden startup did not create an isolated live worker.");
                            window.RestoreFromSystemTray();
                            PumpUntil(() => window.IsVisible && window.IsLoaded && !Field<bool>(window, "_busy"), "hidden startup restored its first window");
                            Check(Field<bool>(window, "_running") && Field<Process>(window, "_worker").Id == pid,
                                "First restore restarted or lost the already initialized tracking worker.");
                            window.RequestExit();
                            PumpUntil(() => observation.HasExited, "Quit stopped the hidden-started worker");
                        }
                        finally { CloseWindow(); }
                    });
                }

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
                    CheckAvailableDownloads(ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink, ModuleKind.VirtualDesktop);
                    Check(Named<TextBlock>(window, "PackageDetail").Text.Contains("23456"), "Custom headset input port was not shown.");
                    PumpUntil(() => Named<StackPanel>(window, "OverviewPage").Opacity >= .999,
                        "module overview entrance finished before preview capture");
                    SavePreview(window, renderDirectory, "module-ready.png");
                    var readyTheme = themes.CurrentIsDark;
                    foreach (var dark in new[] { false, true })
                    {
                        themes.ApplyTheme(dark); Pump();
                        SavePreview(window, renderDirectory, dark ? "module-ready-dark.png" : "module-ready-light.png");
                    }
                    themes.ApplyTheme(readyTheme);
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
                    CheckAvailableDownloads(ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink, ModuleKind.VirtualDesktop);
                    var cards = string.Join("\n", Descendants(Named<ItemsControl>(window, "ModuleLibrary"))
                        .OfType<TextBlock>().Select(block => block.Text));
                    Check(cards.Contains("UDP 23456") && cards.Contains("UDP 42424"),
                        "The library did not show the distinct input ports of both profiles.");
                    CheckLibraryLayout();
                    SavePreview(window, renderDirectory, "modules.png");
                    var libraryTheme = themes.CurrentIsDark;
                    themes.ApplyTheme(true); Pump();
                    SavePreview(window, renderDirectory, "legacy-bfr-desktop-choice.png");
                    themes.ApplyTheme(libraryTheme);
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

            if (baballoniaAlphaZip is not null)
            {
                Test("a compatible bare Baballonia DLL is recognized without a module manifest or misleading version warning", () =>
                {
                    var profile = Path.Combine(root, "alpha-bare-dll-profile");
                    var fixtures = Path.Combine(root, "alpha-bare-dll-fixtures");
                    Directory.CreateDirectory(fixtures);
                    var bareDll = Path.Combine(fixtures, ModuleStore.BaballoniaDll);
                    using (var archive = ZipFile.OpenRead(baballoniaAlphaZip))
                    {
                        var entry = archive.Entries.Single(entry => Path.GetFileName(entry.FullName) == ModuleStore.BaballoniaDll);
                        using var input = entry.Open();
                        using var output = File.Create(bareDll);
                        input.CopyTo(output);
                    }
                    var originalHash = HashFile(bareDll);
                    new AppSettings { AutoStart = false }.Save(profile);
                    var release = new ModuleStore(profile).Install(bareDll);
                    Check(release.Kind == ModuleKind.BaballoniaDesktop && !release.SupportsHeadset
                        && Version.TryParse(release.Version, out var numeric) && numeric < new Version(3, 2, 0),
                        "The compatible bare-DLL fixture did not cover its ambiguous assembly version.");
                    try
                    {
                        window = CreateWindow(profile);
                        themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "bare alpha DLL profile loaded");
                        Named<RadioButton>(window, "ModulesNav").IsChecked = true; Pump();
                        CheckAvailableDownloads(ModuleKind.BabbleHeadsetRuntime, ModuleKind.SteamLink, ModuleKind.VirtualDesktop);
                        Check(!CardButtons(release).Any(button => button.IsVisible
                            && AutomationProperties.GetName(button) == "Update for Expressions Alpha"),
                            "The compatible bare DLL incorrectly received an old-version update warning.");
                        Check(Field<object?>(window, "_worker") is null && HashFile(bareDll) == originalHash,
                            "Inspecting the compatible bare DLL executed it or changed its original file.");
                    }
                    finally { CloseWindow(); }
                });
            }

            if (baballoniaLegacyZip is not null && baballoniaAlphaZip is not null && workerExecutable is not null)
            {
                Test("an old Baballonia profile offers an alpha update and preserves live tracking on failure or cancellation", () =>
                {
                    var profile = Path.Combine(root, "alpha-update-profile");
                    var fixtures = Path.Combine(root, "alpha-update-fixtures");
                    Directory.CreateDirectory(fixtures);
                    using var output = ReservePort();
                    var reservations = Enumerable.Range(0, 3).Select(_ => ReservePort()).ToArray();
                    var receivePort = PortOf(reservations[0]); var linkPort = PortOf(reservations[1]); var inputPort = PortOf(reservations[2]);
                    foreach (var reservation in reservations) reservation.Dispose();
                    new AppSettings { AutoStart = false, OutputPort = PortOf(output), ReceivePort = receivePort, LinkPort = linkPort }.Save(profile);
                    var legacy = ModuleFixture(baballoniaLegacyZip, fixtures, "3.1.0", inputPort, linkPort);
                    var originalZipHash = HashFile(baballoniaLegacyZip);
                    using var handler = new FixtureDownloadHandler(new Dictionary<Uri, byte[]>
                    {
                        [ModuleDownloader.SourceFor(ModuleKind.BaballoniaDesktop).DownloadUri] = File.ReadAllBytes(baballoniaAlphaZip)
                    });
                    using var client = new HttpClient(handler);
                    using var downloader = new ModuleDownloader(client);
                    try
                    {
                        window = CreateWindow(profile, workerExecutable, downloader);
                        themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "old alpha profile loaded");
                        InvokeModuleImport(legacy);
                        var old = new ModuleStore(profile).Current!;
                        Check(old.Kind == ModuleKind.BaballoniaDesktop && old.Version == "3.1.0", "The old module fixture did not select its desktop profile.");
                        var oldPid = Field<Process>(window, "_worker").Id;
                        var oldConfig = Path.Combine(old.DirectoryPath, "BabbleConfig.json");
                        var oldConfigHash = HashFile(oldConfig);
                        var state = File.ReadAllText(Path.Combine(profile, "modules", "module-state.json"));
                        var settings = File.ReadAllText(Path.Combine(profile, "settings.json"));
                        Named<RadioButton>(window, "ModulesNav").IsChecked = true; Pump();
                        CheckAvailableDownloads(DownloadOrder);
                        Check(CardButton(old, "Update for Expressions Alpha").IsEnabled, "The old module has no in-app alpha update action.");
                        Check(string.Join("\n", Descendants(Named<ItemsControl>(window, "ModuleLibrary")).OfType<TextBlock>().Select(text => text.Text))
                            .Contains("3.2.0", StringComparison.Ordinal), "The old module does not explain the compatible alpha version.");
                        SavePreview(window, renderDirectory, "baballonia-alpha-update.png");

                        void CheckOldUnchanged()
                        {
                            Check(new ModuleStore(profile).Current == old && Field<bool>(window!, "_running")
                                && Field<Process>(window!, "_worker").Id == oldPid, "An incomplete alpha update interrupted the working module.");
                            Check(File.ReadAllText(Path.Combine(profile, "modules", "module-state.json")) == state
                                && File.ReadAllText(Path.Combine(profile, "settings.json")) == settings
                                && HashFile(oldConfig) == oldConfigHash, "An incomplete alpha update changed saved modules or connection settings.");
                        }
                        handler.SetMode(DownloadResponse.Failure);
                        InvokeCardButton(old, "Update for Expressions Alpha");
                        CheckOldUnchanged();
                        handler.SetMode(DownloadResponse.WaitForCancellation);
                        BeginButtonClick(CardButton(old, "Update for Expressions Alpha"));
                        PumpUntil(() => handler.RequestStarted.Task.IsCompleted && Field<bool>(window, "_busy"), "cancelable alpha update began");
                        BeginButtonClick(Named<Button>(window, "DownloadCancelButton"));
                        PumpUntil(() => !Field<bool>(window, "_busy"), "alpha update cancellation completed");
                        Check(handler.ObservedCancellation, "The alpha update did not cancel its download.");
                        CheckOldUnchanged();
                        handler.SetMode(DownloadResponse.Success);
                        InvokeCardButton(old, "Update for Expressions Alpha");
                        var updatedStore = new ModuleStore(profile);
                        var updated = updatedStore.Current!;
                        Check(updated.Kind == ModuleKind.BaballoniaDesktop && Version.Parse(updated.Version) >= new Version(3, 2, 0)
                            && updated.Id != old.Id && updatedStore.Previous == old && updatedStore.Installed.Count == 2,
                            "The alpha update did not select the compatible desktop module while retaining the old module.");
                        Check(Field<bool>(window, "_running") && Field<Process>(window, "_worker").Id != oldPid,
                            "The compatible alpha module was not started.");
                        Check(WorkerArgument("--input-port") == inputPort.ToString()
                            && JsonNode.Parse(File.ReadAllText(Path.Combine(updated.DirectoryPath, "BabbleConfig.json")))!["Port"]!.GetValue<int>() == inputPort,
                            "Updating the alpha module reset the user's tracking input port.");
                        Check(HashFile(oldConfig) == oldConfigHash && HashFile(baballoniaLegacyZip) == originalZipHash
                            && File.ReadAllText(Path.Combine(profile, "settings.json")) == settings,
                            "Updating the alpha module modified its rollback copy, original ZIP or VRChat connection.");
                        Check(handler.Requests.Count == 3 && handler.Requests.All(uri => uri == ModuleDownloader.SourceFor(ModuleKind.BaballoniaDesktop).DownloadUri),
                            "The alpha update requested the wrong provider or an unexpected download.");
                        CheckAvailableDownloads(ModuleKind.BabbleHeadsetRuntime, ModuleKind.SteamLink, ModuleKind.VirtualDesktop);
                        CloseWindow();
                        window = CreateWindow(profile, workerExecutable, downloader);
                        themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "compatible alpha profile reopened");
                        Check(new ModuleStore(profile).Current == updated && Field<object?>(window, "_worker") is null,
                            "Reopening lost the alpha update or ignored AutoStart=false.");
                    }
                    finally { CloseWindow(); }
                });

                if (suppliedZip is not null)
                {
                    Test("an alpha update reuses the highest compatible desktop module and leaves its headset companion intact", () =>
                    {
                        var profile = Path.Combine(root, "alpha-reuse-profile");
                        var fixtures = Path.Combine(root, "alpha-reuse-fixtures");
                        Directory.CreateDirectory(fixtures);
                        using var output = ReservePort();
                        var reservations = Enumerable.Range(0, 3).Select(_ => ReservePort()).ToArray();
                        var receivePort = PortOf(reservations[0]); var linkPort = PortOf(reservations[1]);
                        var oldInput = PortOf(reservations[2]); var compatibleInput = oldInput;
                        foreach (var reservation in reservations) reservation.Dispose();
                        new AppSettings { AutoStart = false, OutputPort = PortOf(output), ReceivePort = receivePort, LinkPort = linkPort }.Save(profile);
                        var store = new ModuleStore(profile);
                        var old = store.Install(ModuleFixture(baballoniaLegacyZip, fixtures, "3.1.0", oldInput, linkPort));
                        var headset = store.Prepare(ModuleFixture(suppliedZip, fixtures, "4.0.0", compatibleInput, linkPort), ModuleKind.BabbleHeadsetRuntime);
                        store.Register(headset);
                        var desktop = store.CreateCompanion(headset); store.Register(desktop);
                        var alpha = store.Prepare(baballoniaAlphaZip, ModuleKind.BaballoniaDesktop); store.Register(alpha);
                        var headsetConfig = Path.Combine(headset.DirectoryPath, "BabbleConfig.json");
                        var headsetHash = HashFile(headsetConfig);
                        using var handler = new FixtureDownloadHandler(new Dictionary<Uri, byte[]>());
                        using var client = new HttpClient(handler);
                        using var downloader = new ModuleDownloader(client);
                        try
                        {
                            window = CreateWindow(profile, workerExecutable, downloader);
                            themes.AttachWindow(window); window.Show();
                            PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "alpha reuse profile loaded");
                            BeginButtonClick(Named<Button>(window, "StartButton"));
                            PumpUntil(() => Field<bool>(window, "_running") && !Field<bool>(window, "_busy"), "old module started before compatible reuse");
                            CheckAvailableDownloads(ModuleKind.SteamLink, ModuleKind.VirtualDesktop);
                            InvokeCardButton(old, "Update for Expressions Alpha");
                            var updated = new ModuleStore(profile);
                            Check(updated.Current == desktop && updated.Previous == old && updated.Installed.Count == 4,
                                "Updating the old module did not reuse the highest compatible existing desktop profile.");
                            Check(handler.Requests.Count == 0 && DownloadArchives(profile).Length == 0,
                                "A compatible installed desktop module unnecessarily triggered a download.");
                            Check(WorkerArgument("--input-port") == compatibleInput.ToString() && HashFile(headsetConfig) == headsetHash,
                                "Reusing the compatible desktop module reset its port or changed the headset's independent settings.");
                        }
                        finally { CloseWindow(); }
                    });

                    Test("updating an inactive old Baballonia profile preserves a running headset and independently configured modules", () =>
                    {
                        var profile = Path.Combine(root, "alpha-inactive-update-profile");
                        var fixtures = Path.Combine(root, "alpha-inactive-update-fixtures");
                        Directory.CreateDirectory(fixtures);
                        using var output = ReservePort();
                        var reservations = Enumerable.Range(0, 4).Select(_ => ReservePort()).ToArray();
                        var receivePort = PortOf(reservations[0]); var linkPort = PortOf(reservations[1]);
                        var oldInput = PortOf(reservations[2]); var headsetInput = PortOf(reservations[3]);
                        foreach (var reservation in reservations) reservation.Dispose();
                        new AppSettings { AutoStart = false, OutputPort = PortOf(output), ReceivePort = receivePort, LinkPort = linkPort }.Save(profile);
                        var store = new ModuleStore(profile);
                        var old = store.Install(ModuleFixture(baballoniaLegacyZip, fixtures, "3.1.0", oldInput, linkPort));
                        var headset = store.Prepare(ModuleFixture(suppliedZip, fixtures, "4.0.0", headsetInput, linkPort), ModuleKind.BabbleHeadsetRuntime);
                        store.Register(headset);
                        var desktop = store.CreateCompanion(headset); store.Register(desktop);
                        store.Activate(headset);
                        using var handler = new FixtureDownloadHandler(new Dictionary<Uri, byte[]>
                        {
                            [ModuleDownloader.SourceFor(ModuleKind.BaballoniaDesktop).DownloadUri] = File.ReadAllBytes(baballoniaAlphaZip)
                        });
                        using var client = new HttpClient(handler);
                        using var downloader = new ModuleDownloader(client);
                        try
                        {
                            window = CreateWindow(profile, workerExecutable, downloader);
                            themes.AttachWindow(window); window.Show();
                            PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "inactive alpha update profile loaded");
                            BeginButtonClick(Named<Button>(window, "StartButton"));
                            PumpUntil(() => Field<bool>(window, "_running") && !Field<bool>(window, "_busy"), "headset started before inactive alpha update");
                            var pid = Field<Process>(window, "_worker").Id;
                            var previous = new ModuleStore(profile).Previous;
                            var desktopConfig = Path.Combine(desktop.DirectoryPath, "BabbleConfig.json");
                            var desktopHash = HashFile(desktopConfig);
                            var settings = File.ReadAllText(Path.Combine(profile, "settings.json"));
                            InvokeCardButton(old, "Update for Expressions Alpha");
                            var updated = new ModuleStore(profile);
                            var alpha = updated.Installed.Single(release => release.Kind == ModuleKind.BaballoniaDesktop && release.Version == "3.2.0");
                            Check(updated.Current == headset && updated.Previous == previous && updated.Installed.Count == 4
                                && Field<bool>(window, "_running") && Field<Process>(window, "_worker").Id == pid,
                                "Updating an inactive old profile interrupted or switched the running headset.");
                            Check(handler.Requests.Count == 1 && HashFile(desktopConfig) == desktopHash
                                && JsonNode.Parse(File.ReadAllText(Path.Combine(alpha.DirectoryPath, "BabbleConfig.json")))!["Port"]!.GetValue<int>() == oldInput,
                                "The inactive alpha update reused or overwrote a differently configured compatible desktop profile.");
                            Check(File.ReadAllText(Path.Combine(profile, "settings.json")) == settings,
                                "The inactive alpha update changed the VRChat connection settings.");
                        }
                        finally { CloseWindow(); }
                    });
                }
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
                        CheckAvailableDownloads(ModuleKind.BaballoniaDesktop);
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
                    var executable = workerExecutable!;
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
                        var liveParticles = Named<ParticleField>(window, "HeroParticles");
                        PumpUntil(() => liveParticles.Opacity >= .999,
                            "real tracking startup brightened the particle field");
                        Check(liveParticles.AnimationsEnabled == SystemParameters.ClientAreaAnimation,
                            "Real tracking startup did not apply the Windows particle-motion policy.");
                        Check(Field<Process>(window, "_worker").StartInfo.ArgumentList.Contains("--no-discovery"),
                            "The isolated worker could discover a live VRChat session.");
                        var originalPid = Field<Process>(window, "_worker").Id;
                        window.ConfigureSystemTray(() => true);
                        window.Close();
                        PumpUntil(() => !window.IsVisible, "active tracking window hid to tray");
                        Check(Field<bool>(window, "_running") && Field<Process>(window, "_worker").Id == originalPid
                            && !Field<Process>(window, "_worker").HasExited,
                            "Closing to tray stopped or replaced the active tracking worker.");
                        window.RestoreFromSystemTray();
                        PumpUntil(() => window.IsVisible, "active tracking window restored from tray");
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
                    var executable = workerExecutable!;
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
                        CheckAvailableDownloads(ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink, ModuleKind.VirtualDesktop);
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
                        var stoppedParticles = Named<ParticleField>(window, "HeroParticles");
                        PumpUntil(() => Math.Abs(stoppedParticles.Opacity - .24) < .001,
                            "real tracking shutdown dimmed the particle field");
                        PumpUntil(() => stoppedParticles.MotionSpeed == 0 && !Field<bool>(stoppedParticles, "attached"),
                            "real tracking shutdown coasted the particle field to rest");
                        Check(!stoppedParticles.AnimationsEnabled && !Field<bool>(stoppedParticles, "attached"),
                            "Real tracking shutdown left the particle field moving.");
                        var frozen = Field<double>(stoppedParticles, "seconds");
                        RenderParticlesAt(stoppedParticles, TimeSpan.FromSeconds(400));
                        Check(Field<double>(stoppedParticles, "seconds") == frozen,
                            "Particles advanced after the real worker stopped.");
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
                        CheckAvailableDownloads(ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink, ModuleKind.VirtualDesktop);
                        foreach (var remaining in new ModuleStore(profile).Installed.ToArray())
                        {
                            InvokeCardButton(remaining, "Remove module");
                            InvokeCardButton(remaining, "Confirm remove module");
                            var remainingStore = new ModuleStore(profile);
                            CheckLibraryReferences(remainingStore);
                            CheckAvailableDownloads(DownloadOrder.Where(kind => kind == ModuleKind.BaballoniaDesktop
                                || !remainingStore.Installed.Any(release => release.Kind == kind)).ToArray());
                        }
                        var empty = new ModuleStore(profile);
                        Check(empty.Installed.Count == 0 && empty.Current is null && empty.Previous is null,
                            "Removing the final entry left an installed selection or rollback reference. Remaining: "
                            + string.Join(", ", empty.Installed.Select(item => item.Id)) + "; notice: "
                            + Named<TextBlock>(window, "GlobalNoticeText").Text);
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
                        CheckAvailableDownloads(DownloadOrder);
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

            if (suppliedZip is not null && baballoniaAlphaZip is not null)
            {
                Test("downloading desktop Baballonia first keeps its profile and settings when BFR is added", () =>
                {
                    var profile = Path.Combine(root, "download-desktop-first-profile");
                    using var handler = new FixtureDownloadHandler(new Dictionary<Uri, byte[]>
                    {
                        [ModuleDownloader.SourceFor(ModuleKind.BabbleHeadsetRuntime).DownloadUri] = File.ReadAllBytes(suppliedZip),
                        [ModuleDownloader.SourceFor(ModuleKind.BaballoniaDesktop).DownloadUri] = File.ReadAllBytes(baballoniaAlphaZip)
                    });
                    using var client = new HttpClient(handler);
                    using var downloader = new ModuleDownloader(client);
                    try
                    {
                        window = CreateWindow(profile, moduleDownloader: downloader);
                        themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "desktop-first download profile loaded");
                        InvokeDownload(ModuleKind.BaballoniaDesktop);
                        var desktop = new ModuleStore(profile).Installed.Single();
                        Check(desktop is { Kind: ModuleKind.BaballoniaDesktop, SupportsHeadset: false, Version: "3.2.0" },
                            "Downloading Baballonia first installed an unwanted headset or non-desktop package.");
                        var desktopConfig = Path.Combine(desktop.DirectoryPath, "BabbleConfig.json");
                        var config = JsonNode.Parse(File.ReadAllText(desktopConfig))!.AsObject();
                        config["Port"] = 45679;
                        File.WriteAllText(desktopConfig, config.ToJsonString());
                        var configHash = HashFile(desktopConfig);
                        CheckAvailableDownloads(ModuleKind.BabbleHeadsetRuntime, ModuleKind.SteamLink, ModuleKind.VirtualDesktop);
                        InvokeDownload(ModuleKind.BabbleHeadsetRuntime);
                        var store = new ModuleStore(profile);
                        Check(store.Installed.Count == 2
                            && store.Installed.Single(release => release.Kind == ModuleKind.BaballoniaDesktop).Id == desktop.Id
                            && store.Installed.Single(release => release.Kind == ModuleKind.BabbleHeadsetRuntime).Version == "4.0.0"
                            && HashFile(desktopConfig) == configHash,
                            "Adding BFR duplicated desktop Baballonia or changed its existing connection settings.");
                        Check(store.Current is null && Field<object?>(window, "_worker") is null,
                            "Adding BFR automatically selected a profile or started tracking.");
                        CheckAvailableDownloads(ModuleKind.SteamLink, ModuleKind.VirtualDesktop);
                        Check(handler.Requests.SequenceEqual(new[] {
                            ModuleDownloader.SourceFor(ModuleKind.BaballoniaDesktop).DownloadUri,
                            ModuleDownloader.SourceFor(ModuleKind.BabbleHeadsetRuntime).DownloadUri }),
                            "The two buttons did not download their separate official packages.");
                        CloseWindow();
                        window = CreateWindow(profile, moduleDownloader: downloader);
                        themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "desktop-first downloaded profiles reopened");
                        Check(new ModuleStore(profile).Installed.Count == 2 && new ModuleStore(profile).Current is null
                            && HashFile(desktopConfig) == configHash,
                            "Reopening lost a chosen profile, selected tracking, or reset desktop settings.");
                        CheckAvailableDownloads(ModuleKind.SteamLink, ModuleKind.VirtualDesktop);
                    }
                    finally { CloseWindow(); }
                });
            }

            if (suppliedZip is not null && baballoniaAlphaZip is not null && virtualDesktopZip is not null && steamLinkZip is not null)
            {
                Test("official downloads add only the requested profiles and remain inactive across relaunch", () =>
                {
                    var profile = Path.Combine(root, "download-four-provider-profile");
                    var packages = new Dictionary<Uri, byte[]>
                    {
                        [ModuleDownloader.SourceFor(ModuleKind.BabbleHeadsetRuntime).DownloadUri] = File.ReadAllBytes(suppliedZip),
                        [ModuleDownloader.SourceFor(ModuleKind.BaballoniaDesktop).DownloadUri] = File.ReadAllBytes(baballoniaAlphaZip),
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
                        foreach (var kind in DownloadOrder)
                        {
                            var priorArchives = DownloadArchives(profile).ToHashSet(StringComparer.OrdinalIgnoreCase);
                            InvokeDownload(kind);
                            var store = new ModuleStore(profile);
                            Check(store.Installed.Any(release => release.Kind == kind), "Download did not add the selected provider: " + kind);
                            if (kind == ModuleKind.BabbleHeadsetRuntime)
                            {
                                Check(store.Installed.Count == 1 && !store.Installed.Any(release => release.Kind == ModuleKind.BaballoniaDesktop),
                                    "The headset download also installed an unwanted desktop profile.");
                                var priorTheme = themes.CurrentIsDark;
                                themes.ApplyTheme(true); Pump();
                                PumpUntil(() => Named<StackPanel>(window, "ModulesPage").Opacity >= .999,
                                    "BFR-only module page finished its entrance before preview");
                                SavePreview(window, renderDirectory, "bfr-download-only.png");
                                themes.ApplyTheme(priorTheme);
                            }
                            if (kind == ModuleKind.BaballoniaDesktop)
                                Check(store.Installed.Count == 2 && store.Installed.Single(release => release.Kind == kind) is { SupportsHeadset: false, Version: "3.2.0" },
                                    "The desktop download did not add the dedicated 3.2.0 profile alongside the existing headset.");
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
                            CheckAvailableDownloads(DownloadOrder.Where(available => !store.Installed.Any(release => release.Kind == available)).ToArray());
                        }
                        Check(handler.Requests.Count == 4 && DownloadArchives(profile).Length == 4,
                            "Each requested module did not retain its own completed ZIP.");
                        Check(new ModuleStore(profile).Installed.Count == 4,
                            "The four package downloads did not add exactly the four requested provider profiles.");
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
                        Named<RadioButton>(window, "ModulesNav").IsChecked = true; Pump();
                        CheckAvailableDownloads();
                        Check(Named<TextBlock>(window, "DownloadChoicesEmptyText").Text.Contains("already in your library"),
                            "The empty download list does not explain that the providers are already imported.");
                        SavePreview(window, renderDirectory, "all-downloads-installed.png");
                    }
                    finally { CloseWindow(); }
                });
            }

            if (additionalModuleDirectory is not null)
            {
                Test("official SRanipal, Beyond and EyeTrackVR download buttons import separate modules and hide installed choices", () =>
                {
                    var profile = Path.Combine(root, "additional-official-provider-downloads");
                    var files = new Dictionary<ModuleKind, string>
                    {
                        [ModuleKind.SRanipal] = "SRanipalTrackingModule.zip",
                        [ModuleKind.Beyond] = "Beyond-Build.zip",
                        [ModuleKind.EyeTrackVR] = "ETVRTrackingModule.dll"
                    };
                    var packages = files.ToDictionary(pair => ModuleDownloader.SourceFor(pair.Key).DownloadUri,
                        pair => File.ReadAllBytes(Path.Combine(additionalModuleDirectory, pair.Value)));
                    using var handler = new FixtureDownloadHandler(packages);
                    using var client = new HttpClient(handler);
                    using var downloader = new ModuleDownloader(client);
                    try
                    {
                        window = CreateWindow(profile, moduleDownloader: downloader); themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "additional providers window loaded");
                        Check(AppSettings.Load(profile).AutoStart, "The fixture did not cover a first-launch auto-start preference.");
                        var installed = 0;
                        foreach (var kind in AdditionalDownloadOrder)
                        {
                            InvokeDownload(kind);
                            var store = new ModuleStore(profile);
                            Check(store.Installed.Count == ++installed && store.Installed.Any(release => release.Kind == kind),
                                "The official download added another provider or failed to import " + kind);
                            Check(store.Current is null && Field<object?>(window, "_worker") is null
                                && !Named<Button>(window, "StartButton").IsEnabled,
                                "Downloading an additional provider selected or started tracking automatically.");
                            CheckAvailableDownloads(DownloadOrder.Concat(AdditionalDownloadOrder.Where(candidate => !store.Installed.Any(release => release.Kind == candidate))).ToArray());
                            var cached = DownloadArchives(profile).Single(path => Path.GetFileName(path) == ModuleDownloader.SourceFor(kind).FileName);
                            Check(HashFile(cached) == Convert.ToHexString(SHA256.HashData(packages[ModuleDownloader.SourceFor(kind).DownloadUri])),
                                "The imported official module bytes were changed or replaced.");
                        }
                        Check(handler.Requests.SequenceEqual(AdditionalDownloadOrder.Select(kind => ModuleDownloader.SourceFor(kind).DownloadUri)),
                            "An additional module button requested the wrong publisher's download.");
                        CloseWindow();
                        window = CreateWindow(profile, moduleDownloader: downloader); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "additional provider library reopened");
                        var reopened = new ModuleStore(profile);
                        Check(reopened.Installed.Count == 3 && AdditionalDownloadOrder.All(kind => reopened.Installed.Any(release => release.Kind == kind))
                            && reopened.Current is null && Field<object?>(window, "_worker") is null,
                            "Reopening lost an additional provider or activated tracking without selection.");
                        Named<RadioButton>(window, "ModulesNav").IsChecked = true; Pump();
                        CheckAvailableDownloads(DownloadOrder);
                        SavePreview(window, renderDirectory, "additional-official-modules-installed.png");
                    }
                    finally { CloseWindow(); }
                });
            }

            if (additionalModuleDirectory is not null && workerExecutable is not null)
            {
                Test("Use module keeps offline SRanipal and Beyond selected with the correct waiting state and a quick stop", () =>
                {
                    foreach (var (kind, filename, expectedLabel) in new[]
                    {
                        (ModuleKind.SRanipal, "SRanipalTrackingModule.zip", "SRanipal"),
                        (ModuleKind.Beyond, "Beyond-Build.zip", "Beyond")
                    })
                    {
                        var activeRuntime = false;
                        if (kind == ModuleKind.SRanipal)
                        {
                            var processes = Process.GetProcessesByName("sr_runtime");
                            try { activeRuntime = processes.Length > 0; }
                            finally { foreach (var process in processes) process.Dispose(); }
                        }
                        else
                        {
                            using var memory = OpenFileMappingW(0x0004, false, "VRCFTMemmapData");
                            activeRuntime = !memory.IsInvalid || Marshal.GetLastWin32Error() == 5;
                        }
                        if (activeRuntime)
                        {
                            Console.WriteLine("SKIP " + kind + " offline UI: the user's tracking runtime is present and was not changed.");
                            continue;
                        }
                        var profile = Path.Combine(root, "offline-" + kind + "-selection");
                        using var output = ReservePort(); using var feedback = ReservePort();
                        var feedbackPort = PortOf(feedback); feedback.Dispose();
                        new AppSettings { AutoStart = false, OutputPort = PortOf(output), ReceivePort = feedbackPort }.Save(profile);
                        var store = new ModuleStore(profile);
                        var module = store.Prepare(Path.Combine(additionalModuleDirectory, filename), kind);
                        store.Register(module);
                        try
                        {
                            window = CreateWindow(profile, workerExecutable); themes.AttachWindow(window); window.Show();
                            PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "offline provider window loaded");
                            var selection = Stopwatch.StartNew();
                            InvokeModuleSelection(module);
                            Check(selection.Elapsed < TimeSpan.FromSeconds(10), "Selecting the offline module waited for its startup timeout.");
                            Check(new ModuleStore(profile).Current == module && Field<bool>(window, "_running")
                                && Field<bool>(window, "_awaitingTrackingSource"),
                                "Offline module selection was rejected or lost its waiting state: " + kind);
                            var title = Named<TextBlock>(window, "StateTitle").Text;
                            var details = Named<TextBlock>(window, "StateDetail").Text;
                            var headset = Named<TextBlock>(window, "HeadsetState").Text;
                            Check(title.Contains("Waiting", StringComparison.OrdinalIgnoreCase) && title.Contains(expectedLabel)
                                && details.Contains(expectedLabel, StringComparison.OrdinalIgnoreCase) && headset.Contains(expectedLabel),
                                "The waiting state does not identify the selected provider: " + kind);
                            Check(!title.Contains("Virtual Desktop") && !details.Contains("Virtual Desktop")
                                && !Named<TextBlock>(window, "GlobalNoticeText").Text.Contains("could not", StringComparison.OrdinalIgnoreCase),
                                "The offline provider displayed another provider's instructions or a false startup error.");
                            var processId = Field<Process>(window, "_worker").Id;
                            PumpFor(TimeSpan.FromMilliseconds(200));
                            Check(output.Available == 0 && Field<Process>(window, "_worker").Id == processId,
                                "Waiting for hardware sent neutral tracking or restarted the runtime.");
                            Named<RadioButton>(window, "OverviewNav").IsChecked = true; Pump();
                            BeginButtonClick(Named<Button>(window, "StartButton"));
                            PumpUntil(() => !Field<bool>(window, "_busy") && !Field<bool>(window, "_running"),
                                "offline provider stopped quickly", TimeSpan.FromSeconds(6));
                            Check(Field<object?>(window, "_worker") is null && !Field<bool>(window, "_awaitingTrackingSource")
                                && new ModuleStore(profile).Current == module,
                                "Stopping the offline provider lost its selection or retained the worker.");
                            CloseWindow();
                            window = CreateWindow(profile, workerExecutable); window.Show();
                            PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "offline provider selection reopened");
                            Check(new ModuleStore(profile).Current == module && Field<object?>(window, "_worker") is null,
                                "Reopening lost the offline provider selection or ignored disabled auto-start.");
                        }
                        finally { CloseWindow(); }
                    }
                });
            }

            if (suppliedZip is not null && baballoniaAlphaZip is not null && published is not null)
            {
                Test("download registration, duplicates, failure and cancellation preserve a running module and its connection", () =>
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
                        [ModuleDownloader.SourceFor(ModuleKind.BabbleHeadsetRuntime).DownloadUri] = File.ReadAllBytes(suppliedZip),
                        [ModuleDownloader.SourceFor(ModuleKind.BaballoniaDesktop).DownloadUri] = File.ReadAllBytes(baballoniaAlphaZip)
                    });
                    using var client = new HttpClient(handler);
                    using var downloader = new ModuleDownloader(client);
                    try
                    {
                        window = CreateWindow(profile, workerExecutable!, downloader);
                        themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "running download profile loaded");
                        InvokeModuleImport(firstZip);
                        var original = new ModuleStore(profile).Current;
                        var originalIds = new ModuleStore(profile).Installed.Select(release => release.Id).ToHashSet();
                        CheckAvailableDownloads(ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink, ModuleKind.VirtualDesktop);
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
                        InvokeDownloadDirect(ModuleKind.BabbleHeadsetRuntime);
                        CheckActiveUnchanged();
                        var afterHeadset = new ModuleStore(profile).Installed;
                        Check(afterHeadset.Count == originalIds.Count + 1
                            && afterHeadset.Count(release => release.Kind == ModuleKind.BaballoniaDesktop) == 1,
                            "The BFR download added an unwanted desktop companion to the imported library.");
                        CheckAvailableDownloads(ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink, ModuleKind.VirtualDesktop);
                        var cache = DownloadArchives(profile).Single();
                        var cacheHash = HashFile(cache);
                        var downloadedHeadset = afterHeadset.Single(release => !originalIds.Contains(release.Id)
                            && release.Kind == ModuleKind.BabbleHeadsetRuntime);
                        InvokeDownload(ModuleKind.BaballoniaDesktop);
                        CheckActiveUnchanged();
                        var afterSuccess = new ModuleStore(profile).Installed;
                        Check(afterSuccess.Count == originalIds.Count + 2, "The explicit desktop download did not add one independent desktop profile.");
                        var downloadedDesktop = afterSuccess.Single(release => !originalIds.Contains(release.Id)
                            && release.Kind == ModuleKind.BaballoniaDesktop);
                        Check(downloadedDesktop is { SupportsHeadset: false, Version: "3.2.0" },
                            "The explicit Baballonia action added a BFR package instead of the dedicated desktop module.");
                        var configuredPath = Path.Combine(downloadedHeadset.DirectoryPath, "BabbleConfig.json");
                        var configured = JsonNode.Parse(File.ReadAllText(configuredPath))!.AsObject();
                        configured["Port"] = 45678;
                        File.WriteAllText(configuredPath, configured.ToJsonString());
                        var configuredHash = HashFile(configuredPath);
                        var initialDownloadedIds = afterSuccess.Select(release => release.Id).ToHashSet();
                        foreach (var repeated in new[] { ModuleKind.BabbleHeadsetRuntime, ModuleKind.BaballoniaDesktop })
                        {
                            InvokeDownloadDirect(repeated);
                            CheckActiveUnchanged();
                            Check(new ModuleStore(profile).Installed.Select(release => release.Id).ToHashSet().SetEquals(initialDownloadedIds)
                                && HashFile(configuredPath) == configuredHash,
                                "A repeated Babble download duplicated profiles or overwrote configured module settings.");
                        }
                        InvokeCardButton(downloadedDesktop, "Remove module");
                        InvokeCardButton(downloadedDesktop, "Confirm remove module");
                        CheckActiveUnchanged();
                        Check(File.Exists(cache) && HashFile(cache) == cacheHash, "Removing a downloaded profile altered its cached source ZIP.");
                        InvokeDownloadDirect(ModuleKind.BaballoniaDesktop);
                        CheckActiveUnchanged();
                        var restoredProfiles = new ModuleStore(profile).Installed;
                        Check(restoredProfiles.Count == afterSuccess.Count
                            && restoredProfiles.Any(release => release.Id == downloadedHeadset.Id)
                            && restoredProfiles.All(release => release.Id != downloadedDesktop.Id)
                            && HashFile(configuredPath) == configuredHash,
                            "Re-downloading a removed desktop profile duplicated the existing headset or reset its settings.");
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
                            CheckAvailableDownloads(ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink, ModuleKind.VirtualDesktop);
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
                        CheckAvailableDownloads(DownloadOrder);
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

            if (suppliedZip is not null && virtualDesktopZip is not null && published is not null)
            {
                Test("a Virtual Desktop startup error leaves an error notice and restores the prior selected worker", () =>
                {
                    var profile = Path.Combine(root, "virtual-desktop-startup-failure-profile");
                    var fixtures = Path.Combine(root, "virtual-desktop-startup-failure-fixtures");
                    Directory.CreateDirectory(fixtures);
                    using var output = ReservePort();
                    var ports = Enumerable.Range(0, 3).Select(_ => ReservePort()).ToArray();
                    var feedback = PortOf(ports[0]); var link = PortOf(ports[1]); var input = PortOf(ports[2]);
                    foreach (var reservation in ports) reservation.Dispose();
                    new AppSettings { AutoStart = false, OutputPort = PortOf(output), ReceivePort = feedback, LinkPort = link }.Save(profile);
                    var babbleZip = ModuleFixture(suppliedZip, fixtures, "4.0.0", input, link);
                    try
                    {
                        window = CreateWindow(profile, workerExecutable!);
                        themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "VD failure profile loaded");
                        InvokeModuleImport(babbleZip);
                        var store = Field<ModuleStore>(window, "_store");
                        var original = store.Current;
                        var originalPid = Field<Process>(window, "_worker").Id;
                        var virtualDesktop = store.Prepare(virtualDesktopZip);
                        store.Register(virtualDesktop);
                        Method(window, "RefreshModule").Invoke(window, []);
                        CheckAvailableDownloads(ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink);
                        // This isolated staged copy is locked only to force a deterministic
                        // loader error; the user's real Streamer and IPC are untouched.
                        using (var unavailableDll = new FileStream(Path.Combine(virtualDesktop.DirectoryPath, virtualDesktop.DllFileName),
                            FileMode.Open, FileAccess.Read, FileShare.None))
                        {
                            InvokeModuleSelection(virtualDesktop);
                            Check(new ModuleStore(profile).Current == original && Field<bool>(window, "_running")
                                && Field<Process>(window, "_worker").Id != originalPid,
                                "A failed VD activation did not restore the prior selected tracking worker.");
                            Check(Named<Border>(window, "GlobalNotice").Visibility == Visibility.Visible
                                && Named<TextBlock>(window, "GlobalNoticeText").Text.Contains("could not start", StringComparison.OrdinalIgnoreCase)
                                && Named<TextBox>(window, "LogBox").Text.Contains("Restored the previous active module."),
                                "The VD startup error was lost or the recovery was not explained.");
                        }
                        CheckAvailableDownloads(ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink);
                    }
                    finally { CloseWindow(); }
                });
            }

            if (virtualDesktopZip is not null && published is not null)
            {
                Test("Use module keeps Virtual Desktop selected while waiting for Streamer and allows a quick stop", () =>
                {
                    if (HasExistingVirtualDesktopIpc())
                    {
                        Console.WriteLine("SKIP VD waiting UI: existing user IPC objects are present; they were not changed.");
                        return;
                    }
                    var profile = Path.Combine(root, "virtual-desktop-waiting-profile");
                    using var output = ReservePort(); using var feedback = ReservePort();
                    var feedbackPort = PortOf(feedback); feedback.Dispose();
                    new AppSettings { AutoStart = false, OutputPort = PortOf(output), ReceivePort = feedbackPort }.Save(profile);
                    var store = new ModuleStore(profile);
                    var virtualDesktop = store.Prepare(virtualDesktopZip);
                    store.Register(virtualDesktop);
                    try
                    {
                        window = CreateWindow(profile, workerExecutable!);
                        themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "VD waiting profile loaded");
                        InvokeModuleSelection(virtualDesktop);
                        Check(new ModuleStore(profile).Current == virtualDesktop && Field<bool>(window, "_running")
                            && Field<bool>(window, "_awaitingTrackingSource"),
                            "Use module rejected the absent Streamer instead of saving a waiting VD selection.");
                        Check(Named<TextBlock>(window, "StateTitle").Text.Contains("Waiting", StringComparison.OrdinalIgnoreCase)
                            && Named<TextBlock>(window, "StateDetail").Text.Contains("Streamer", StringComparison.OrdinalIgnoreCase),
                            "The waiting VD session does not explain the Streamer setup step.");
                        var pid = Field<Process>(window, "_worker").Id;
                        PumpFor(TimeSpan.FromMilliseconds(150));
                        Check(Field<Process>(window, "_worker").Id == pid && output.Available == 0,
                            "A waiting VD session restarted or sent tracking data before initialization.");
                        CheckAvailableDownloads(ModuleKind.BabbleHeadsetRuntime, ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink);
                        Named<RadioButton>(window, "OverviewNav").IsChecked = true; Pump();
                        SavePreview(window, renderDirectory, "virtual-desktop-waiting.png");
                        BeginButtonClick(Named<Button>(window, "StartButton"));
                        PumpUntil(() => !Field<bool>(window, "_busy") && !Field<bool>(window, "_running"),
                            "waiting VD session stopped", TimeSpan.FromSeconds(6));
                        Check(Field<object?>(window, "_worker") is null && !Field<bool>(window, "_awaitingTrackingSource")
                            && new ModuleStore(profile).Current == virtualDesktop,
                            "Stopping a waiting VD worker lost its saved selection or retained waiting state.");
                    }
                    finally { CloseWindow(); }
                });
            }

            if (suppliedZip is not null && published is not null)
            {
                Test("an inactive missing DLL preserves startup of the valid module and restores its download and removal actions", () =>
                {
                    var profile = Path.Combine(root, "missing-inactive-module-profile");
                    var fixtures = Path.Combine(root, "missing-inactive-module-fixtures");
                    Directory.CreateDirectory(fixtures);
                    using var output = ReservePort();
                    var ports = Enumerable.Range(0, 3).Select(_ => ReservePort()).ToArray();
                    var feedback = PortOf(ports[0]); var link = PortOf(ports[1]); var input = PortOf(ports[2]);
                    foreach (var reservation in ports) reservation.Dispose();
                    new AppSettings { AutoStart = false, OutputPort = PortOf(output), ReceivePort = feedback, LinkPort = link }.Save(profile);
                    var originalHash = HashFile(suppliedZip);
                    var package = ModuleFixture(suppliedZip, fixtures, "4.0.0", input, link);
                    var packageHash = HashFile(package);
                    var store = new ModuleStore(profile);
                    var headset = store.Install(package);
                    var desktop = store.CreateCompanion(headset); store.Register(desktop);
                    try
                    {
                        window = CreateWindow(profile, workerExecutable!);
                        themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "valid missing-entry setup opened");
                        CloseWindow();
                        // Delete only the staged inactive copy, never its original package.
                        File.Delete(Path.Combine(desktop.DirectoryPath, desktop.DllFileName));
                        var settings = AppSettings.Load(profile); settings.AutoStart = true; settings.Save(profile);
                        window = CreateWindow(profile, workerExecutable!);
                        themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "library reopened with one inactive DLL missing");
                        var reopened = new ModuleStore(profile);
                        Check(reopened.Current == headset && reopened.Installed.Count == 2
                            && !reopened.Installed.Single(release => release.Id == desktop.Id).IsAvailable,
                            "The missing inactive DLL blocked or erased otherwise valid module metadata.");
                        Check(Field<bool>(window, "_running") && Named<Button>(window, "StartButton").IsEnabled,
                            "The valid saved module could not start because an inactive DLL was missing.");
                        CheckAvailableDownloads(ModuleKind.BaballoniaDesktop, ModuleKind.SteamLink, ModuleKind.VirtualDesktop);
                        var pid = Field<Process>(window, "_worker").Id;
                        Check(!CardButton(desktop, "Unavailable").IsEnabled,
                            "The missing inactive DLL could still be selected for startup.");
                        var text = string.Join("\n", Descendants(Named<ItemsControl>(window, "ModuleLibrary")).OfType<TextBlock>().Select(block => block.Text));
                        Check(text.Contains("missing", StringComparison.OrdinalIgnoreCase), "The missing imported DLL has no recovery guidance.");
                        SavePreview(window, renderDirectory, "missing-inactive-module.png");
                        InvokeCardButton(desktop, "Remove module"); InvokeCardButton(desktop, "Confirm remove module");
                        Check(new ModuleStore(profile).Current == headset && new ModuleStore(profile).Installed.Count == 1
                            && Field<bool>(window, "_running") && Field<Process>(window, "_worker").Id == pid,
                            "Removing a missing inactive entry interrupted the remaining tracking worker.");
                        Check(HashFile(suppliedZip) == originalHash && HashFile(package) == packageHash
                            && File.Exists(Path.Combine(headset.DirectoryPath, headset.DllFileName)),
                            "Missing-entry recovery changed a source package or the valid module's copied DLL.");
                    }
                    finally { CloseWindow(); }
                });
            }

            if (customModuleDll is not null)
            {
                Test("a missing selected custom DLL opens stopped with recovery actions and can be removed safely", () =>
                {
                    var profile = Path.Combine(root, "missing-current-custom-profile");
                    new AppSettings { AutoStart = true }.Save(profile);
                    var originalHash = HashFile(customModuleDll);
                    var store = new ModuleStore(profile);
                    var custom = store.Install(customModuleDll);
                    File.Delete(Path.Combine(custom.DirectoryPath, custom.DllFileName));
                    try
                    {
                        window = CreateWindow(profile, workerExecutable);
                        themes.AttachWindow(window); window.Show();
                        PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "missing selected custom DLL profile opened");
                        var reopened = new ModuleStore(profile);
                        Check(reopened.Current is { IsAvailable: false } missing && missing.Id == custom.Id,
                            "The unavailable custom selection was erased or prevented the library from reopening.");
                        Check(Field<object?>(window, "_worker") is null && !Field<bool>(window, "_running")
                            && !Named<Button>(window, "StartButton").IsEnabled && Named<Button>(window, "ImportButton").IsEnabled,
                            "A missing selected DLL auto-started or blocked the available import/recovery actions.");
                        Check((Named<TextBlock>(window, "StateTitle").Text + " " + Named<TextBlock>(window, "StateDetail").Text)
                            .Contains("missing", StringComparison.OrdinalIgnoreCase), "The selected missing DLL has no visible recovery explanation.");
                        CheckAvailableDownloads(DownloadOrder);
                        Named<RadioButton>(window, "ModulesNav").IsChecked = true; Pump();
                        Check(!CardButton(custom, "Unavailable").IsEnabled, "A missing selected custom DLL was offered for use.");
                        SavePreview(window, renderDirectory, "missing-current-custom-module.png");
                        InvokeCardButton(custom, "Remove module"); InvokeCardButton(custom, "Confirm remove module");
                        Check(new ModuleStore(profile).Current is null && new ModuleStore(profile).Installed.Count == 0
                            && Named<Button>(window, "ImportButton").IsEnabled,
                            "The missing custom entry could not be removed without losing import access.");
                        Check(File.Exists(customModuleDll) && HashFile(customModuleDll) == originalHash,
                            "Removing the missing copied entry changed its original SDK DLL.");
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

    private static MainWindow CreateWindow(string profile, string? workerExecutable = null, ModuleDownloader? moduleDownloader = null,
        Func<bool, Task>? configureSteamVrAutoLaunch = null)
    {
        var result = new MainWindow(profile, useDiscovery: false, workerExecutable: workerExecutable,
            moduleDownloader: moduleDownloader, configureSteamVrAutoLaunch: configureSteamVrAutoLaunch)
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

    private static void EnableKitty(MainWindow target)
    {
        var overlay = Named<HologramKittyOverlay>(target, "KittyOverlay");
        if (!overlay.IsActive) Named<Button>(target, "KittyToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => overlay.IsActive && overlay.IsVisible
            && overlay.IsAnimating == SystemParameters.ClientAreaAnimation, "kitty explicitly enabled for this session");
    }

    private static Point KittyBodyPoint(HologramKittyOverlay overlay)
    {
        var bounds = overlay.CatBounds;
        return new Point(bounds.X + bounds.Width * .5, bounds.Y + bounds.Height * .5);
    }

    private static void BeginKittyDrag(HologramKittyOverlay overlay)
    {
        var body = KittyBodyPoint(overlay);
        Check(overlay.BeginPickup(body), "The kitty did not accept a new pickup gesture.");
        overlay.MovePickup(body + new Vector(16, -8));
        Check(overlay.IsDragging, "Holding and moving the kitty did not pick it up.");
    }

    private static KittySurface KittyPerchSurface(HologramKittyOverlay overlay)
    {
        // Select an eligible edge from the app's actual panel inventory rather
        // than giving the overlay a synthetic surface that could mask layout bugs.
        foreach (var surface in overlay.GetSurfaces?.Invoke() ?? [])
            if (KittyPanelWorld.TryFindNearestPerch([surface], overlay.ActualWidth, overlay.ActualHeight,
                surface.Left + surface.Width * .5 - KittyPanelWorld.FeetX,
                surface.Top - KittyPanelWorld.FeetY, .1, out _)) return surface;
        throw new InvalidOperationException("No real visible panel top is available to the kitty.");
    }

    private static void DropKittyOn(HologramKittyOverlay overlay, KittySurface surface)
    {
        var body = KittyBodyPoint(overlay);
        var feet = overlay.FeetPosition;
        Check(overlay.BeginPickup(body), "The kitty did not accept pickup before a panel drop.");
        var release = body + (new Point(surface.Left + surface.Width * .5, surface.Top) - feet);
        // A cat already on this edge must still cross the pickup threshold.
        overlay.MovePickup(body + new Vector(18, -9));
        overlay.MovePickup(release);
        overlay.EndPickup(release);
        PumpUntil(() => overlay.IsPerched && overlay.CurrentPerchSurfaceId == surface.Id,
            "kitty finished its soft landing on the panel edge");
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

    private static void CheckAvailableDownloads(params ModuleKind[] expected)
    {
        // These profile fixtures install the original four providers. The three
        // additional official providers must remain available beside them.
        var installed = Field<ModuleStore>(window!, "_store").Installed.Where(release => release.IsAvailable).Select(release => release.Kind).ToHashSet();
        expected = expected.Concat(AdditionalDownloadOrder.Where(kind => !installed.Contains(kind))).Distinct().ToArray();
        var choices = Named<ItemsControl>(window!, "DownloadChoices");
        var actual = choices.Items.Cast<object>().Select(item => (ModuleKind)item.GetType().GetProperty("Kind")!.GetValue(item)!).ToArray();
        Check(actual.SequenceEqual(expected), "Installed providers were not hidden from downloads in the requested order: " + string.Join(", ", actual));
        Check(Named<TextBlock>(window!, "DownloadChoicesEmptyText").Visibility == (expected.Length == 0 ? Visibility.Visible : Visibility.Collapsed),
            "The empty-download message did not follow available provider choices.");
        var manual = Descendants(Named<StackPanel>(window!, "ModulesPage")).OfType<Button>()
            .Single(button => button.Content is string text && text.StartsWith("Import", StringComparison.Ordinal));
        Check(manual.IsEnabled && (Named<StackPanel>(window!, "ModulesPage").Visibility != Visibility.Visible || manual.IsVisible),
            "Filtering downloads hid or disabled manual DLL/ZIP import.");
    }

    private static void InvokeDownloadDirect(ModuleKind kind)
    {
        Task? download = null;
        window!.Dispatcher.BeginInvoke(new Action(() => download = (Task)Method(window, "DownloadModule").Invoke(window, [kind])!));
        PumpUntil(() => download?.IsCompleted == true, "backend duplicate download completed", TimeSpan.FromSeconds(30));
        download!.GetAwaiter().GetResult();
        PumpUntil(() => !Field<bool>(window, "_busy"), "backend duplicate download finished");
    }

    private static bool HasExistingVirtualDesktopIpc()
    {
        // Read-only handle probes: never create, map, modify, or signal user IPC.
        using var mapping = OpenFileMappingW(0x0004, false, "VirtualDesktop.BodyState");
        if (!mapping.IsInvalid || Marshal.GetLastWin32Error() == 5) return true;
        using var changed = OpenEventW(0x00100000, false, "VirtualDesktop.BodyStateEvent");
        return !changed.IsInvalid || Marshal.GetLastWin32Error() == 5;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenFileMappingW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle OpenEventW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

    private static string[] DownloadArchives(string profile)
    {
        var downloads = Path.Combine(profile, "downloads");
        return Directory.Exists(downloads) ? Directory.GetFiles(downloads, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)).ToArray() : [];
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

    private static void CheckThemeTextContrast()
    {
        // Check the surfaces used by small labels and actions as well as the page
        // heading. Bright cyan buttons need dark text; light-mode links need a
        // deeper accent than the decorative particles.
        (string text, string surface)[] pairs =
        [
            ("Ink", "CardBackground"), ("Ink", "InputBackground"),
            ("Muted", "CardBackground"), ("SubtleInk", "SoftBackground"),
            ("FaintInk", "CardBackground"), ("AccentInk", "CardBackground"),
            ("AccentInk", "InsetBackground"), ("ButtonInk", "AccentDark"),
            ("HeroInk", "HeroBackground"), ("HeroMuted", "HeroBackground"),
            ("HeroButtonInk", "HeroButtonBackground"),
            ("SidebarInk", "SidebarBackground"), ("SidebarMuted", "SidebarBackground"),
            ("SidebarFaint", "SidebarBackground"), ("HeroInk", "NavSelected"),
            ("NoticeInk", "NoticeBackground"), ("ErrorInk", "ErrorBackground"),
            ("DangerInk", "ErrorBackground")
        ];
        foreach (var (text, surface) in pairs)
        {
            var ink = ColorOf((Brush)Application.Current.FindResource(text));
            var background = ColorOf((Brush)Application.Current.FindResource(surface));
            var ratio = Contrast(ink, background);
            Check(ratio >= 4.5, $"{text} on {surface} has insufficient text contrast ({ratio:N2}:1).");
        }
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
    private static IEnumerable<Transform> TransformParts(Transform transform)
    {
        yield return transform;
        if (transform is TransformGroup group)
            foreach (var child in group.Children)
                foreach (var item in TransformParts(child)) yield return item;
    }
    private static bool NearlyIdentity(Matrix matrix) => Math.Abs(matrix.M11 - 1) < .001 && Math.Abs(matrix.M22 - 1) < .001
        && Math.Abs(matrix.M12) < .001 && Math.Abs(matrix.M21) < .001 && Math.Abs(matrix.OffsetX) < .01 && Math.Abs(matrix.OffsetY) < .01;
    private static bool EntranceActive(FrameworkElement element)
    {
        var states = typeof(Motion).GetField("States", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new InvalidOperationException("Missing motion state table.");
        object?[] arguments = [element, null];
        Check(states.GetType().GetMethod("TryGetValue")!.Invoke(states, arguments) is true,
            "The transitioned element has no motion state.");
        var state = arguments[1]!;
        return (bool)state.GetType().GetField("EntranceActive")!.GetValue(state)!;
    }

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
        window.RequestExit();
        PumpUntil(() => !window.IsLoaded && Field<bool>(window, "_allowClose"), "test window closed");
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

    private static IntPtr FindProcessWindow(int processId, string title)
    {
        var found = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var owner);
            if (owner != processId) return true;
            var text = new System.Text.StringBuilder(256);
            GetWindowText(handle, text, text.Capacity);
            if (text.ToString() != title) return true;
            found = handle;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static bool HasTrayIcon(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = window, Id = 1
        };
        return ShellNotifyIconGetRect(ref identifier, out _) == 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid Guid;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
    private static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect rectangle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
}
