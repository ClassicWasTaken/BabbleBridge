// Frame Bridge modification notice (2026-10-29): safe pupil normalization and explicit normalized source ranges.
// Upstream: Project-Babble/VRCFaceTracking b4b39da5c9fe048c960aa3b9e56742834df6ff36.
// Copyright 2024 benaclejames. Apache-2.0; see vendor/LICENSE and repository NOTICE.txt.
using VRCFaceTracking.Core.Params.Expressions;
using VRCFaceTracking.Core.Types;

namespace VRCFaceTracking.Core.Params.Data
{
    /// <summary>
    /// Struct that represents a single eye.
    /// </summary>
    public struct UnifiedSingleEyeData
    {
        // Eye data.
        public Vector2 Gaze;
        public float PupilDiameter_MM;
        public float Openness = 1.0f;

        public UnifiedSingleEyeData() {}
    }

    /// <summary>
    /// Struct that represents all possible eye data. 
    /// </summary>
    public class UnifiedEyeData
    {
        public UnifiedSingleEyeData Left = new (), Right = new ();
        public float _maxDilation, _minDilation = 999f;
        public float _leftDiameter, _rightDiameter;
        private (float MinMm, float MaxMm)? _normalizedPupilRange;

        /// <summary>
        /// An explicit diameter range for a source that already supplies normalized pupil dilation.
        /// Leave null to learn the observed range of a source that supplies physical diameters.
        /// </summary>
        public (float MinMm, float MaxMm)? NormalizedPupilRange
        {
            get => _normalizedPupilRange;
            set
            {
                if (value is { } range &&
                    (!float.IsFinite(range.MinMm) || !float.IsFinite(range.MaxMm) ||
                     range.MinMm < 0f || range.MaxMm <= range.MinMm))
                    throw new ArgumentOutOfRangeException(nameof(value), "The pupil range must have finite, increasing, nonnegative endpoints.");
                _normalizedPupilRange = value;
            }
        }

        private static bool IsValidPupilDiameter(float value) => float.IsFinite(value) && value > 0f;

        private float AveragePupilDiameter()
        {
            var leftValid = IsValidPupilDiameter(Left.PupilDiameter_MM);
            var rightValid = IsValidPupilDiameter(Right.PupilDiameter_MM);
            if (leftValid && rightValid)
                return Left.PupilDiameter_MM * .5f + Right.PupilDiameter_MM * .5f;
            return leftValid ? Left.PupilDiameter_MM : rightValid ? Right.PupilDiameter_MM : 0f;
        }

        private void UpdatePupilRange()
        {
            _leftDiameter = Left.PupilDiameter_MM;
            _rightDiameter = Right.PupilDiameter_MM;
            if (_normalizedPupilRange.HasValue)
                return;

            var average = AveragePupilDiameter();
            if (!IsValidPupilDiameter(average))
                return;
            if (!float.IsFinite(_minDilation) || !float.IsFinite(_maxDilation) ||
                _minDilation < 0f || _maxDilation <= 0f || _minDilation > _maxDilation)
            {
                _minDilation = _maxDilation = average;
                return;
            }
            // Both endpoints must be initialized together; a first sample is neutral until a range exists.
            _minDilation = Math.Min(_minDilation, average);
            _maxDilation = Math.Max(_maxDilation, average);
        }

        /// <summary>
        /// Normalize one eye using the source's explicit range or the shared observed diameter range.
        /// Missing diameters and an as-yet constant observed range return neutral dilation (0.5).
        /// </summary>
        public float NormalizePupilDiameter(float diameterMm)
        {
            UpdatePupilRange();
            if (!IsValidPupilDiameter(diameterMm))
                return .5f;
            var (min, max) = _normalizedPupilRange ?? (_minDilation, _maxDilation);
            if (!float.IsFinite(min) || !float.IsFinite(max) || max <= min)
                return .5f;
            var normalized = ((double)diameterMm - min) / ((double)max - min);
            return (float)Math.Clamp(normalized, 0.0, 1.0);
        }

        /// <summary>
        /// Creates relevant data that combines the Left and Right single eye datas into a combined single eye data.
        /// </summary>
        /// <returns>Relevant combined Left and Right Eye data</returns>
        /// <remarks>
        /// Pupil Dilation is averaged between both the Left and Right eye datas, and also normalizes the range between 0.0 - 1.0. 
        /// The combined eye gaze is based off of the validity of the eye data; Both eyes being valid will return the averaged vectors between the two,
        /// If only one is valid then the valid eye will control the gaze vector.
        /// </remarks>
        public UnifiedSingleEyeData Combined()
        {
            var normalizedDilation = NormalizePupilDiameter(AveragePupilDiameter());

            return new UnifiedSingleEyeData
            {
                Gaze = (Left.Gaze + Right.Gaze) / 2.0f,
                Openness = (Left.Openness + Right.Openness) / 2.0f,
                PupilDiameter_MM = normalizedDilation,
            };
        }
        public void CopyPropertiesOf(UnifiedEyeData data)
        {
            data.Combined();

            this.Left = data.Left;
            this.Right = data.Right;
            this._maxDilation = data._maxDilation;
            this._minDilation = data._minDilation;
            this._rightDiameter = data._rightDiameter;
            this._leftDiameter = data._leftDiameter;
            this.NormalizedPupilRange = data.NormalizedPupilRange;
        }
    }

    /// <summary>
    /// Container of information pertaining to a singular Unified Expression shape.
    /// </summary>
    public struct UnifiedExpressionShape
    {
        /// <summary>
        /// Value that contains the specified Unified Expression raw value.
        /// </summary>
        public float Weight;
    }

    public struct UnifiedHeadData
    {
        /// <summary>
        /// Contains head rotation and position data.
        /// Head coordinate system should be *LEFT handed, Y-up* (matching Unity coordinate system)
        /// Head rotation values should be normalized to a [-1, 1] range representing -90d to 90d rotation (0 meaning facing directly forwards)
        ///     Yaw - Y axis rotation (positive values = looking right)
        ///     Pitch - X axis rotation (positive values = looking down)
        ///     Roll - Z axis rotation (positive values = side-tilt left)
        /// Head position values should be normalized / capped to a [-1, 1] range and represent deviation from a set user origin point in meters
        ///     The normalized values should be *approximately* represent a 1x1x1 meter movement region about the user's origin point 
        ///     (i.e. HeadPosX = 1 means the user is 0.5m to their right from their starting point, HeadPosX = -1 the user is 0.5m to their left)
        /// </summary>
        public float HeadYaw;
        public float HeadPitch;
        public float HeadRoll;

        public float HeadPosX;
        public float HeadPosY;
        public float HeadPosZ;
    }

    /// <summary>
    /// All data that is accessible by modules and is output to parameters.
    /// </summary>
    public class UnifiedTrackingData
    {
        /// <summary>
        /// Container of all relevant Unified raw eye data. 
        /// </summary>
        public UnifiedEyeData Eye = new UnifiedEyeData();

        /// <summary>
        /// Container of all Unified Expression expression data. 
        /// </summary>
        /// <remarks>
        /// Example of usage:
        /// <code>
        /// // Update JawOpen shape in Expression Data.
        /// Shapes[(int)UnifiedExpression.JawOpen].Weight = JawOpen;
        /// </code>
        /// </remarks>
        public UnifiedExpressionShape[] Shapes = new UnifiedExpressionShape[(int)UnifiedExpressions.Max + 1];

        /// <summary>
        /// Container of head pose data.
        /// </summary>
        public UnifiedHeadData Head = new UnifiedHeadData();

        public void CopyPropertiesOf(UnifiedTrackingData data)
        {
            Eye.CopyPropertiesOf(data.Eye);
            for (int i = 0; i < Shapes.Length; i++)
                Shapes[i].Weight = data.Shapes[i].Weight;
            
            Head.HeadYaw   = data.Head.HeadYaw;
            Head.HeadPitch = data.Head.HeadPitch;
            Head.HeadRoll  = data.Head.HeadRoll;
            Head.HeadPosX  = data.Head.HeadPosX;
            Head.HeadPosY  = data.Head.HeadPosY;
            Head.HeadPosZ  = data.Head.HeadPosZ; 
        }
    }
}
