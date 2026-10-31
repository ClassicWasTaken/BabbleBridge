// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using BabbleFrameBridge.App;
using System.Diagnostics;
using System.Text.Json;

if (args.Contains("--native-probe", StringComparer.Ordinal))
{
    // Explicit opt-in, read-only diagnostic: no Add/Remove/Set calls and no registration files.
    static string[] SteamVrProcesses() => new[] { "vrserver", "vrmonitor", "vrcompositor" }
        .SelectMany(name => Process.GetProcessesByName(name).Select(process =>
        {
            using (process) return process.ProcessName + ":" + process.Id;
        })).Order(StringComparer.Ordinal).ToArray();
    var before = SteamVrProcesses();
    Console.WriteLine("SteamVR processes before: " + (before.Length == 0 ? "none" : string.Join(", ", before)));
    Console.WriteLine("Installed runtime: " + OpenVrApplications.FindRuntimeDirectory());
    using (var native = new OpenVrApplications())
    {
        Console.WriteLine("Our app registered: " + native.IsInstalled(SteamVrIntegration.ApplicationKey));
        Console.WriteLine("Our auto launch flag: " + native.GetAutoLaunch(SteamVrIntegration.ApplicationKey));
    }
    await Task.Delay(500);
    var immediate = SteamVrProcesses();
    Console.WriteLine("Processes after settings disconnect: " + (immediate.Length == 0 ? "none" : string.Join(", ", immediate)));
    if (immediate.Except(before, StringComparer.Ordinal).Any(process => !process.StartsWith("vrserver:", StringComparison.Ordinal)))
        throw new Exception("A SteamVR presentation process appeared during the read-only native probe.");
    // Utility can start the headless settings server, which normally exits two seconds after disconnect.
    await Task.Delay(3000);
    var after = SteamVrProcesses();
    Console.WriteLine("SteamVR processes after: " + (after.Length == 0 ? "none" : string.Join(", ", after)));
    if (after.Except(before, StringComparer.Ordinal).Any())
        throw new Exception("A SteamVR process appeared during the read-only native probe.");
    Console.WriteLine("PASS: read-only OpenVR Utility ABI probe; no registration writes or VR session; settings service exited.");
    return;
}

int passed = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    passed++;
}
void ExpectFailure(Action action, string description)
{
    try { action(); }
    catch (InvalidOperationException) { passed++; return; }
    throw new Exception(description);
}

// Every service receives a fake backend and synthetic process probe. No test registers with SteamVR.
using (var profile = new TestProfile())
{
    profile.Service.ConfigureAutoLaunch(true);
    Check(profile.Backend.Installed && profile.Backend.Enabled, "Enabling should register and enable auto launch.");
    using var manifest = JsonDocument.Parse(File.ReadAllText(profile.Service.ManifestPath));
    var app = manifest.RootElement.GetProperty("applications")[0];
    Check(app.GetProperty("app_key").GetString() == SteamVrIntegration.ApplicationKey, "App key must remain stable.");
    Check(app.GetProperty("binary_path_windows").GetString() == profile.Executable, "Registration must target the actual installed executable.");
    Check(app.GetProperty("arguments").GetString() == "--steamvr", "SteamVR launches must be identifiable.");
    Check(app.GetProperty("is_dashboard_overlay").GetBoolean(), "SteamVR requires the overlay property for application auto launch.");
    Check(app.GetProperty("strings").GetProperty("en_us").GetProperty("name").GetString() == "Face Tracking Bridge", "Startup entry must use current display branding.");
    Check(profile.Backend.Disposals == 1, "Registration should release its native settings session.");
    profile.Service.ConfigureAutoLaunch(false);
    Check(!profile.Backend.Installed && !profile.Backend.Enabled && !File.Exists(profile.Service.ManifestPath), "Disabling must remove the startup entry and launch definition.");
    profile.Service.Unregister();
    Check(!profile.Backend.Installed, "Unregister must be idempotent.");
    Check(File.ReadAllText(profile.UnrelatedFile) == "preserve", "Cleanup must preserve unrelated user files.");
    Check(profile.Backend.Calls.All(call => call.Key == SteamVrIntegration.ApplicationKey), "Registration operations must never target another application.");
}

using (var profile = new TestProfile())
{
    profile.Backend.FailEnableOnce = true;
    ExpectFailure(() => profile.Service.ConfigureAutoLaunch(true), "A rejected startup flag must be reported.");
    Check(!profile.Backend.Installed && !profile.Backend.Enabled, "A failed first enable must restore an unregistered state.");
    Check(!File.Exists(profile.Service.ManifestPath), "A failed first enable must remove its manifest.");
    Check(!Directory.EnumerateFiles(profile.Root, "*.tmp", SearchOption.AllDirectories).Any(), "Failed registration must not leave a temporary launch file.");
}

using (var profile = new TestProfile())
{
    profile.Service.ConfigureAutoLaunch(true);
    var previous = File.ReadAllText(profile.Service.ManifestPath);
    profile.Backend.FailAddOnce = true;
    ExpectFailure(() => profile.Service.ConfigureAutoLaunch(true), "An update failure must be reported.");
    Check(profile.Backend.Installed && profile.Backend.Enabled, "A failed re-registration must preserve the previous enabled state.");
    Check(File.ReadAllText(profile.Service.ManifestPath) == previous, "A failed re-registration must restore the previous manifest exactly.");
    profile.Backend.FailRemoveOnce = true;
    ExpectFailure(() => profile.Service.ConfigureAutoLaunch(false), "A rejected removal must be reported.");
    Check(profile.Backend.Installed && profile.Backend.Enabled, "A failed disable must restore the previous enabled state.");
    Check(File.ReadAllText(profile.Service.ManifestPath) == previous, "A failed disable must preserve the launch definition.");
}

using (var profile = new TestProfile())
{
    File.Delete(profile.Executable);
    ExpectFailure(() => profile.Service.ConfigureAutoLaunch(true), "A missing executable must not be registered.");
    Check(profile.Backend.FactoryCalls == 0, "Executable validation should precede any SteamVR access.");
}

using (var profile = new TestProfile())
{
    Directory.CreateDirectory(Path.GetDirectoryName(profile.Service.ManifestPath)!);
    File.WriteAllText(profile.Service.ManifestPath, "{\"applications\":[{\"app_key\":\"someone.else\"}]}");
    var previous = File.ReadAllText(profile.Service.ManifestPath);
    ExpectFailure(() => profile.Service.ConfigureAutoLaunch(true), "A foreign manifest must never be overwritten.");
    ExpectFailure(profile.Service.Unregister, "A foreign manifest must never be deleted.");
    Check(File.ReadAllText(profile.Service.ManifestPath) == previous && profile.Backend.FactoryCalls == 0, "Foreign registration must stay untouched.");
}

using (var profile = new TestProfile())
{
    profile.Service.ConfigureAutoLaunch(true);
    var missingRuntime = new SteamVrIntegration(profile.Root, profile.Executable,
        () => throw new SteamVrUnavailableException("Synthetic missing runtime"), () => false);
    missingRuntime.Unregister();
    Check(!File.Exists(profile.Service.ManifestPath), "Uninstall must clean up its launch file even when SteamVR was uninstalled first.");
    missingRuntime.Unregister();
    Check(File.Exists(profile.UnrelatedFile), "Missing-runtime cleanup must remain safe and idempotent.");
    ExpectFailure(() => missingRuntime.ConfigureAutoLaunch(true), "A missing runtime cannot silently enable startup.");
    Check(!File.Exists(profile.Service.ManifestPath), "A missing runtime must not create a new launch file.");
}

using (var profile = new TestProfile())
{
    profile.Service.ConfigureAutoLaunch(true);
    profile.Backend.FailRemoveOnce = true;
    ExpectFailure(profile.Service.Unregister, "Uninstall should report a runtime cleanup error to its nonblocking CLI handler.");
    Check(!File.Exists(profile.Service.ManifestPath) && !profile.Backend.Enabled, "Uninstall should still remove its launch file after a runtime cleanup error.");
}

var neverStarted = new SteamVrSessionTracker();
Check(!SteamVrIntegration.ContainsVrSession([], 1), "An empty process snapshot cannot be a VR session.");
Check(!SteamVrIntegration.ContainsVrSession([new("vrserver", 1)], 1), "A headless Utility settings server cannot arm auto-close.");
Check(!SteamVrIntegration.ContainsVrSession([new("vrmonitor", 1)], 1), "A startup monitor without a running server cannot arm auto-close.");
Check(SteamVrIntegration.ContainsVrSession([new("vrserver", 1), new("vrmonitor", 1)], 1), "A server and monitor in our Windows session must count as running VR.");
Check(SteamVrIntegration.ContainsVrSession([new("vrserver", 1), new("vrcompositor", 1)], 1), "A server and compositor must count as running VR without the monitor.");
Check(!SteamVrIntegration.ContainsVrSession([new("vrserver", 2), new("vrmonitor", 2)], 1), "Another Windows user's session must not arm auto-close.");
Check(!SteamVrIntegration.ContainsVrSession([new("vrserver", 1), new("vrmonitor", 2)], 1), "Processes from different Windows sessions must not be combined.");
Check(!neverStarted.Observe(false, TimeSpan.Zero) && !neverStarted.Observe(false, TimeSpan.FromMinutes(5)), "An app opened before SteamVR has run must remain open.");
var session = new SteamVrSessionTracker();
Check(!session.Observe(true, TimeSpan.Zero), "A running session must not close the app.");
Check(!session.Observe(false, TimeSpan.FromSeconds(1)), "First absent sample must start a grace period.");
Check(!session.Observe(false, TimeSpan.FromSeconds(3.9)), "Brief server downtime must not close the app.");
Check(session.Observe(false, TimeSpan.FromSeconds(4)), "A confirmed ended session must close after the grace period.");
Check(!session.Observe(false, TimeSpan.FromSeconds(5)), "Session exit must be reported only once.");
var restart = new SteamVrSessionTracker();
restart.Observe(true, TimeSpan.Zero);
restart.Observe(false, TimeSpan.FromSeconds(1));
Check(!restart.Observe(true, TimeSpan.FromSeconds(3)), "A restarted server must reset its absence timer.");
restart.Observe(false, TimeSpan.FromSeconds(4));
Check(!restart.Observe(null, TimeSpan.FromSeconds(6)), "A failed process snapshot must not count as an exit.");
Check(!restart.Observe(false, TimeSpan.FromSeconds(8)), "A failed snapshot must reset consecutive absence.");
Check(restart.Observe(false, TimeSpan.FromSeconds(11)), "A subsequent known absence must still eventually report exit.");

using (var profile = new TestProfile())
using (var cancellation = new CancellationTokenSource())
{
    var callbacks = 0;
    cancellation.CancelAfter(30);
    try { await profile.Service.MonitorSessionAsync(() => callbacks++, cancellation.Token); }
    catch (OperationCanceledException) { }
    Check(callbacks == 0, "Canceling the observer must not close the app.");
    Check(profile.Backend.FactoryCalls == 0, "Session observation must never initialize or register OpenVR.");
}

Console.WriteLine($"PASS: {passed} SteamVR lifecycle checks (fake runtime only).");

sealed class TestProfile : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "FaceTrackingBridge-SteamVR-tests-" + Guid.NewGuid().ToString("N"));
    public string Executable { get; }
    public string UnrelatedFile { get; }
    public FakeApplications Backend { get; } = new();
    public SteamVrIntegration Service { get; }

    public TestProfile()
    {
        Directory.CreateDirectory(Root);
        Executable = Path.Combine(Root, "Face Tracking Bridge.exe");
        UnrelatedFile = Path.Combine(Root, "keep-user-data.txt");
        File.WriteAllText(Executable, "Synthetic executable; never launched.");
        File.WriteAllText(UnrelatedFile, "preserve");
        Service = new SteamVrIntegration(Root, Executable, () => { Backend.FactoryCalls++; return Backend; }, () => false);
    }

    public void Dispose()
    {
        var parent = Path.GetFullPath(Path.GetTempPath());
        var target = Path.GetFullPath(Root);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("FaceTrackingBridge-SteamVR-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe synthetic profile cleanup path.");
        Directory.Delete(target, true);
    }
}

sealed class FakeApplications : ISteamVrApplications
{
    public bool Installed;
    public bool Enabled;
    public bool FailEnableOnce;
    public bool FailAddOnce;
    public bool FailRemoveOnce;
    public int FactoryCalls;
    public int Disposals;
    public List<(string Operation, string Key)> Calls { get; } = [];
    public bool IsInstalled(string key) { Calls.Add(("installed", key)); return Installed; }
    public bool GetAutoLaunch(string key) { Calls.Add(("get", key)); return Enabled; }
    public void SetAutoLaunch(string key, bool enabled)
    {
        Calls.Add(("set", key));
        if (enabled && FailEnableOnce) { FailEnableOnce = false; throw new InvalidOperationException("Synthetic enable failure"); }
        Enabled = enabled;
    }
    public void AddManifest(string path)
    {
        if (FailAddOnce) { FailAddOnce = false; throw new InvalidOperationException("Synthetic manifest registration failure"); }
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        Calls.Add(("add", manifest.RootElement.GetProperty("applications")[0].GetProperty("app_key").GetString()!));
        Installed = true;
    }
    public void RemoveManifest(string path)
    {
        Calls.Add(("remove", SteamVrIntegration.ApplicationKey));
        if (FailRemoveOnce) { FailRemoveOnce = false; throw new InvalidOperationException("Synthetic manifest removal failure"); }
        Installed = false;
    }
    public void Dispose() => Disposals++;
}
