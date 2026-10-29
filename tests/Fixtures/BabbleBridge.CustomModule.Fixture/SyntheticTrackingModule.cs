// Copyright (c) 2026 Babble Bridge contributors.
// SPDX-License-Identifier: Apache-2.0
// A hardware-free, deterministic SDK module. Never ships in app packages.
using System.Runtime.InteropServices;
using BabbleBridge.CustomModule.Dependency;
using VRCFaceTracking;
using VRCFaceTracking.Core.Params.Expressions;

namespace BabbleBridge.CustomModule.Fixture;

public sealed class SyntheticTrackingModule : ExtTrackingModule, IFixtureDependency
{
    public override (bool SupportsEye, bool SupportsExpression) Supported => (true, true);
    public override (bool eyeSuccess, bool expressionSuccess) Initialize(bool eyeAvailable, bool expressionAvailable)
    {
        var mode = File.Exists("fixture-mode.txt") ? File.ReadAllText("fixture-mode.txt").Trim() : "";
        if (mode == "unsupported-sdk") throw new MissingMethodException("Synthetic fixture requires VRCFaceTracking.FutureSdkApi().");
        if (mode == "missing-native") _ = MissingNativeFixture();
        if (mode == "no-tracking") return (false, false);
        return (eyeAvailable, expressionAvailable);
    }
    public override void Update()
    {
        UnifiedTracking.Data.Eye.Left.Openness = FixtureValues.EyeLeft;
        UnifiedTracking.Data.Eye.Right.Openness = FixtureValues.EyeRight;
        UnifiedTracking.Data.Eye.NormalizedPupilRange = (3f, 7f);
        UnifiedTracking.Data.Eye.Left.PupilDiameter_MM = 4;
        UnifiedTracking.Data.Eye.Right.PupilDiameter_MM = 5;
        UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.JawOpen].Weight = FixtureValues.JawOpen;
        UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight = .4f;
        UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeWideRight].Weight = .2f;
    }
    public override void Teardown() => File.WriteAllText("fixture-torn-down.txt", "stopped");
    // This is deliberately unrelated to the SDK: generic hosting must not probe it.
    private bool RestartNeeded(int unused) => false;
    [DllImport("BabbleBridge_Test_Missing_Native_Dependency_1023")]
    private static extern int MissingNativeFixture();
}
