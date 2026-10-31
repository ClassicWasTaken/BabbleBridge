// Copyright (c) 2026 Face Tracking Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace BabbleFrameBridge.Shared;

/// <summary>Versioned, bounded IPC data; contains no module objects or executable payload.</summary>
public sealed record ModuleChannelSnapshot
{
    public const int ProtocolVersion = 1;
    public const int MaximumLineLength = 16_384;
    public const int MaximumShapes = 128;
    [JsonPropertyName("type")] public string Type { get; init; } = "input";
    [JsonPropertyName("version")] public int Version { get; init; } = ProtocolVersion;
    [JsonPropertyName("sequence")] public long Sequence { get; init; }
    [JsonPropertyName("eye")] public bool Eye { get; init; }
    [JsonPropertyName("face")] public bool Face { get; init; }
    [JsonPropertyName("connected")] public bool Connected { get; init; }
    [JsonPropertyName("receivingKnown")] public bool ReceivingKnown { get; init; }
    [JsonPropertyName("receiving")] public bool Receiving { get; init; }
    [JsonPropertyName("pupilAvailable")] public bool PupilAvailable { get; init; }
    [JsonPropertyName("widenAvailable")] public bool WidenAvailable { get; init; }
    [JsonPropertyName("left")] public ModuleEyeSnapshot Left { get; init; } = new();
    [JsonPropertyName("right")] public ModuleEyeSnapshot Right { get; init; } = new();
    [JsonPropertyName("pupilMinimum")] public float? PupilMinimum { get; init; }
    [JsonPropertyName("pupilMaximum")] public float? PupilMaximum { get; init; }
    [JsonPropertyName("shapes")] public float[] Shapes { get; init; } = [];

    public bool IsValid(int shapeCount) => Type == "input" && Version == ProtocolVersion && Sequence > 0
        && shapeCount is > 0 and <= MaximumShapes && Shapes is { } shapes && shapes.Length == shapeCount
        && shapes.All(value => float.IsFinite(value) && value is >= 0 and <= 1)
        && Left is not null && Right is not null && Left.IsValid() && Right.IsValid()
        && (PupilMinimum is null && PupilMaximum is null
            || PupilMinimum is { } min && PupilMaximum is { } max && float.IsFinite(min) && float.IsFinite(max) && min >= 0 && max > min && max <= 100);
}

public sealed record ModuleEyeSnapshot
{
    [JsonPropertyName("x")] public float X { get; init; }
    [JsonPropertyName("y")] public float Y { get; init; }
    [JsonPropertyName("openness")] public float Openness { get; init; } = 1;
    [JsonPropertyName("pupil")] public float Pupil { get; init; }
    public bool IsValid() => float.IsFinite(X) && X is >= -1 and <= 1 && float.IsFinite(Y) && Y is >= -1 and <= 1
        && float.IsFinite(Openness) && Openness is >= -1 and <= 2 && float.IsFinite(Pupil) && Pupil is >= 0 and <= 100;
}
