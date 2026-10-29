// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BabbleFrameBridge.Shared;

var suppliedZip = args.FirstOrDefault();
if (!File.Exists(suppliedZip))
{
    Console.Error.WriteLine("Pass the path to a genuine VRCFaceTracking.Baballonia module ZIP as the first argument.");
    return 2;
}
var fixtureEntries = ReadZip(suppliedZip);
var tempParent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "work", "module-tests"));
Directory.CreateDirectory(tempParent);
var root = Path.Combine(tempParent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;

try
{
    Test("new profiles remain empty until a module ZIP is imported", () =>
    {
        var dataRoot = Path.Combine(root, "empty-profile");
        var store = new ModuleStore(dataRoot);
        Check(store.Current is null && store.Previous is null, "A new profile auto-installed a module.");
        Check(!Directory.EnumerateFiles(dataRoot, "*.dll", SearchOption.AllDirectories).Any(), "A new profile contains a module DLL.");
        Check(new ModuleStore(dataRoot).Current is null, "Reopening an empty profile installed a module.");
    });
    Test("valid package stages without activation", () =>
    {
        var store = NewStore("staging");
        var staged = store.Prepare(suppliedZip);
        Check(store.Current is null, "Prepare changed the active release.");
        Check(staged.Version == "4.0.0", "Unexpected supplied module version.");
        Check(staged.Sha256.Length == 64 && File.Exists(Path.Combine(staged.DirectoryPath, staged.DllFileName)), "Incomplete staged release.");
        store.Activate(staged);
        Check(store.Current == staged, "Prepared release was not activated.");
        Check(store.Previous is null, "Unexpected previous release on first activation.");
    });
    Test("update, restart, rollback, and undo rollback retain both versions", () =>
    {
        var dataRoot = Path.Combine(root, "lifecycle");
        var store = new ModuleStore(dataRoot);
        var first = store.Install(suppliedZip);
        var nextZip = Package("next", entries => SetManifest(entries, "Version", "4.0.1"));
        var second = store.Install(nextZip);
        Check(second.Version == "4.0.1" && store.Previous == first, "Update lost the previous release.");
        store = new ModuleStore(dataRoot);
        Check(store.Current == second && store.Previous == first, "State did not survive restart.");
        Check(store.Rollback() == first && store.Current == first && store.Previous == second, "Rollback failed.");
        Check(store.Rollback() == second && store.Previous == first, "Undo rollback failed.");
        Check(File.Exists(Path.Combine(first.DirectoryPath, first.DllFileName)), "Old DLL was deleted.");
    });
    Test("wrapper directory packages resolve the module directory", () =>
    {
        var zip = Path.Combine(root, "wrapped.zip");
        WriteZip(zip, fixtureEntries.Select(e => e with { Name = "download/" + e.Name }));
        var store = NewStore("wrapped");
        var release = store.Install(zip);
        Check(Path.GetFileName(release.DirectoryPath) == "download", "Incorrect wrapped module path.");
        Check(File.Exists(Path.Combine(release.DirectoryPath, "module.json")), "Manifest not found in resolved directory.");
    });
    Test("rejected packages preserve the running and previous versions", () =>
    {
        var store = NewStore("rejections");
        var current = store.Install(suppliedZip);
        var badPackages = new Dictionary<string, Action<List<FixtureEntry>>>
        {
            ["traversal"] = entries => entries.Add(new("../escaped.txt", [1])),
            ["backslash-traversal"] = entries => entries.Add(new("..\\escaped.txt", [1])),
            ["absolute"] = entries => entries.Add(new("C:/escaped.txt", [1])),
            ["alternate-stream"] = entries => entries.Add(new("file.txt:payload", [1])),
            ["windows-device"] = entries => entries.Add(new("CON.txt", [1])),
            ["trailing-dot"] = entries => entries.Add(new("bad./payload", [1])),
            ["duplicate"] = entries => entries.Add(entries[0] with { Name = entries[0].Name.ToUpperInvariant() }),
            ["symlink"] = entries => entries.Add(new("linked", Encoding.UTF8.GetBytes("outside"), unchecked((int)0xA1FF0000))),
            ["wrong-module"] = entries => SetManifest(entries, "ModuleId", Guid.NewGuid().ToString()),
            ["wrong-dll"] = entries => SetManifest(entries, "DllFileName", "Other.dll"),
            ["missing-dll"] = entries => entries.RemoveAll(e => e.Name == ModuleStore.BaballoniaDll),
            ["non-managed-dll"] = entries => Replace(entries, ModuleStore.BaballoniaDll, [1, 2, 3]),
            ["other-assembly"] = entries => Replace(entries, ModuleStore.BaballoniaDll, File.ReadAllBytes(typeof(ModuleStore).Assembly.Location)),
            ["multiple-manifests"] = entries => entries.Add(new("extra/module.json", Encoding.UTF8.GetBytes("{}"))),
            ["invalid-json"] = entries => Replace(entries, "module.json", Encoding.UTF8.GetBytes("{not-json}")),
            ["oversized-manifest"] = entries => Replace(entries, "module.json", new byte[1024 * 1024 + 1]),
        };
        foreach (var (name, mutate) in badPackages)
        {
            var zip = Package(name, mutate);
            ExpectFailure(() => store.Install(zip), name);
            Check(store.Current == current && store.Previous is null, $"{name} changed active module state.");
        }
        Check(!File.Exists(Path.Combine(root, "escaped.txt")), "Traversal wrote outside the release directory.");
    });
    Test("prepared releases cannot be activated in another store", () =>
    {
        var a = NewStore("store-a");
        var b = NewStore("store-b");
        var release = a.Prepare(suppliedZip);
        ExpectFailure(() => b.Activate(release), "foreign-release");
        Check(b.Current is null, "Foreign release changed state.");
        ExpectFailure(() => a.Activate(release with { DirectoryPath = root }), "tampered-release");
        Check(a.Current is null, "Tampered release changed state.");
    });
    Test("corrupt state recovers the last atomically replaced manifest", () =>
    {
        var dataRoot = Path.Combine(root, "recovery");
        var store = new ModuleStore(dataRoot);
        var first = store.Install(suppliedZip);
        store.Install(Package("recovery-next", entries => SetManifest(entries, "Version", "4.0.2")));
        File.WriteAllText(Path.Combine(dataRoot, "modules", "module-state.json"), "broken");
        store = new ModuleStore(dataRoot);
        Check(store.Current == first, "State backup recovery did not restore the previous valid manifest.");
    });
    Test("settings persist and validation rejects conflicting listening ports", () =>
    {
        var dataRoot = Path.Combine(root, "settings");
        var settings = AppSettings.Load(dataRoot);
        Check(settings.OutputHost == "127.0.0.1" && settings.OutputPort == 9000 && settings.ReceivePort == 9001, "Unexpected defaults.");
        settings.OutputHost = "192.168.1.20";
        settings.OutputPort = 19000;
        settings.AutoStart = true;
        settings.Save(dataRoot);
        var reloaded = AppSettings.Load(dataRoot);
        Check(reloaded.OutputHost == settings.OutputHost && reloaded.OutputPort == 19000 && reloaded.AutoStart, "Settings did not persist.");
        foreach (var invalidPort in new[] { 0, 65536, 6767, 8888, settings.ReceivePort })
        {
            settings.OutputPort = invalidPort;
            ExpectFailure(() => settings.Save(dataRoot), "invalid-port");
        }
        Check(AppSettings.Load(dataRoot).OutputPort == 19000, "Rejected settings overwrote the last valid settings.");
        settings.OutputPort = 19000;
        settings.OutputHost = "not-an-ip";
        ExpectFailure(settings.Validate, "invalid-address");
    });
}
finally
{
    if (Path.GetFullPath(root).StartsWith(tempParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        Directory.Delete(root, recursive: true);
}
Console.WriteLine($"{passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
}
ModuleStore NewStore(string name) => new(Path.Combine(root, name));
string Package(string name, Action<List<FixtureEntry>> mutate)
{
    var entries = fixtureEntries.Select(e => e with { }).ToList();
    mutate(entries);
    var path = Path.Combine(root, name + ".zip");
    WriteZip(path, entries);
    return path;
}
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void ExpectFailure(Action action, string context)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException) { return; }
    throw new Exception("Expected rejection: " + context);
}
static List<FixtureEntry> ReadZip(string path)
{
    using var zip = ZipFile.OpenRead(path);
    return zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e =>
    {
        using var input = e.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return new FixtureEntry(e.FullName, output.ToArray(), e.ExternalAttributes);
    }).ToList();
}
static void WriteZip(string path, IEnumerable<FixtureEntry> entries)
{
    using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
    foreach (var entry in entries)
    {
        var target = zip.CreateEntry(entry.Name);
        target.ExternalAttributes = entry.Attributes;
        using var output = target.Open();
        output.Write(entry.Data);
    }
}
static void SetManifest(List<FixtureEntry> entries, string field, string value)
{
    var manifest = entries.Single(e => e.Name == "module.json");
    var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(manifest.Data)!;
    data[field] = JsonSerializer.SerializeToElement(value);
    Replace(entries, "module.json", JsonSerializer.SerializeToUtf8Bytes(data));
}
static void Replace(List<FixtureEntry> entries, string name, byte[] data)
{
    var index = entries.FindIndex(e => e.Name == name);
    entries[index] = entries[index] with { Data = data };
}
sealed record FixtureEntry(string Name, byte[] Data, int Attributes = 0);
