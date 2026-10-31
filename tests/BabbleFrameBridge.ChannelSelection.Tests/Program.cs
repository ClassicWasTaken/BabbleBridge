// Copyright (c) 2026 Face Tracking Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using BabbleFrameBridge.Shared;

var parent = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "work", "channel-selection-tests"));
var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var fixture = typeof(ModuleStore).Assembly.Location;
var checks = 0;
var tests = 0;
try
{
    Test("registration and fresh profiles never select roles", () =>
    {
        var (profile, store, eye, face, _) = Library("empty");
        Check(store.Current is null && store.Previous is null, "Registration selected a module.");
        Check(!store.Selection.IsComplete && store.Selection.Modules.Count == 0, "Registration selected tracking roles.");
        Check(store.EyeModule is null && store.FaceModule is null && store.PreviousEyeModule is null && store.PreviousFaceModule is null,
            "An empty library has a role assignment.");
        var reopened = new ModuleStore(profile);
        Check(reopened.Installed.Count == 3 && reopened.Selection.Modules.Count == 0, "Empty role selection did not survive reload.");
        Check(File.Exists(Path.Combine(eye.DirectoryPath, eye.DllFileName)) && File.Exists(Path.Combine(face.DirectoryPath, face.DllFileName)),
            "Registration lost module payloads.");
    });

    Test("separate eye and face roles persist as one atomic selection", () =>
    {
        var (profile, store, eye, face, _) = Library("pair");
        store.SelectChannels(eye, face);
        CheckPair(store, eye, face);
        Check(store.Current == eye && store.Selection.PrimaryModule == eye, "Current did not retain the primary eye module.");
        Check(store.Selection.Modules.Count == 2 && store.Selection.IsSelected(face.Id), "The face role is absent from the active modules.");
        Check(store.Previous is null && !store.PreviousSelection.IsComplete, "First selection invented a previous selection.");
        CheckPair(new ModuleStore(profile), eye, face);
        using var state = JsonDocument.Parse(File.ReadAllText(StatePath(profile)));
        Check(state.RootElement.GetProperty("Schema").GetInt32() == 3, "Pair state did not use schema 3.");
        Check(state.RootElement.GetProperty("EyeModuleId").GetString() == eye.Id
            && state.RootElement.GetProperty("FaceModuleId").GetString() == face.Id, "Both role IDs were not persisted.");
    });

    Test("one shared module represents one module instance", () =>
    {
        var (profile, store, eye, _, _) = Library("shared");
        store.SelectChannels(eye, eye);
        CheckPair(store, eye, eye);
        Check(store.Selection.Modules.Count == 1, "Shared roles would create duplicate workers or module instances.");
        Check(new ModuleStore(profile).Selection.Modules.Count == 1, "Shared module deduplication did not survive reload.");
        var before = StateBytes(profile);
        store.SelectChannels(eye, eye);
        Check(before.SequenceEqual(StateBytes(profile)) && store.Previous is null, "Selecting an unchanged pair rewrote rollback history.");
    });

    Test("pair rollback and undo preserve each role", () =>
    {
        var (profile, store, eye, face, _) = Library("rollback");
        store.SelectChannels(eye, face);
        store.SelectChannels(face, eye);
        Check(store.PreviousEyeModule == eye && store.PreviousFaceModule == face, "Previous role assignments were flattened.");
        store = new ModuleStore(profile);
        var restored = store.RestorePreviousChannels();
        Check(restored.EyeModule == eye && restored.FaceModule == face, "Restore returned the wrong pair.");
        CheckPair(store, eye, face);
        Check(store.PreviousSelection.EyeModule == face && store.PreviousSelection.FaceModule == eye, "Restore lost its undo pair.");
        Check(store.Rollback() == face, "Legacy Rollback did not return the restored primary module.");
        CheckPair(new ModuleStore(profile), face, eye);
    });

    Test("legacy activation sets both roles even when the primary is unchanged", () =>
    {
        var (_, store, eye, face, _) = Library("activate");
        store.SelectChannels(eye, face);
        store.Activate(eye);
        CheckPair(store, eye, eye);
        Check(store.PreviousEyeModule == eye && store.PreviousFaceModule == face, "Activation lost the prior split selection.");
        store.Rollback();
        CheckPair(store, eye, face);
        var prepared = store.Prepare(fixture);
        store.Activate(prepared);
        CheckPair(store, prepared, prepared);
        Check(store.Installed.Any(module => module.Id == prepared.Id), "Legacy prepared-release activation stopped registering its module.");
    });

    Test("rejected selections leave state, backup, and module files unchanged", () =>
    {
        var (profile, store, eye, face, other) = Library("invalid");
        store.SelectChannels(eye, face);
        var staged = store.Prepare(fixture);
        AssertRejectedUnchanged(store, profile, () => store.SelectChannels(eye, staged));
        var foreign = new ModuleStore(Path.Combine(root, "foreign")).Install(fixture);
        AssertRejectedUnchanged(store, profile, () => store.SelectChannels(foreign, face));
        AssertRejectedUnchanged(store, profile, () => store.SelectChannels(eye, face with { DirectoryPath = root }));
        AssertRejectedUnchanged(store, profile, () => store.SelectChannels(eye, eye with { Version = "forged" }));
        AssertRejectedUnchanged(store, profile, () => store.SelectChannels(null!, face));
        AssertRejectedUnchanged(store, profile, () => store.SelectChannels(eye, null!));
        var path = Path.Combine(other.DirectoryPath, other.DllFileName);
        var payload = File.ReadAllBytes(path);
        File.Delete(path);
        AssertRejectedUnchanged(store, profile, () => store.SelectChannels(other, face));
        File.WriteAllBytes(path, payload);
        CheckPair(store, eye, face);
        Check(store.Installed.Count == 3 && staged.IsAvailable, "Rejected selection registered or removed a prepared module.");
    });

    Test("removing either selected role clears both without selecting a survivor", () =>
    {
        foreach (var removeEye in new[] { true, false })
        {
            var (profile, store, eye, face, _) = Library("remove-role-" + removeEye);
            store.SelectChannels(eye, face);
            var removed = removeEye ? eye : face;
            var survivor = removeEye ? face : eye;
            store.Remove(removed);
            CheckEmptySelection(store);
            Check(store.Installed.Any(module => module.Id == survivor.Id) && survivor.IsAvailable, "Removal deleted the other role's module.");
            Check(!store.Installed.Any(module => module.Id == removed.Id), "Removed role remained registered.");
            CheckEmptySelection(new ModuleStore(profile));
            // Removal writes a sanitized recovery manifest, so interruption or
            // later primary corruption must not resurrect either assignment.
            File.WriteAllText(StatePath(profile), "{");
            var recovered = new ModuleStore(profile);
            CheckEmptySelection(recovered);
            Check(!recovered.Installed.Any(module => module.Id == removed.Id), "Backup recovery resurrected a removed module.");
        }
    });

    Test("removing a shared selected module clears the selection", () =>
    {
        var (profile, store, eye, _, _) = Library("remove-shared");
        store.SelectChannels(eye, eye);
        store.Remove(eye);
        CheckEmptySelection(new ModuleStore(profile));
        ExpectFailure(() => store.RestorePreviousChannels());
    });

    Test("inactive removals preserve current roles and invalidate an affected previous pair", () =>
    {
        var (profile, store, eye, face, other) = Library("remove-previous");
        store.SelectChannels(eye, face);
        store.Activate(other);
        store.Remove(face);
        CheckPair(store, other, other);
        Check(store.Previous is null && store.PreviousEyeModule is null && store.PreviousFaceModule is null,
            "Removing one previous role retained a partial rollback selection.");
        AssertRejectedUnchanged(store, profile, () => store.RestorePreviousChannels());
        Check(store.Installed.Any(module => module.Id == eye.Id), "Removal deleted the unaffected former eye module.");
        CheckPair(new ModuleStore(profile), other, other);
        var inactive = store.Prepare(fixture);
        store.Register(inactive);
        store.Remove(inactive);
        CheckPair(store, other, other);
    });

    Test("removing a current role preserves an unrelated complete previous pair", () =>
    {
        var (profile, store, eye, face, previous) = Library("remove-with-history");
        store.Activate(previous);
        store.SelectChannels(eye, face);
        store.Remove(face);
        CheckEmptySelection(store);
        Check(store.PreviousEyeModule == previous && store.PreviousFaceModule == previous, "Removal discarded unrelated rollback history.");
        var restored = store.RestorePreviousChannels();
        Check(restored.Modules.Count == 1, "Restoring shared history duplicated the module.");
        CheckPair(new ModuleStore(profile), previous, previous);
    });

    Test("legacy schemas map their saved selection into both roles without a reset", () =>
    {
        foreach (var schema in new[] { 1, 2 })
        {
            var (profile, _, eye, face, other) = Library("legacy-" + schema);
            var legacy = JsonSerializer.Serialize(new { Schema = schema, Current = eye.Id, Previous = face.Id,
                Installed = new[] { eye.Id, face.Id, other.Id } });
            File.WriteAllText(StatePath(profile), legacy);
            var store = new ModuleStore(profile);
            CheckPair(store, eye, eye);
            Check(store.PreviousEyeModule == face && store.PreviousFaceModule == face, "Legacy previous selection did not own both roles.");
            Check(File.ReadAllText(StatePath(profile)) == legacy, "Reading a legacy profile rewrote its state unnecessarily.");
            store.SelectChannels(eye, other);
            CheckPair(new ModuleStore(profile), eye, other);
            Check(store.PreviousEyeModule == eye && store.PreviousFaceModule == eye, "Upgrade did not preserve the old selection as a pair.");
        }
    });

    Test("legacy rollback remains reversible after schema upgrade", () =>
    {
        var (profile, _, eye, face, _) = Library("legacy-rollback");
        File.WriteAllText(StatePath(profile), JsonSerializer.Serialize(new { Schema = 2, Current = eye.Id, Previous = face.Id,
            Installed = new[] { eye.Id, face.Id } }));
        var store = new ModuleStore(profile);
        Check(store.Rollback() == face && store.Previous == eye, "Legacy rollback changed its return or history semantics.");
        CheckPair(new ModuleStore(profile), face, face);
        Check(store.Rollback() == eye && store.Previous == face, "Legacy undo rollback failed.");
        CheckPair(store, eye, eye);
    });

    Test("an unavailable selected module stays assigned but cannot be reselected or restored", () =>
    {
        var (profile, store, eye, face, other) = Library("unavailable");
        store.SelectChannels(eye, face);
        File.Delete(Path.Combine(face.DirectoryPath, face.DllFileName));
        store = new ModuleStore(profile);
        Check(store.EyeModule == eye && store.FaceModule == face && store.FaceModule.IsAvailable == false,
            "Missing files silently reassigned one of the roles.");
        AssertRejectedUnchanged(store, profile, () => store.SelectChannels(eye, face));
        store.Activate(other);
        AssertRejectedUnchanged(store, profile, () => store.RestorePreviousChannels());
        CheckPair(store, other, other);
    });

    Test("inconsistent role manifests recover the last valid complete backup", () =>
    {
        var (profile, store, eye, face, _) = Library("recovery");
        store.SelectChannels(eye, face);
        var valid = StateBytes(profile);
        File.WriteAllBytes(StatePath(profile) + ".backup", valid);
        File.WriteAllText(StatePath(profile), JsonSerializer.Serialize(new { Schema = 3, Current = face.Id, Previous = (string?)null,
            Installed = new[] { eye.Id, face.Id }, EyeModuleId = face.Id, FaceModuleId = (string?)null }));
        CheckPair(new ModuleStore(profile), eye, face);
        var repaired = new ModuleStore(profile);
        CheckPair(repaired, eye, face);
        Check(repaired.Installed.Count == 3, "Recovery lost other registered modules.");
    });

    Test("a corrupt inactive custom DLL remains removable without blocking healthy sources", () =>
    {
        var (profile, store, eye, face, other) = Library("corrupt-inactive");
        store.SelectChannels(eye, face);
        File.WriteAllBytes(Path.Combine(other.DirectoryPath, other.DllFileName), [1, 2, 3]);
        store = new ModuleStore(profile);
        CheckPair(store, eye, face);
        Check(store.Installed.Any(module => module.Id == other.Id), "The damaged inactive module disappeared from recovery.");
        AssertRejectedUnchanged(store, profile, () => store.Activate(other));
        AssertRejectedUnchanged(store, profile, () => store.SelectChannels(eye, other));
        store.Remove(other);
        CheckPair(new ModuleStore(profile), eye, face);
        Check(!store.Installed.Any(module => module.Id == other.Id), "The corrupt inactive module could not be removed.");
    });

    Test("a corrupt selected custom DLL opens for recovery but cannot be activated or restored", () =>
    {
        var (profile, store, eye, face, healthy) = Library("corrupt-selected");
        store.Activate(healthy);
        store.SelectChannels(eye, face);
        File.WriteAllBytes(Path.Combine(face.DirectoryPath, face.DllFileName), [1, 2, 3]);
        store = new ModuleStore(profile);
        CheckPair(store, eye, face);
        AssertRejectedUnchanged(store, profile, () => store.Activate(face));
        AssertRejectedUnchanged(store, profile, () => store.SelectChannels(eye, face));
        store.RestorePreviousChannels();
        CheckPair(store, healthy, healthy);
        AssertRejectedUnchanged(store, profile, () => store.RestorePreviousChannels());
        store.Remove(face);
        CheckPair(new ModuleStore(profile), healthy, healthy);
        Check(!store.PreviousSelection.IsComplete, "Removing the damaged rollback source retained a partial previous pair.");
    });

    Test("corrupt payload recovery retains strict custom metadata identity and path checks", () =>
    {
        var (profile, store, eye, face, other) = Library("corrupt-metadata");
        store.SelectChannels(eye, face);
        File.WriteAllBytes(Path.Combine(other.DirectoryPath, other.DllFileName), [1, 2, 3]);
        var metadataPath = Path.Combine(profile, "modules", "releases", other.Id, "release.json");
        var metadata = File.ReadAllText(metadataPath);
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(other with { DirectoryPath = root }));
        ExpectFailure(() => new ModuleStore(profile));
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(other with { AssemblyName = "VRCFaceTracking.Baballonia" }));
        ExpectFailure(() => new ModuleStore(profile));
        File.WriteAllText(metadataPath, metadata);
        CheckPair(new ModuleStore(profile), eye, face);
    });

    Test("single-module Install and rollback remain compatible", () =>
    {
        var profile = Path.Combine(root, "legacy-install");
        var store = new ModuleStore(profile);
        var first = store.Install(fixture);
        var second = store.Install(fixture);
        CheckPair(store, second, second);
        Check(store.Previous == first && store.Rollback() == first, "Single-module rollback changed.");
        CheckPair(new ModuleStore(profile), first, first);
        Check(store.Rollback() == second, "Single-module undo rollback changed.");
    });

    if (OperatingSystem.IsWindows())
    {
        Test("a failed atomic write preserves the in-memory pair and rollback history", () =>
        {
            var (profile, store, eye, face, other) = Library("write-failure");
            store.SelectChannels(eye, face);
            using (var heldState = new FileStream(StatePath(profile), FileMode.Open, FileAccess.Read, FileShare.Read))
                AssertRejectedUnchanged(store, profile, () => store.SelectChannels(other, eye));
            CheckPair(store, eye, face);
            CheckPair(new ModuleStore(profile), eye, face);
        });
    }

    Console.WriteLine($"PASS {tests} scenarios, {checks} checks.");
    return 0;
}
finally
{
    var canonical = Path.GetFullPath(root);
    if (!canonical.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing cleanup outside the isolated channel-selection workspace.");
    Directory.Delete(canonical, recursive: true);
}

void Test(string name, Action action)
{
    action();
    tests++;
    Console.WriteLine("PASS " + name);
}

void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

void CheckPair(ModuleStore store, ModuleRelease eye, ModuleRelease face)
{
    Check(store.EyeModule == eye && store.FaceModule == face && store.Selection.IsComplete, "The selected eye/face pair changed.");
    Check(store.Selection.EyeModule == eye && store.Selection.FaceModule == face && store.Current == eye, "Selection and legacy Current disagree.");
}

void CheckEmptySelection(ModuleStore store)
{
    Check(store.Current is null && store.EyeModule is null && store.FaceModule is null, "A cleared role selection reappeared.");
    Check(!store.Selection.IsComplete && store.Selection.Modules.Count == 0, "A cleared selection still includes active modules.");
}

void ExpectFailure(Action action)
{
    try { action(); }
    catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
    { checks++; return; }
    throw new InvalidOperationException("An invalid operation unexpectedly succeeded.");
}

void AssertRejectedUnchanged(ModuleStore store, string profile, Action action)
{
    var state = StateBytes(profile);
    var backup = File.ReadAllBytes(StatePath(profile) + ".backup");
    var selection = store.Selection;
    var previous = store.PreviousSelection;
    ExpectFailure(action);
    Check(state.SequenceEqual(StateBytes(profile)), "A rejected selection changed the state manifest.");
    Check(backup.SequenceEqual(File.ReadAllBytes(StatePath(profile) + ".backup")), "A rejected selection changed the recovery manifest.");
    Check(store.Selection == selection && store.PreviousSelection == previous, "A rejected selection changed the in-memory state.");
}

(string Profile, ModuleStore Store, ModuleRelease Eye, ModuleRelease Face, ModuleRelease Other) Library(string name)
{
    var profile = Path.Combine(root, name);
    var store = new ModuleStore(profile);
    var eye = store.Prepare(fixture);
    var face = store.Prepare(fixture);
    var other = store.Prepare(fixture);
    store.Register(eye); store.Register(face); store.Register(other);
    return (profile, store, eye, face, other);
}

string StatePath(string profile) => Path.Combine(profile, "modules", "module-state.json");
byte[] StateBytes(string profile) => File.ReadAllBytes(StatePath(profile));
