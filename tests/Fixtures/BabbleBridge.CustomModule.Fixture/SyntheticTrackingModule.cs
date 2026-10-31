// Copyright (c) 2026 Babble Bridge contributors.
// SPDX-License-Identifier: Apache-2.0
// A hardware-free, deterministic SDK module. Never ships in app packages.
using System.Runtime.InteropServices;
using System.Text.Json;
using BabbleBridge.CustomModule.Dependency;
using VRCFaceTracking;
using VRCFaceTracking.Core.Params.Expressions;

namespace BabbleBridge.CustomModule.Fixture;

public sealed class SyntheticTrackingModule : ExtTrackingModule, IFixtureDependency
{
    private Dictionary<string, float> _values = new();
    private string _mode = "";
    private float Value(string key, float fallback) => _values.GetValueOrDefault(key, fallback);
    public override (bool SupportsEye, bool SupportsExpression) Supported => (true, true);
    public override (bool eyeSuccess, bool expressionSuccess) Initialize(bool eyeAvailable, bool expressionAvailable)
    {
        var mode = File.Exists("fixture-mode.txt") ? File.ReadAllText("fixture-mode.txt").Trim() : "";
        _mode = mode;
        if (File.Exists("fixture-values.json")) _values = JsonSerializer.Deserialize<Dictionary<string, float>>(File.ReadAllText("fixture-values.json"))!;
        File.WriteAllText("fixture-initialized.txt", Environment.ProcessId.ToString());
        if (mode == "unsupported-sdk") throw new MissingMethodException("Synthetic fixture requires VRCFaceTracking.FutureSdkApi().");
        if (mode == "missing-native") _ = MissingNativeFixture();
        if (mode == "no-tracking") return (false, false);
        return (eyeAvailable, expressionAvailable);
    }
    public override void Update()
    {
        if (_mode == "malformed-snapshot") Console.WriteLine("{\"type\":\"input\",\"version\":1,\"sequence\":1,\"shapes\":\"invalid\"}");
        if (File.Exists("fixture-pause.flag")) Thread.Sleep(5000);
        UnifiedTracking.Data.Eye.Left.Openness = Value("left", FixtureValues.EyeLeft);
        UnifiedTracking.Data.Eye.Right.Openness = Value("right", FixtureValues.EyeRight);
        UnifiedTracking.Data.Eye.Left.Gaze = new(Value("x", 0), Value("y", 0));
        UnifiedTracking.Data.Eye.Right.Gaze = new(Value("rx", 0), Value("ry", 0));
        UnifiedTracking.Data.Eye.NormalizedPupilRange = (3f, 7f);
        UnifiedTracking.Data.Eye.Left.PupilDiameter_MM = 4;
        UnifiedTracking.Data.Eye.Right.PupilDiameter_MM = 5;
        UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.JawOpen].Weight = Value("jaw", FixtureValues.JawOpen);
        UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight = Value("wide", .4f);
        UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeWideRight].Weight = .2f;
        UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.BrowInnerUpLeft].Weight = Value("brow", 0);
    }
    public override void Teardown() => File.WriteAllText("fixture-torn-down.txt", "stopped");
    // This is deliberately unrelated to the SDK: generic hosting must not probe it.
    private bool RestartNeeded(int unused) => false;
    [DllImport("BabbleBridge_Test_Missing_Native_Dependency_1023")]
    private static extern int MissingNativeFixture();
}
