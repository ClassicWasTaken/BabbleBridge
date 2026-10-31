// Copyright (c) 2026 Face Tracking Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using BabbleFrameBridge.Shared;

namespace BabbleFrameBridge.App;

public partial class MainWindow
{
    private ModuleRelease? _sessionFaceRelease;
    private bool _syncingSources;
    private string _sourceLibraryKey = "", _sourceSelectionKey = "";
    private sealed record ModuleSourceChoice(string Id, string Label);
    private ModuleChannelSelection ActiveSources => _sessionRelease is { } eye
        ? new(eye, _sessionFaceRelease ?? eye) : _store.Selection;
    private bool SelectedSourcesAvailable => _store.Selection.IsComplete && _store.Selection.Modules.All(module => module.IsAvailable);

    private bool IsSelectedForBoth(ModuleRelease release) => _store.EyeModule?.Id == release.Id && _store.FaceModule?.Id == release.Id;
    private string SourceSelectionLabel(ModuleRelease release)
    {
        if (!_store.Selection.IsSelected(release.Id)) return release.IsAvailable ? "Use module" : "Unavailable";
        var label = IsSelectedForBoth(release) ? "Selected" : _store.EyeModule?.Id == release.Id ? "Selected · Eyes" : "Selected · Face";
        return label + (release.IsAvailable ? " ✓" : " · Missing");
    }
    private string SourceAssignmentHint(ModuleRelease release) => !_store.Selection.IsSelected(release.Id) ? ""
        : IsSelectedForBoth(release) ? "\nSelected for eyes and face."
        : _store.EyeModule?.Id == release.Id ? "\nSelected for eyes and eyebrows." : "\nSelected for face and mouth.";

    private void RefreshSourceChoices()
    {
        if (EyeModuleBox is null || FaceModuleBox is null || _store is null) return;
        var installed = _store.Installed.ToArray();
        var libraryKey = string.Join("|", installed.Select(module => module.Id + ":" + module.Version + ":" + module.IsAvailable));
        var selectionKey = _store.EyeModule?.Id + ":" + _store.FaceModule?.Id;
        if (libraryKey != _sourceLibraryKey || selectionKey != _sourceSelectionKey)
        {
            var changedSelection = selectionKey != _sourceSelectionKey;
            var eye = changedSelection ? _store.EyeModule?.Id : EyeModuleBox.SelectedValue as string;
            var face = changedSelection ? _store.FaceModule?.Id : FaceModuleBox.SelectedValue as string;
            _syncingSources = true;
            try
            {
                var choices = installed.Select(module => new ModuleSourceChoice(module.Id,
                    NameFor(module) + " / " + module.Version + (module.IsAvailable ? "" : " (unavailable)"))).ToArray();
                EyeModuleBox.ItemsSource = choices; FaceModuleBox.ItemsSource = choices;
                EyeModuleBox.SelectedValue = choices.Any(choice => choice.Id == eye) ? eye : choices.FirstOrDefault()?.Id;
                FaceModuleBox.SelectedValue = choices.Any(choice => choice.Id == face) ? face : choices.FirstOrDefault()?.Id;
                _sourceLibraryKey = libraryKey; _sourceSelectionKey = selectionKey;
            }
            finally { _syncingSources = false; }
        }
        EyeModuleBox.IsEnabled = FaceModuleBox.IsEnabled = !_busy && _initialized && installed.Length > 0 && !_closing;
        UpdateApplySourcesButton();
    }

    private void UpdateApplySourcesButton()
    {
        if (ApplySourcesButton is null) return;
        ApplySourcesButton.IsEnabled = !_busy && _initialized && !_closing
            && _store.Installed.Any(module => module.Id == EyeModuleBox.SelectedValue as string && module.IsAvailable)
            && _store.Installed.Any(module => module.Id == FaceModuleBox.SelectedValue as string && module.IsAvailable);
    }

    private void ModuleSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSources && _initialized) UpdateApplySourcesButton();
    }

    private async void ApplySources_Click(object sender, RoutedEventArgs e) => await RunOperation(async () =>
    {
        var eye = _store.Installed.FirstOrDefault(module => module.Id == EyeModuleBox.SelectedValue as string);
        var face = _store.Installed.FirstOrDefault(module => module.Id == FaceModuleBox.SelectedValue as string);
        if (eye is null || face is null) throw new InvalidOperationException("Choose an installed module for eyes and face.");
        await ApplyModuleSources(eye, face);
    });

    private Task StartSelectedSources()
    {
        var selection = _store.Selection;
        if (!selection.IsComplete) throw new InvalidOperationException("Choose your tracking sources in Modules.");
        return StartWorker(selection.EyeModule!, selection.FaceModule!);
    }

    private Task ReplaceSelectedSource(ModuleRelease previous, ModuleRelease replacement)
    {
        var selection = _store.Selection;
        if (!selection.IsSelected(previous.Id)) return SwitchModule(replacement);
        return ApplyModuleSources(selection.EyeModule?.Id == previous.Id ? replacement : selection.EyeModule!,
            selection.FaceModule?.Id == previous.Id ? replacement : selection.FaceModule!);
    }

    private async Task ApplyModuleSources(ModuleRelease eye, ModuleRelease face)
    {
        if (!eye.IsAvailable || !face.IsAvailable) throw new InvalidOperationException("A selected module’s DLL is missing. Restore or re-import it first.");
        ValidateSourcePair(_settings, eye, face);
        var previous = _store.Selection;
        var wasRunning = _running;
        _pendingRestart = false;
        await StopWorker();
        try
        {
            await StartWorker(eye, face);
            if (eye.Id == face.Id) _store.Activate(eye);
            else _store.SelectChannels(eye, face);
            RefreshModule();
            var message = _awaitingTrackingSource ? ProviderName + " selected. Waiting for your headset connection."
                : eye.Id != face.Id ? "Eye and face sources selected. Each source connects independently."
                : previous.PrimaryModule is null ? $"Module {eye.Version} is active. Your tracking source is ready."
                : $"Module {eye.Version} is active. Your previous module is saved.";
            Log(message); ShowNotice(message);
        }
        catch (Exception failure)
        {
            await StopWorker();
            Log("The update could not start: " + failure.Message);
            if (wasRunning && previous.IsComplete)
            {
                try { await StartWorker(previous.EyeModule!, previous.FaceModule!); Log("Restored the previous tracking sources."); }
                catch (Exception restoreFailure) { Log("Previous modules could not restart: " + restoreFailure.Message); }
            }
            RefreshModule();
            throw new InvalidOperationException((previous.PrimaryModule is null
                ? "The module could not start. No module was activated. "
                : "The new module could not start. Your previous module is still installed. ") + failure.Message);
        }
    }

    private void ValidateSourcePair(AppSettings settings, ModuleRelease eye, ModuleRelease face)
    {
        ValidateConnection(settings, eye);
        if (eye.Id == face.Id) return;
        ValidateConnection(settings, face);
        var overlap = ModuleInputPorts(eye).Intersect(ModuleInputPorts(face)).FirstOrDefault();
        if (overlap > 0) throw new InvalidOperationException($"These modules both listen on UDP {overlap}. Configure different input ports in their tracking apps and module settings before combining them.");
    }

    private IReadOnlyList<int> ModuleInputPorts(ModuleRelease release)
    {
        if (release.Kind == ModuleKind.BabbleHeadsetRuntime) return new[] { _settings.LinkPort, BaballoniaOscPortFor(release) };
        var port = InputPortFor(release);
        return port > 0 ? new[] { port } : Array.Empty<int>();
    }

    private static int EyeTrackVrPortFor(ModuleRelease release)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(release.DirectoryPath, "ETVRModuleConfig.json")));
            if (document.RootElement.TryGetProperty("PortNumber", out var value) && value.TryGetInt32(out var port) && port is > 0 and <= 65535) return port;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        return 8889;
    }
}
