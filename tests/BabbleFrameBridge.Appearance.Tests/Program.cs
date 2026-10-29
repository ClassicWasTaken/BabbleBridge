// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.IO;
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
                Check(AppSettings.Load(emptyProfile).InputSource == TrackingInputSource.SteamFrameRuntime,
                    "First launch did not save Frame Runtime as the initial source.");
                Check(Named<RadioButton>(window, "FrameSourceButton").IsChecked == true
                    && Named<RadioButton>(window, "DesktopSourceButton").IsChecked == false,
                    "First launch did not select Frame Runtime in the interface.");
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

            Test("source selection previews its tracking port without changing the VRChat connection", () =>
            {
                var send = Named<TextBox>(window, "PortBox").Text;
                var receive = Named<TextBox>(window, "ReceiveBox").Text;
                Check(Named<TextBlock>(window, "TrackingInputPort").Text.Contains("6767"), "Frame input port was not shown.");
                Named<RadioButton>(window, "DesktopSourceButton").IsChecked = true;
                Pump();
                Check(Named<TextBlock>(window, "TrackingInputPort").Text.Contains("8888"), "Desktop selection did not show its input port.");
                Named<RadioButton>(window, "FrameSourceButton").IsChecked = true;
                Pump();
                Check(Named<TextBlock>(window, "TrackingInputPort").Text.Contains("6767"), "Returning to Frame did not restore its input port.");
                Check(Named<TextBox>(window, "PortBox").Text == send && Named<TextBox>(window, "ReceiveBox").Text == receive,
                    "Choosing a tracking source changed the separate VRChat send/receive ports.");
                Check(AppSettings.Load(emptyProfile).InputSource == TrackingInputSource.SteamFrameRuntime,
                    "Previewing a source saved it before Save connection.");
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

            Test("a later launch restores the saved source and connection preferences", () =>
            {
                Check(Named<RadioButton>(window, "FrameSourceButton").IsChecked == true,
                    "An existing/default profile changed away from Frame Runtime.");
                Named<RadioButton>(window, "DesktopSourceButton").IsChecked = true;
                Named<CheckBox>(window, "AutoStartBox").IsChecked = false;
                Named<TextBox>(window, "HostBox").Text = "127.0.0.2";
                Named<TextBox>(window, "PortBox").Text = "19000";
                Named<TextBox>(window, "ReceiveBox").Text = "19001";
                Method(window, "Save_Click").Invoke(window, [window, new RoutedEventArgs()]);
                PumpUntil(() => !Field<bool>(window, "_busy"), "desktop source saved");
                Check(AppSettings.Load(emptyProfile).InputSource == TrackingInputSource.BaballoniaDesktop,
                    "Save connection did not persist desktop input.");
                var noticeTimer = Field<DispatcherTimer>(window, "_noticeTimer");
                CloseWindow();
                Check(!noticeTimer.IsEnabled, "Closing the window left its notification timer running.");
                window = CreateWindow(emptyProfile);
                themes.AttachWindow(window);
                window.Show();
                PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "desktop profile reopened");
                Check(Named<RadioButton>(window, "DesktopSourceButton").IsChecked == true,
                    "Reopening did not restore desktop source selection.");
                Check(Named<TextBlock>(window, "TrackingInputPort").Text.Contains("8888"),
                    "Reopening did not restore the desktop input port display.");
                var restored = AppSettings.Load(emptyProfile);
                Check(restored.OutputHost == "127.0.0.2" && restored.OutputPort == 19000
                    && restored.ReceivePort == 19001 && !restored.AutoStart,
                    "Reopening changed other saved connection preferences.");
                Named<RadioButton>(window, "FrameSourceButton").IsChecked = true;
                Method(window, "Save_Click").Invoke(window, [window, new RoutedEventArgs()]);
                PumpUntil(() => !Field<bool>(window, "_busy"), "Frame source saved");
                CloseWindow();
                window = CreateWindow(emptyProfile);
                themes.AttachWindow(window);
                window.Show();
                PumpUntil(() => Field<bool>(window, "_initialized") && !Field<bool>(window, "_busy"), "Frame profile reopened");
                Check(Named<RadioButton>(window, "FrameSourceButton").IsChecked == true
                    && AppSettings.Load(emptyProfile).InputSource == TrackingInputSource.SteamFrameRuntime,
                    "A later launch did not restore the explicitly saved Frame source.");
                Check(Field<object?>(window, "_worker") is null, "Changing source launched a worker without a module.");
                Method(window, "SelectPage").Invoke(window, ["settings"]);
                Named<StackPanel>(window, "SettingsPage").Opacity = 1;
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
                    Check(Named<TextBlock>(window, "TrackingInputPort").Text.Contains("23456"), "Custom Frame Link port was not shown.");
                    PumpUntil(() => Named<StackPanel>(window, "OverviewPage").Opacity >= .999,
                        "module overview entrance finished before preview capture");
                    SavePreview(window, renderDirectory, "module-ready.png");
                    Named<RadioButton>(window, "DesktopSourceButton").IsChecked = true;
                    Pump();
                    Check(Named<TextBlock>(window, "TrackingInputPort").Text.Contains("42424"), "Custom module desktop input port was not shown.");
                    Check(JsonNode.Parse(File.ReadAllText(configPath))!["Port"]!.GetValue<int>() == 42424,
                        "Previewing the desktop source overwrote its configured module port.");
                    CloseWindow();
                });
            }

            if (published is not null)
            {
                Test("the published app contains no bundled Baballonia module archive or DLL", () =>
                {
                    Check(Directory.Exists(published), "Published app directory does not exist.");
                    var payload = Directory.EnumerateFiles(published, "*", SearchOption.AllDirectories)
                        .Where(path => Path.GetFileName(path).Equals("VRCFaceTracking.Baballonia.dll", StringComparison.OrdinalIgnoreCase)
                            || (Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)
                                && Path.GetFileName(path).Contains("Baballonia", StringComparison.OrdinalIgnoreCase)))
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

    private static MainWindow CreateWindow(string profile)
    {
        var result = new MainWindow(profile)
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000
        };
        return result;
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
