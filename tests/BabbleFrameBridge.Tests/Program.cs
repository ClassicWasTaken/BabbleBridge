// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BabbleFrameBridge.Shared;

var settingsOnly = args.Contains("--settings-only", StringComparer.Ordinal);
var suppliedZip = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)) ?? "";
if (!settingsOnly && !File.Exists(suppliedZip))
{
    Console.Error.WriteLine("Pass a genuine VRCFaceTracking.Baballonia module ZIP, or use --settings-only for settings regressions.");
    return 2;
}
var fixtureEntries = settingsOnly ? new List<FixtureEntry>() : ReadZip(suppliedZip);
var tempParent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "work", "module-tests"));
Directory.CreateDirectory(tempParent);
var root = Path.Combine(tempParent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;

try
{
    if (!settingsOnly)
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
        Test("Babble input profiles have isolated configuration and remember the selected module", () =>
        {
            var dataRoot = Path.Combine(root, "library");
            var store = new ModuleStore(dataRoot);
            var headset = store.Prepare(suppliedZip);
            Check(headset.Kind == ModuleKind.BabbleHeadsetRuntime && headset.SupportsHeadset, "Runtime capability was not detected.");
            var desktop = store.CreateCompanion(headset);
            Check(desktop.Kind == ModuleKind.BaballoniaDesktop && desktop.DirectoryPath != headset.DirectoryPath, "Input profiles share mutable configuration.");
            store.Register(headset); store.Register(desktop); store.Activate(desktop);
            File.WriteAllText(Path.Combine(desktop.DirectoryPath, "profile-test.txt"), "desktop");
            Check(!File.Exists(Path.Combine(headset.DirectoryPath, "profile-test.txt")), "Desktop mutated the headset profile.");
            store = new ModuleStore(dataRoot);
            Check(store.Installed.Count == 2 && store.Current == desktop, "Library/selection did not persist.");
            store.Activate(headset);
            Check(store.Previous == desktop && store.Rollback() == desktop, "Hot swap lost the previous selection.");
        });
        Test("legacy selected desktop profile migrates without changing its ports", () =>
        {
            var dataRoot = Path.Combine(root, "legacy-library");
            var store = new ModuleStore(dataRoot);
            var release = store.Install(suppliedZip);
            var statePath = Path.Combine(dataRoot, "modules", "module-state.json");
            File.WriteAllText(statePath, JsonSerializer.Serialize(new { Schema = 1, Current = release.Id, Previous = (string?)null }));
            store = new ModuleStore(dataRoot); store.MigrateLegacyProfiles(TrackingInputSource.BaballoniaDesktop);
            Check(store.Current?.Id == release.Id && store.Current.Kind == ModuleKind.BaballoniaDesktop, "Migration reset the desktop selection.");
            Check(store.Installed.Count == 2, "Migration did not add a separate headset profile.");
            Check(new ModuleStore(dataRoot).Current?.Kind == ModuleKind.BaballoniaDesktop, "Migrated selection did not persist.");
        });
        Test("removing an inactive imported profile preserves active and previous selections", () =>
        {
            var store = NewStore("remove-inactive");
            var inactive = store.Install(suppliedZip);
            var previous = store.Install(suppliedZip);
            var current = store.Install(suppliedZip);
            store.Remove(inactive);
            Check(store.Current == current && store.Previous == previous && store.Installed.Count == 2, "Inactive removal changed another selection.");
            Check(!Directory.Exists(Path.GetDirectoryName(inactive.DirectoryPath)!), "Removed managed payload remains in releases.");
        });
        Test("a missing inactive DLL does not block the selected module or its library", () =>
        {
            var dataRoot = Path.Combine(root, "missing-inactive-dll");
            var store = new ModuleStore(dataRoot);
            var missing = store.Install(suppliedZip);
            var previous = store.Install(suppliedZip);
            var current = store.Install(suppliedZip);
            File.Delete(Path.Combine(missing.DirectoryPath, missing.DllFileName));
            store = new ModuleStore(dataRoot);
            Check(store.Current == current && store.Previous == previous && store.Installed.Count == 3, "A missing inactive DLL blocked or discarded the valid library.");
            var unavailable = store.Installed.Single(item => item.Id == missing.Id);
            Check(!unavailable.IsAvailable && current.IsAvailable && previous.IsAvailable, "Availability does not reflect the actual entry DLLs.");
            Check(!JsonSerializer.Serialize(unavailable).Contains("IsAvailable"), "Computed availability was persisted as stale metadata.");
            ExpectFailure(() => store.Activate(unavailable), "missing-module-activation");
            Check(store.Current == current && store.Previous == previous, "Rejected missing-module activation changed selection.");
            store.Remove(unavailable);
            store = new ModuleStore(dataRoot);
            Check(store.Current == current && store.Installed.Count == 2 && store.Installed.All(item => item.Id != missing.Id), "Unavailable-profile removal did not persist.");
        });
        Test("a missing selected DLL remains visible and becomes available after repair", () =>
        {
            var dataRoot = Path.Combine(root, "missing-active-dll");
            var store = new ModuleStore(dataRoot);
            var current = store.Install(suppliedZip);
            var path = Path.Combine(current.DirectoryPath, current.DllFileName);
            var original = File.ReadAllBytes(path);
            File.Delete(path);
            store = new ModuleStore(dataRoot);
            var unavailable = store.Current!;
            Check(unavailable.Id == current.Id && !unavailable.IsAvailable && store.Installed.Single().Id == current.Id, "A missing selected DLL prevented the library from loading.");
            ExpectFailure(() => store.Activate(unavailable), "missing-selected-module-activation");
            File.WriteAllBytes(path, original);
            Check(unavailable.IsAvailable && store.Current!.IsAvailable, "Availability remained stale after repairing the DLL.");
            store.Activate(unavailable);
            Check(store.Current == unavailable, "A repaired profile could not be activated.");
        });
        Test("legacy profile migration keeps unavailable modules visible without probing missing DLLs", () =>
        {
            var dataRoot = Path.Combine(root, "missing-legacy-dll");
            var store = new ModuleStore(dataRoot);
            var current = store.Install(suppliedZip);
            File.Delete(Path.Combine(current.DirectoryPath, current.DllFileName));
            File.WriteAllText(Path.Combine(dataRoot, "modules", "module-state.json"), JsonSerializer.Serialize(new { Schema = 1, Current = current.Id, Previous = (string?)null }));
            store = new ModuleStore(dataRoot);
            store.MigrateLegacyProfiles(TrackingInputSource.BaballoniaDesktop);
            Check(store.Current?.Id == current.Id && store.Current.Kind == ModuleKind.BaballoniaDesktop
                && !store.Current.IsAvailable && store.Installed.Count == 1, "Legacy migration discarded an unavailable profile or created a broken companion.");
            Check(new ModuleStore(dataRoot).Current?.Id == current.Id, "Migrated unavailable profile could not reopen.");
        });
        Test("removing the previous profile clears only the rollback reference", () =>
        {
            var store = NewStore("remove-previous");
            var previous = store.Install(suppliedZip);
            var current = store.Install(suppliedZip);
            store.Remove(previous);
            Check(store.Current == current && store.Previous is null && store.Installed.Single() == current, "Previous removal changed the current profile.");
            ExpectFailure(() => store.Rollback(), "removed-rollback-target");
        });
        Test("removing the selected profile leaves selection empty and preserves its companion", () =>
        {
            var dataRoot = Path.Combine(root, "remove-companion");
            var store = new ModuleStore(dataRoot);
            var headset = store.Prepare(suppliedZip);
            var desktop = store.CreateCompanion(headset);
            store.Register(headset); store.Register(desktop); store.Activate(desktop); store.Activate(headset);
            var desktopDll = Path.Combine(desktop.DirectoryPath, desktop.DllFileName);
            var retainedBytes = File.ReadAllBytes(desktopDll);
            var zipBytes = File.ReadAllBytes(suppliedZip);
            store.Remove(headset);
            Check(store.Current is null && store.Previous == desktop && store.Installed.Single() == desktop, "Active removal selected another profile or removed its companion.");
            Check(File.ReadAllBytes(desktopDll).SequenceEqual(retainedBytes), "Companion payload changed.");
            Check(File.ReadAllBytes(suppliedZip).SequenceEqual(zipBytes), "The user's original ZIP was changed.");
            store = new ModuleStore(dataRoot);
            Check(store.Current is null && store.Previous == desktop && store.Installed.Single() == desktop, "Removal did not survive reopening.");
        });
        Test("removing the final profile persists an empty library without backup resurrection", () =>
        {
            var dataRoot = Path.Combine(root, "remove-last");
            var store = new ModuleStore(dataRoot);
            var release = store.Install(suppliedZip);
            store.Remove(release);
            Check(store.Current is null && store.Previous is null && store.Installed.Count == 0, "Removing the last profile left a reference.");
            var statePath = Path.Combine(dataRoot, "modules", "module-state.json");
            Check(!File.ReadAllText(statePath + ".backup").Contains(release.Id), "Backup still references the removed profile.");
            File.WriteAllText(statePath, "broken");
            store = new ModuleStore(dataRoot);
            Check(store.Current is null && store.Previous is null && store.Installed.Count == 0, "Backup recovery resurrected a removed profile.");
        });
        Test("removal rejects foreign, tampered and unregistered releases before changing files", () =>
        {
            var store = NewStore("remove-validation");
            var release = store.Install(suppliedZip);
            var other = NewStore("remove-foreign");
            ExpectFailure(() => other.Remove(release), "foreign-removal");
            ExpectFailure(() => store.Remove(release with { DirectoryPath = root }), "tampered-removal");
            var staged = store.Prepare(suppliedZip);
            ExpectFailure(() => store.Remove(staged), "unregistered-removal");
            Check(store.Current == release && store.Installed.Single() == release, "Rejected removal changed the library.");
            Check(File.Exists(Path.Combine(release.DirectoryPath, release.DllFileName)) && File.Exists(Path.Combine(staged.DirectoryPath, staged.DllFileName)), "Rejected removal deleted payload files.");
        });
        Test("failed removal state commits restore the payload and original state backup", () =>
        {
            var dataRoot = Path.Combine(root, "remove-commit-failure");
            var store = new ModuleStore(dataRoot);
            var previous = store.Install(suppliedZip);
            var current = store.Install(suppliedZip);
            var statePath = Path.Combine(dataRoot, "modules", "module-state.json");
            var primary = File.ReadAllBytes(statePath);
            var backup = File.ReadAllBytes(statePath + ".backup");
            foreach (var lockedPath in new[] { statePath, statePath + ".backup" })
            {
                using (var held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ExpectFailure(() => store.Remove(current), "locked-state-commit");
                Check(store.Current == current && store.Previous == previous && store.Installed.Count == 2, "A failed removal changed selection.");
                Check(File.Exists(Path.Combine(current.DirectoryPath, current.DllFileName)), "A failed commit lost its payload.");
                Check(File.ReadAllBytes(statePath).SequenceEqual(primary) && File.ReadAllBytes(statePath + ".backup").SequenceEqual(backup), "A failed removal changed persisted state.");
                Check(!Directory.EnumerateDirectories(Path.Combine(dataRoot, "modules"), ".removed-*").Any(), "A failed commit left a tombstone.");
            }
            Check(new ModuleStore(dataRoot).Current == current, "The restored library cannot be reopened.");
        });
        Test("locked removal cleanup is deferred and safely retried when the library reopens", () =>
        {
            var dataRoot = Path.Combine(root, "remove-deferred");
            var store = new ModuleStore(dataRoot);
            var release = store.Install(suppliedZip);
            var marker = Path.Combine(release.DirectoryPath, "locked-removal.txt");
            File.WriteAllText(marker, "retry cleanup");
            File.SetAttributes(marker, FileAttributes.ReadOnly);
            try
            {
                store.Remove(release);
                var tombstone = Path.Combine(dataRoot, "modules", ".removed-" + release.Id);
                Check(store.Installed.Count == 0 && Directory.Exists(tombstone), "Deferred cleanup did not commit the removal.");
                Check(File.Exists(Path.Combine(tombstone, "release.json")), "Deferred cleanup lost its ownership metadata.");
                File.SetAttributes(Path.Combine(tombstone, "payload", "locked-removal.txt"), FileAttributes.Normal);
                Check(new ModuleStore(dataRoot).Installed.Count == 0 && !Directory.Exists(tombstone), "Deferred cleanup was not completed on reopen.");
            }
            finally
            {
                foreach (var candidate in Directory.EnumerateFiles(dataRoot, "locked-removal.txt", SearchOption.AllDirectories))
                    File.SetAttributes(candidate, FileAttributes.Normal);
            }
        });
        Test("interrupted removal recovers the sanitized backup and finishes owned cleanup", () =>
        {
            var dataRoot = Path.Combine(root, "remove-interrupted");
            var store = new ModuleStore(dataRoot);
            var retained = store.Install(suppliedZip);
            var removed = store.Install(suppliedZip);
            var modules = Path.Combine(dataRoot, "modules");
            var statePath = Path.Combine(modules, "module-state.json");
            File.WriteAllText(statePath + ".backup", JsonSerializer.Serialize(new { Schema = 2, Current = (string?)null, Previous = retained.Id, Installed = new[] { retained.Id } }));
            Directory.Move(Path.Combine(modules, "releases", removed.Id), Path.Combine(modules, ".removed-" + removed.Id));
            store = new ModuleStore(dataRoot);
            Check(store.Current is null && store.Installed.Single() == retained, "Interrupted removal resurrected its target or lost another profile.");
            Check(!Directory.EnumerateDirectories(modules, ".removed-*").Any(), "Interrupted removal payload was not cleaned.");
            Check(!File.ReadAllText(statePath).Contains(removed.Id), "Recovery did not repair the interrupted primary state.");
        });
        Test("removal refuses payload junctions and preserves the external target", () =>
        {
            var store = NewStore("remove-junction");
            var release = store.Install(suppliedZip);
            var target = Path.Combine(root, "foreign-junction-target");
            Directory.CreateDirectory(target);
            var sentinel = Path.Combine(target, "keep.txt");
            File.WriteAllText(sentinel, "keep external data");
            var link = Path.Combine(release.DirectoryPath, "foreign-link");
            CreateDirectoryLink(link, target, root);
            try
            {
                ExpectFailure(() => store.Remove(release), "payload-junction-removal");
                Check(store.Current == release && store.Installed.Single() == release, "Rejected junction removal changed the library.");
                Check(File.ReadAllText(sentinel) == "keep external data", "Removal followed a junction into external data.");
            }
            finally { Directory.Delete(link); }
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
        Test("known profiles written before assembly identity metadata remain readable", () =>
        {
            var dataRoot = Path.Combine(root, "legacy-assembly-identity");
            var store = new ModuleStore(dataRoot);
            var release = store.Install(suppliedZip);
            var path = Path.Combine(dataRoot, "modules", "releases", release.Id, "release.json");
            var metadata = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path))!;
            metadata.Remove("AssemblyName");
            File.WriteAllText(path, JsonSerializer.Serialize(metadata));
            store = new ModuleStore(dataRoot);
            var legacy = store.Current!;
            Check(legacy.Id == release.Id && legacy.AssemblyName is null, "An older profile did not reopen with optional assembly metadata.");
            store.Activate(legacy);
            Check(store.Current == legacy, "Known identity was not derived from its legacy catalog entry.");
        });
        Test("manifest-free SDK packages identify their tracking entry among managed dependencies", () =>
        {
            var zip = Path.Combine(root, "known-sdk-no-manifest.zip");
            WriteZip(zip, fixtureEntries.Where(entry => entry.Name != "module.json").Concat(new[] {
                new FixtureEntry("ManagedDependency.dll", File.ReadAllBytes(typeof(ModuleStore).Assembly.Location)),
                new FixtureEntry("NativeDependency.dll", [0x4d, 0x5a, 1]) }));
            var release = NewStore("known-sdk-no-manifest").Prepare(zip);
            Check(release.Kind == ModuleKind.BabbleHeadsetRuntime && release.SupportsHeadset
                && release.AssemblyName == "VRCFaceTracking.Baballonia", "Tracking inheritance metadata did not identify the known module.");
        });
        Test("custom manifests cannot disguise a known module assembly under another identity", () =>
        {
            var dll = fixtureEntries.Single(entry => entry.Name == ModuleStore.BaballoniaDll);
            var zip = Path.Combine(root, "forged-known-custom.zip");
            WriteZip(zip, new[] { new FixtureEntry("module.json", JsonSerializer.SerializeToUtf8Bytes(new {
                ModuleId = Guid.NewGuid().ToString(), DllFileName = "Community.dll", Version = "1.0.0", ModuleName = "Community" })),
                dll with { Name = "Community.dll" } });
            ExpectFailure(() => NewStore("forged-known-custom").Prepare(zip), "known-assembly-disguised-as-custom");
        });
        Test("semantically invalid saved state recovers its valid atomic backup", () =>
        {
            var dataRoot = Path.Combine(root, "semantic-recovery");
            var store = new ModuleStore(dataRoot);
            var first = store.Install(suppliedZip);
            store.Install(Package("semantic-next", entries => SetManifest(entries, "Version", "4.0.3")));
            var path = Path.Combine(dataRoot, "modules", "module-state.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { Schema = 999, Current = first.Id, Previous = (string?)null }));
            Check(new ModuleStore(dataRoot).Current == first, "An unsupported state schema skipped its valid backup.");
            File.WriteAllText(path, JsonSerializer.Serialize(new { Schema = 2, Current = "not-a-release-id", Previous = (string?)null }));
            Check(new ModuleStore(dataRoot).Current == first, "An invalid release ID skipped its valid backup.");
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
    }
    Test("a single custom DLL stages only its selected file and derives stable metadata", () =>
    {
        var sourceDirectory = Path.Combine(root, "bare-source");
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(sourceDirectory, "MyTracking.dll");
        var original = File.ReadAllBytes(typeof(ModuleStore).Assembly.Location);
        File.WriteAllBytes(source, original);
        File.WriteAllBytes(Path.Combine(sourceDirectory, "UnrelatedDownloaded.dll"), [1, 2, 3]);
        var store = NewStore("bare-custom");
        var first = store.Prepare(source);
        var second = store.Prepare(source);
        Check(first.Kind == ModuleKind.Custom && first.AssemblyName == typeof(ModuleStore).Assembly.GetName().Name, "Custom assembly identity was not recorded.");
        Check(first.ModuleName == first.AssemblyName && first.Version == typeof(ModuleStore).Assembly.GetName().Version!.ToString(), "Missing manifest metadata did not use the assembly.");
        Check(Guid.TryParse(first.ModuleId, out _) && first.ModuleId == second.ModuleId, "Bare DLL imports did not derive a stable module ID.");
        Check(Directory.EnumerateFiles(first.DirectoryPath).Count() == 1 && !File.Exists(Path.Combine(first.DirectoryPath, "UnrelatedDownloaded.dll")), "Import copied unrelated neighboring DLLs.");
        Check(File.ReadAllBytes(source).SequenceEqual(original), "Import changed the selected original DLL.");
        store.Register(first); store.Activate(first);
        Check(store.Current == first, "Custom assembly identity could not be activated.");
    });
    Test("ZIPs without manifests can contain one managed entry with native dependencies", () =>
    {
        var zip = Path.Combine(root, "custom-no-manifest.zip");
        WriteZip(zip, new[] {
            new FixtureEntry("module/MyTracking.dll", File.ReadAllBytes(typeof(ModuleStore).Assembly.Location)),
            new FixtureEntry("module/native-helper.dll", [0x4d, 0x5a, 1, 2, 3]),
            new FixtureEntry("module/runtime-data.dat", [4, 5, 6]) });
        var store = NewStore("custom-no-manifest");
        var release = store.Prepare(zip);
        Check(release.Kind == ModuleKind.Custom && release.DllFileName == "MyTracking.dll", "Manifest-free custom entry was not identified.");
        Check(File.Exists(Path.Combine(release.DirectoryPath, "native-helper.dll")), "Native dependencies were not preserved.");
    });
    Test("custom manifests preserve author identity and reject entry filename escapes", () =>
    {
        var moduleId = Guid.NewGuid().ToString();
        var manifest = new Dictionary<string, object> { ["ModuleId"] = moduleId, ["DllFileName"] = "MyTracking.dll",
            ["Version"] = "9.2.1-preview", ["ModuleName"] = "Community Tracker" };
        string BuildCustom(string name)
        {
            var zip = Path.Combine(root, name + ".zip");
            WriteZip(zip, new[] { new FixtureEntry("package/module.json", JsonSerializer.SerializeToUtf8Bytes(manifest)),
                new FixtureEntry("package/MyTracking.dll", File.ReadAllBytes(typeof(ModuleStore).Assembly.Location)) });
            return zip;
        }
        var store = NewStore("custom-manifest");
        var release = store.Prepare(BuildCustom("custom-authored"), ModuleKind.Custom);
        Check(release.Kind == ModuleKind.Custom && release.ModuleId == moduleId && release.Version == "9.2.1-preview" && release.ModuleName == "Community Tracker", "Custom manifest metadata was lost.");
        foreach (var (index, filename) in new[] { "../MyTracking.dll", "..\\MyTracking.dll", "C:\\MyTracking.dll", "nested/MyTracking.dll", "MyTracking.dll:payload", "CON.dll" }.Select((filename, index) => (index, filename)))
        {
            manifest["DllFileName"] = filename;
            ExpectFailure(() => store.Prepare(BuildCustom("custom-escape-" + index)), "custom-entry-escape");
        }
        Check(store.Current is null && store.Installed.Count == 0, "Staging or rejection activated a custom module.");
    });
    Test("ambiguous manifest-free ZIPs ask for an entry DLL or optional manifest", () =>
    {
        var zip = Path.Combine(root, "custom-ambiguous.zip");
        WriteZip(zip, new[] { new FixtureEntry("One.dll", File.ReadAllBytes(typeof(ModuleStore).Assembly.Location)),
            new FixtureEntry("Two.dll", File.ReadAllBytes(typeof(JsonSerializer).Assembly.Location)) });
        var store = NewStore("custom-ambiguous");
        try { store.Prepare(zip); throw new Exception("Ambiguous module ZIP was accepted."); }
        catch (InvalidDataException error)
        { Check(error.Message.Contains("multiple") && error.Message.Contains("DLL directly") && error.Message.Contains("module.json"), "Ambiguity error does not explain how to select the entry."); }
    });
    Test("custom selection and removal survive reopening and backup recovery", () =>
    {
        var dataRoot = Path.Combine(root, "custom-removal");
        var store = new ModuleStore(dataRoot);
        var release = store.Prepare(typeof(ModuleStore).Assembly.Location);
        store.Register(release); store.Activate(release);
        store = new ModuleStore(dataRoot);
        Check(store.Current == release && store.Installed.Single() == release, "Custom release could not reopen.");
        store.Remove(release);
        File.WriteAllText(Path.Combine(dataRoot, "modules", "module-state.json"), "broken");
        store = new ModuleStore(dataRoot);
        Check(store.Current is null && store.Installed.Count == 0, "Backup recovery resurrected a removed custom module.");
    });
    Test("a missing custom entry retains strict metadata and can be removed after reopening", () =>
    {
        var dataRoot = Path.Combine(root, "missing-custom-dll");
        var store = new ModuleStore(dataRoot);
        var current = store.Prepare(typeof(ModuleStore).Assembly.Location);
        store.Register(current); store.Activate(current);
        File.Delete(Path.Combine(current.DirectoryPath, current.DllFileName));
        store = new ModuleStore(dataRoot);
        Check(store.Current == current && !store.Current.IsAvailable && store.Installed.Count == 1, "A missing custom entry blocked the library.");
        ExpectFailure(() => store.Activate(current), "missing-custom-activation");
        store.Remove(current);
        File.WriteAllText(Path.Combine(dataRoot, "modules", "module-state.json"), "broken");
        Check(new ModuleStore(dataRoot).Installed.Count == 0, "Unavailable custom removal was undone by backup recovery.");
    });
    Test("custom SDK inheritance selects the entry while keeping its managed dependency", () =>
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "BabbleBridge.CustomModule.Fixture.dll");
        var dependency = Path.Combine(AppContext.BaseDirectory, "Fixtures", "BabbleBridge.CustomModule.Dependency.dll");
        var zip = Path.Combine(root, "real-custom-sdk.zip");
        WriteZip(zip, new[] { new FixtureEntry("package/BabbleBridge.CustomModule.Fixture.dll", File.ReadAllBytes(fixture)),
            new FixtureEntry("package/BabbleBridge.CustomModule.Dependency.dll", File.ReadAllBytes(dependency)),
            new FixtureEntry("package/native-helper.dll", [0x4d, 0x5a, 0]) });
        var store = NewStore("real-custom-sdk");
        var release = store.Prepare(zip);
        Check(release.Kind == ModuleKind.Custom && release.AssemblyName == "BabbleBridge.CustomModule.Fixture"
            && release.DllFileName == "BabbleBridge.CustomModule.Fixture.dll", "SDK inheritance metadata selected the dependency instead of the module.");
        Check(File.Exists(Path.Combine(release.DirectoryPath, "BabbleBridge.CustomModule.Dependency.dll")), "The managed module dependency was lost.");
        Check(store.Current is null && store.Installed.Count == 0, "Preparing the SDK fixture activated it.");
    });
    Test("a bare SDK module stages without copying neighboring dependencies", () =>
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "BabbleBridge.CustomModule.Fixture.dll");
        var store = NewStore("bare-sdk-fixture");
        var release = store.Prepare(fixture);
        Check(release.Kind == ModuleKind.Custom && release.AssemblyName == "BabbleBridge.CustomModule.Fixture", "Bare SDK module was not accepted.");
        Check(Directory.EnumerateFiles(release.DirectoryPath).Single() == Path.Combine(release.DirectoryPath, release.DllFileName), "Bare import copied its neighbor DLLs without being selected.");
    });
    Test("Virtual Desktop source archives point users to the official compiled module", () =>
    {
        var path = Path.Combine(root, "virtual-desktop-source.zip");
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ModuleId = "91a90618-b020-4064-8832-809b2ca2b3bc", DllFileName = "VirtualDesktop.FaceTracking.dll",
            ModuleName = "Virtual Desktop", Version = "1.5", DownloadUrl = "https://untrusted.example/ignore-this"
        });
        WriteZip(path, new[] { new FixtureEntry("VirtualDesktop.VRCFaceTracking-main/module.json", manifest),
            new FixtureEntry("VirtualDesktop.VRCFaceTracking-main/TrackingModule.cs", Encoding.UTF8.GetBytes("// source fixture")) });
        var store = NewStore("virtual-desktop-source");
        try { store.Prepare(path); throw new Exception("Source archive was accepted without a compiled DLL."); }
        catch (InvalidDataException error)
        {
            Check(error.Message.Contains("compiled module") && error.Message.Contains("Source code ZIP")
                && error.Message.Contains("https://download.vrdesktop.net/files/VirtualDesktop.VRCFaceTracking.zip"), "Missing actionable official compiled download link.");
            Check(!error.Message.Contains("untrusted.example"), "The error trusted a manifest-supplied URL.");
        }
        Check(store.Installed.Count == 0 && store.Current is null, "Rejected source archive changed the library.");
    });
    Test("provider transports have distinct automatic inputs", () =>
    {
        Check(ModuleCatalog.Get(ModuleKind.BabbleHeadsetRuntime).InputPort == 6767, "Wrong headset input.");
        Check(ModuleCatalog.Get(ModuleKind.BaballoniaDesktop).InputPort == 8888, "Wrong desktop input.");
        Check(ModuleCatalog.Get(ModuleKind.VirtualDesktop).InputPort == 0, "Virtual Desktop must not open a UDP input.");
        Check(ModuleCatalog.Get(ModuleKind.SteamLink).InputPort == 9015, "Wrong LinkFT input.");
        Check(ModuleCatalog.Identify(Guid.NewGuid().ToString(), "Other.dll").Kind == ModuleKind.Custom, "Unknown valid module identity was not accepted as custom.");
        ExpectFailure(() => ModuleCatalog.Identify("not-a-guid", "Other.dll"), "invalid-custom-identity");
        Check((int)ModuleKind.Custom == 4 && ModuleCatalog.Get(ModuleKind.Custom).InputPort == 0
            && ModuleCatalog.Get(ModuleKind.Custom).Connection == "Module-managed input", "Custom module transport did not preserve the enum/port contract.");
        ExpectFailure(() => ModuleCatalog.Get((ModuleKind)999), "unknown-kind");
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
        foreach (var invalidPort in new[] { 0, 65536, settings.ReceivePort })
        {
            settings.OutputPort = invalidPort;
            ExpectFailure(() => settings.Save(dataRoot), "invalid-port");
        }
        Check(AppSettings.Load(dataRoot).OutputPort == 19000, "Rejected settings overwrote the last valid settings.");
        settings.OutputPort = 19000;
        settings.OutputHost = "not-an-ip";
        ExpectFailure(settings.Validate, "invalid-address");
    });
    Test("tracking source is explicit, persists, and defaults safely for existing settings", () =>
    {
        var dataRoot = Path.Combine(root, "tracking-source");
        Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(dataRoot, "settings.json"), "{}");
        var settings = AppSettings.Load(dataRoot);
        Check(settings.InputSource == TrackingInputSource.HeadsetRuntime, "Existing settings changed their source.");
        settings.InputSource = TrackingInputSource.BaballoniaDesktop;
        settings.Save(dataRoot);
        Check(AppSettings.Load(dataRoot).InputSource == TrackingInputSource.BaballoniaDesktop, "Desktop source did not persist.");
        Check(File.ReadAllText(Path.Combine(dataRoot, "settings.json")).Contains("\"BaballoniaDesktop\""), "Source must have a readable stable JSON name.");
        settings.InputSource = (TrackingInputSource)123;
        ExpectFailure(settings.Validate, "invalid-tracking-source");
        Check(AppSettings.Load(dataRoot).InputSource == TrackingInputSource.BaballoniaDesktop, "Rejected source overwrote valid settings.");
    });
    Test("corrupt primary settings recover their valid backup without replacing that backup", () =>
    {
        var data = Path.Combine(root, "settings-json-recovery");
        var saved = new AppSettings { OutputPort = 19000, ReceivePort = 19001, AutoStart = true };
        saved.Save(data);
        saved.OutputPort = 19002; saved.Save(data);
        var path = Path.Combine(data, "settings.json");
        var backup = File.ReadAllBytes(path + ".backup");
        File.WriteAllText(path, "{broken-primary");
        var recovered = AppSettings.Load(data);
        Check(recovered.OutputPort == 19000 && recovered.ReceivePort == 19001 && recovered.AutoStart, "The valid settings backup was not recovered.");
        Check(File.ReadAllBytes(path + ".backup").SequenceEqual(backup), "Repair replaced the valid backup with corrupt settings.");
        Check(AppSettings.Load(data).OutputPort == 19000, "The repaired primary cannot be reopened.");
        Check(File.ReadAllText(Directory.EnumerateFiles(data, "settings.json.corrupt-*").Single()) == "{broken-primary", "The invalid original was not retained.");
    });
    Test("semantically invalid settings recover valid saved connection preferences", () =>
    {
        var data = Path.Combine(root, "settings-validation-recovery");
        var saved = new AppSettings { OutputPort = 19100, ReceivePort = 19101, InputSource = TrackingInputSource.BaballoniaDesktop };
        saved.Save(data); saved.OutputPort = 19102; saved.Save(data);
        var path = Path.Combine(data, "settings.json");
        File.WriteAllText(path, "{\"OutputPort\":9001,\"ReceivePort\":9001}");
        var recovered = AppSettings.Load(data);
        Check(recovered.OutputPort == 19100 && recovered.ReceivePort == 19101 && recovered.InputSource == TrackingInputSource.BaballoniaDesktop, "Validation failure did not recover saved preferences.");
    });
    Test("a missing primary settings file recovers its existing backup instead of defaults", () =>
    {
        var data = Path.Combine(root, "settings-missing-primary");
        var saved = new AppSettings { OutputPort = 19200 };
        saved.Save(data); saved.OutputPort = 19202; saved.Save(data);
        var path = Path.Combine(data, "settings.json");
        var backup = File.ReadAllBytes(path + ".backup");
        File.Delete(path);
        Check(AppSettings.Load(data).OutputPort == 19200 && File.Exists(path), "A missing primary caused a silent settings reset.");
        Check(File.ReadAllBytes(path + ".backup").SequenceEqual(backup), "Missing-primary recovery changed the valid backup.");
    });
    Test("unrecoverable saved settings fail clearly while retaining both original files", () =>
    {
        var data = Path.Combine(root, "settings-both-invalid");
        Directory.CreateDirectory(data);
        var path = Path.Combine(data, "settings.json");
        File.WriteAllText(path, "broken primary");
        File.WriteAllText(path + ".backup", "{\"OutputPort\":0}");
        foreach (var backupExists in new[] { true, false })
        {
            if (!backupExists) File.Delete(path + ".backup");
            try { AppSettings.Load(data); throw new Exception("Invalid saved settings silently reset to defaults."); }
            catch (InvalidDataException error) { Check(error.Message.Contains("preserved") && error.Message.Contains("settings.json"), "Startup recovery failure is not actionable."); }
            Check(File.ReadAllText(path) == "broken primary", "Unrecoverable primary settings changed.");
            if (backupExists) Check(File.ReadAllText(path + ".backup") == "{\"OutputPort\":0}", "An invalid backup was overwritten.");
            Check(!Directory.EnumerateFiles(data, "settings.json.corrupt-*").Any(), "Invalid settings were moved before a valid recovery candidate existed.");
        }
    });
    Test("failed settings repair preserves the corrupt primary and known-good backup", () =>
    {
        var data = Path.Combine(root, "settings-repair-locked");
        var saved = new AppSettings { OutputPort = 19300 };
        saved.Save(data); saved.OutputPort = 19302; saved.Save(data);
        var path = Path.Combine(data, "settings.json");
        File.WriteAllText(path, "broken locked primary");
        var backup = File.ReadAllBytes(path + ".backup");
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            ExpectFailure(() => AppSettings.Load(data), "locked-settings-repair");
        Check(File.ReadAllText(path) == "broken locked primary" && File.ReadAllBytes(path + ".backup").SequenceEqual(backup), "A failed repair damaged the saved files.");
        Check(AppSettings.Load(data).OutputPort == 19300, "Settings could not recover after the file lock was released.");
    });
    Test("settings recovery and saving refuse junction paths without modifying external files", () =>
    {
        var target = Path.Combine(root, "external-settings-profile");
        var saved = new AppSettings { OutputPort = 19400 };
        saved.Save(target); saved.OutputPort = 19402; saved.Save(target);
        var path = Path.Combine(target, "settings.json");
        File.WriteAllText(path, "broken external primary");
        var backup = File.ReadAllBytes(path + ".backup");
        var link = Path.Combine(root, "settings-profile-junction");
        CreateDirectoryLink(link, target, root);
        try
        {
            ExpectFailure(() => AppSettings.Load(link), "junction-settings-repair");
            ExpectFailure(() => saved.Save(link), "junction-settings-save");
            Check(File.ReadAllText(path) == "broken external primary" && File.ReadAllBytes(path + ".backup").SequenceEqual(backup), "Settings repair or saving followed the junction.");
            Check(!Directory.EnumerateFiles(target, "settings.json.corrupt-*").Any(), "Repair wrote an external corrupt-file archive through the junction.");
        }
        finally { Directory.Delete(link); }
    });
    Test("the old runtime source name migrates without resetting connection preferences", () =>
    {
        var dataRoot = Path.Combine(root, "source-migration");
        Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, "settings.json");
        File.WriteAllText(path, """
            {"InputSource":"SteamFrameRuntime","OutputHost":"192.168.1.20","OutputPort":19000,
             "ReceivePort":19001,"LinkPort":17677,"AutoStart":true}
            """);
        var settings = AppSettings.Load(dataRoot);
        Check(settings.InputSource == TrackingInputSource.HeadsetRuntime, "The old source name was not recognized.");
        Check(settings.OutputHost == "192.168.1.20" && settings.OutputPort == 19000
            && settings.ReceivePort == 19001 && settings.LinkPort == 17677 && settings.AutoStart,
            "Migrating the runtime source changed saved connection preferences.");
        settings.Save(dataRoot);
        using var canonical = JsonDocument.Parse(File.ReadAllText(path));
        Check(canonical.RootElement.GetProperty("InputSource").GetString() == "HeadsetRuntime",
            "Saving a migrated profile did not write the canonical runtime name.");
        Check(AppSettings.Load(dataRoot).InputSource == TrackingInputSource.HeadsetRuntime,
            "The migrated profile could not be reopened.");
    });
    Test("source JSON preserves supported legacy values and rejects unknown values", () =>
    {
        var dataRoot = Path.Combine(root, "source-values");
        Directory.CreateDirectory(dataRoot);
        var path = Path.Combine(dataRoot, "settings.json");
        foreach (var (json, expected) in new[]
        {
            ("0", TrackingInputSource.HeadsetRuntime), ("1", TrackingInputSource.BaballoniaDesktop),
            ("\"HeadsetRuntime\"", TrackingInputSource.HeadsetRuntime),
            ("\"BaballoniaDesktop\"", TrackingInputSource.BaballoniaDesktop),
            ("\"steamframeruntime\"", TrackingInputSource.HeadsetRuntime),
            ("\"0\"", TrackingInputSource.HeadsetRuntime), ("\"1\"", TrackingInputSource.BaballoniaDesktop)
        })
        {
            File.WriteAllText(path, "{\"InputSource\":" + json + "}");
            var settings = AppSettings.Load(dataRoot);
            Check(settings.InputSource == expected, "Source value was not preserved: " + json);
            settings.Save(dataRoot);
            using var canonical = JsonDocument.Parse(File.ReadAllText(path));
            Check(canonical.RootElement.GetProperty("InputSource").GetString() == expected.ToString(),
                "Saving did not canonicalize the source value: " + json);
        }
        // This case checks rejection when no valid recovery candidate exists;
        // valid-backup recovery is exercised separately above.
        File.Delete(path + ".backup");
        foreach (var json in new[] { "2", "-1", "1.5", "null", "true", "\"2\"", "\"unknown\"" })
        {
            File.WriteAllText(path, "{\"InputSource\":" + json + "}");
            ExpectFailure(() => AppSettings.Load(dataRoot), "invalid-source-" + json);
        }
        ExpectFailure(() => JsonSerializer.Serialize((TrackingInputSource)123), "invalid-source-serialization");
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
static void CreateDirectoryLink(string link, string target, string fixtureRoot)
{
    if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
    var script = Path.Combine(fixtureRoot, "create-test-junction-" + Guid.NewGuid().ToString("N") + ".ps1");
    File.WriteAllText(script, "param([string]$Link, [string]$Target)\n$ErrorActionPreference='Stop'\nNew-Item -ItemType Junction -Path $Link -Target $Target | Out-Null\n");
    var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true };
    foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Link", link, "-Target", target }) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    Check(process.WaitForExit(10000) && process.ExitCode == 0, "Could not create the isolated junction fixture.");
}
static void ExpectFailure(Action action, string context)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException) { return; }
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
