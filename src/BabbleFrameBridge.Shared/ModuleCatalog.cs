// Copyright (c) 2026 Babble Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

namespace BabbleFrameBridge.Shared;

public enum ModuleKind { BabbleHeadsetRuntime, BaballoniaDesktop, VirtualDesktop, SteamLink, Custom }

public sealed record ModuleDefinition(ModuleKind Kind, string Name, string ModuleId, string DllFileName, int InputPort, string Instructions)
{
    public string AssemblyName => Path.GetFileNameWithoutExtension(DllFileName);
    public string Connection => Kind == ModuleKind.Custom ? "Module-managed input" : InputPort == 0 ? "Shared memory" : $"UDP {InputPort}";
}

/// <summary>Known entry points, not a trust/signature check. Imported modules execute with the user's permissions.</summary>
public static class ModuleCatalog
{
    private static readonly ModuleDefinition Custom = new(ModuleKind.Custom, "Custom module", "", "", 0,
        "Module-managed input. Follow this module's setup instructions.");
    public static readonly IReadOnlyList<ModuleDefinition> All = Array.AsReadOnly(new[] {
        new ModuleDefinition(ModuleKind.BabbleHeadsetRuntime, "Babble Headset Runtime", ModuleStore.BaballoniaModuleId, ModuleStore.BaballoniaDll, 6767,
            "Start Babble Frame Runtime on your headset. Discovery is automatic on your local network."),
        new ModuleDefinition(ModuleKind.BaballoniaDesktop, "Baballonia", ModuleStore.BaballoniaModuleId, ModuleStore.BaballoniaDll, 8888,
            "Enable Baballonia’s VRCFaceTracking output to 127.0.0.1:8888."),
        new ModuleDefinition(ModuleKind.VirtualDesktop, "Virtual Desktop", "91a90618-b020-4064-8832-809b2ca2b3bc", "VirtualDesktop.FaceTracking.dll", 0,
            "Connect Virtual Desktop Streamer 1.30 or newer and enable forwarding of eye and face tracking."),
        new ModuleDefinition(ModuleKind.SteamLink, "Steam Link", "2a8c8080-2a76-46af-bf76-1da7c0127ef8", "SteamLinkVRCFTModule.dll", 9015,
            "Enable eye and face tracking in Steam Link. LinkFT receives OSC on 127.0.0.1:9015.")
    });

    public static ModuleDefinition Get(ModuleKind kind) => kind == ModuleKind.Custom ? Custom : All.FirstOrDefault(x => x.Kind == kind)
        ?? throw new ArgumentException("Choose a supported module.");

    public static ModuleDefinition Identify(string? id, string? dll)
    {
        if (!Guid.TryParse(id, out var moduleId) || string.IsNullOrWhiteSpace(dll))
            throw new InvalidDataException("The module manifest must specify a valid module ID and entry DLL.");
        var known = All.FirstOrDefault(x => string.Equals(x.ModuleId, moduleId.ToString("D"), StringComparison.OrdinalIgnoreCase));
        if (known != null)
        {
            if (dll != known.DllFileName) throw new InvalidDataException("The entry DLL does not match this known module's identity.");
            return known;
        }
        if (All.Any(x => string.Equals(x.DllFileName, dll, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The module ID does not match this known module's entry DLL.");
        return Custom with { ModuleId = moduleId.ToString("D"), DllFileName = dll };
    }
}
