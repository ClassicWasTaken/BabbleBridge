// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using VRCFaceTracking.Core.Params.Data;

var passed = 0;
var failed = 0;

Test("first and constant physical pupil samples stay neutral", () =>
{
    var eyes = Eyes(5f, 5f);
    Near(eyes.Combined().PupilDiameter_MM, .5f);
    Near(eyes.Combined().PupilDiameter_MM, .5f);
    Near(eyes._minDilation, 5f);
    Near(eyes._maxDilation, 5f);
});

Test("physical pupil range learns increasing and decreasing samples", () =>
{
    var eyes = Eyes(5f, 5f);
    eyes.Combined();
    Set(eyes, 6f, 6f);
    Near(eyes.Combined().PupilDiameter_MM, 1f);
    Set(eyes, 4f, 4f);
    Near(eyes.Combined().PupilDiameter_MM, 0f);
    Set(eyes, 5f, 5f);
    Near(eyes.Combined().PupilDiameter_MM, .5f);
    Near(eyes.NormalizePupilDiameter(3f), 0f);
    Near(eyes.NormalizePupilDiameter(7f), 1f);
});

Test("normalized Frame dilation retains its full scale without relearning", () =>
{
    var eyes = Eyes(5f, 5f);
    eyes.NormalizedPupilRange = (2f, 8f);
    foreach (var normalized in new[] { .5f, .6f, .4f, .5f, 0f, 1f, .5f })
    {
        Set(eyes, 2f + 6f * normalized, 2f + 6f * normalized);
        Near(eyes.Combined().PupilDiameter_MM, normalized);
    }
});

Test("per-eye normalization preserves asymmetry and clamps outliers", () =>
{
    var eyes = Eyes(3.5f, 6.5f);
    eyes.NormalizedPupilRange = (2f, 8f);
    Near(eyes.NormalizePupilDiameter(eyes.Left.PupilDiameter_MM), .25f);
    Near(eyes.NormalizePupilDiameter(eyes.Right.PupilDiameter_MM), .75f);
    Near(eyes.Combined().PupilDiameter_MM, .5f);
    Near(eyes.NormalizePupilDiameter(1f), 0f);
    Near(eyes.NormalizePupilDiameter(9f), 1f);
});

Test("missing and invalid pupil values remain finite and neutral", () =>
{
    foreach (var invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
    {
        var eyes = Eyes(invalid, invalid);
        Near(eyes.Combined().PupilDiameter_MM, .5f);
        eyes.NormalizedPupilRange = (2f, 8f);
        Near(eyes.Combined().PupilDiameter_MM, .5f);
        Near(eyes.NormalizePupilDiameter(invalid), .5f);
    }
});

Test("one missing eye does not halve the valid physical diameter", () =>
{
    var eyes = Eyes(float.NaN, 5f);
    eyes.NormalizedPupilRange = (2f, 8f);
    Near(eyes.Combined().PupilDiameter_MM, .5f);
    Set(eyes, 8f, 0f);
    Near(eyes.Combined().PupilDiameter_MM, 1f);
});

Test("copying an eye snapshot preserves and clears the source range policy", () =>
{
    var source = Eyes(3.5f, 6.5f);
    source.NormalizedPupilRange = (2f, 8f);
    var copy = new UnifiedEyeData();
    copy.CopyPropertiesOf(source);
    Check(copy.NormalizedPupilRange == (2f, 8f), "Explicit range was lost during snapshot copy.");
    Near(copy.NormalizePupilDiameter(copy.Left.PupilDiameter_MM), .25f);
    Near(copy.NormalizePupilDiameter(copy.Right.PupilDiameter_MM), .75f);
    source.NormalizedPupilRange = null;
    source.Combined();
    copy.CopyPropertiesOf(source);
    Check(copy.NormalizedPupilRange is null, "Adaptive source retained another source's explicit range.");
    Near(copy.Combined().PupilDiameter_MM, .5f);
});

Test("pupil handling leaves eye openness and gaze unchanged", () =>
{
    var eyes = Eyes(5f, 5f);
    eyes.Left.Openness = .2f;
    eyes.Right.Openness = .8f;
    eyes.Left.Gaze.x = -.4f;
    eyes.Right.Gaze.x = .2f;
    var combined = eyes.Combined();
    Near(combined.Openness, .5f);
    Near(combined.Gaze.x, -.1f);
    Near(eyes.Left.Openness, .2f);
    Near(eyes.Right.Openness, .8f);
});

Test("invalid explicit ranges are rejected", () =>
{
    foreach (var range in new[] { (2f, 2f), (8f, 2f), (-1f, 8f), (float.NaN, 8f), (2f, float.PositiveInfinity) })
    {
        try
        {
            new UnifiedEyeData().NormalizedPupilRange = range;
            throw new InvalidOperationException("Invalid pupil normalization range was accepted.");
        }
        catch (ArgumentOutOfRangeException) { }
    }
});

Console.WriteLine($"{passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

void Test(string name, Action body)
{
    try { body(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failed++; Console.Error.WriteLine($"FAIL {name}: {exception.Message}"); }
}

static UnifiedEyeData Eyes(float left, float right)
{
    var eyes = new UnifiedEyeData();
    Set(eyes, left, right);
    return eyes;
}

static void Set(UnifiedEyeData eyes, float left, float right)
{
    eyes.Left.PupilDiameter_MM = left;
    eyes.Right.PupilDiameter_MM = right;
}

static void Near(float actual, float expected)
{
    Check(float.IsFinite(actual) && Math.Abs(actual - expected) < .00001f, $"Expected {expected}; received {actual}.");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
