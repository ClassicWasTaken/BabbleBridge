// Frame Bridge modification notice (2026-10-29): added native face-tracking capability metadata.
// Upstream: Project-Babble/VRCFaceTracking b4b39da5c9fe048c960aa3b9e56742834df6ff36.
// Copyright 2024 benaclejames. Apache-2.0; see vendor/LICENSE and repository NOTICE.txt.
using System.Text.Json.Serialization;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Models.Osc.FileBased;

namespace VRCFaceTracking.Core.Models.ParameterDefinition.FileBased;

public class AvatarConfigFile : IAvatarInfo
{
    public string id { get; set; }
    public string name { get; set; }
    public bool fullFaceTracking { get; set; }
    public AvatarConfigFileParameter[] parameters { get; set; }

    [JsonIgnore] public string Name => name;

    [JsonIgnore] public string Id => id;

    [JsonIgnore] public IParameterDefinition[] Parameters => parameters;
}
