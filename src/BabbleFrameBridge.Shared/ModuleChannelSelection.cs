// Copyright (c) 2026 Face Tracking Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

namespace BabbleFrameBridge.Shared;

/// <summary>
/// The two tracking roles selected for a bridge session. A shared release is
/// listed once so it can provide both roles through one module instance.
/// </summary>
public sealed record ModuleChannelSelection(ModuleRelease? EyeModule, ModuleRelease? FaceModule)
{
    public ModuleRelease? PrimaryModule => EyeModule ?? FaceModule;
    public bool IsComplete => EyeModule is not null && FaceModule is not null;
    public IReadOnlyList<ModuleRelease> Modules => new[] { EyeModule, FaceModule }
        .OfType<ModuleRelease>().DistinctBy(module => module.Id).ToArray();
    public bool IsSelected(string id) => EyeModule?.Id == id || FaceModule?.Id == id;
}
