using System.Collections.Generic;
using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>床の高さをどこから決めたか。</summary>
    public enum FloorSource
    {
        /// <summary>床の平面検出。</summary>
        Plane,

        /// <summary>案内板の高さ（板の中心の高さ − 登録した床からの高さ）。</summary>
        Sign,

        /// <summary>カメラの高さ − 目の高さ。どちらも使えないときの最後の手段。</summary>
        EyeHeight,
    }

    /// <summary>床の高さと、その決め方。</summary>
    public readonly struct FloorEstimate
    {
        public readonly float Y;
        public readonly FloorSource Source;

        public FloorEstimate(float y, FloorSource source)
        {
            Y = y;
            Source = source;
        }
    }

    /// <summary>
    /// 線を描く床の高さを決める（設計書5章「床の高さは床の平面検出を優先する。検出できなければ、案内板の高さの目測から決める」）。
    /// 床が0.2mずれると5m先の曲がり角が約0.9mずれるので、明らかにおかしい面は使わない。
    /// </summary>
    public static class FloorHeight
    {
        /// <summary>カメラからこれより近い面は床とみなさない（机など）。</summary>
        public const float MinGapBelowCamera = 0.8f;

        /// <summary>カメラからこれより遠い面は床とみなさない（下の階が見えたときなど）。</summary>
        public const float MaxGapBelowCamera = 2.5f;

        /// <summary>案内板から求めた高さと、検出した面の差の上限。板の高さは目測なので広めにとる（仮）。</summary>
        public const float SignTolerance = 0.5f;

        /// <summary>
        /// 床の高さを決める。
        /// 1. カメラの 0.8〜2.5m 下にある水平な面のうち、案内板から求めた高さ（なければカメラ − 目の高さ）に一番近いもの。
        ///    案内板から求めた高さがあるときは、その差が 0.5m 以内の面だけを使う
        /// 2. 使える面がなく、案内板から求めた高さがあれば、それ
        /// 3. どちらもなければ、カメラ − 目の高さ
        /// </summary>
        /// <param name="cameraY">カメラの高さ（AR の y）。</param>
        /// <param name="horizontalPlaneYs">検出した上向きの水平な面の高さ。</param>
        /// <param name="signFloorY">案内板から求めた床の高さ（板の中心の y − centerHeightMeters）。ないときは null。</param>
        /// <param name="eyeHeight">床からカメラまでの高さの目安。</param>
        public static FloorEstimate Choose(float cameraY, IEnumerable<float> horizontalPlaneYs, float? signFloorY, float eyeHeight)
        {
            float target = signFloorY ?? cameraY - eyeHeight;
            float bestY = 0f, bestDiff = float.MaxValue;
            bool found = false;

            if (horizontalPlaneYs != null)
            {
                foreach (var y in horizontalPlaneYs)
                {
                    float gap = cameraY - y;
                    if (gap < MinGapBelowCamera || gap > MaxGapBelowCamera) continue;
                    float diff = Mathf.Abs(y - target);
                    if (signFloorY.HasValue && diff > SignTolerance) continue;
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        bestY = y;
                        found = true;
                    }
                }
            }

            if (found) return new FloorEstimate(bestY, FloorSource.Plane);
            if (signFloorY.HasValue) return new FloorEstimate(signFloorY.Value, FloorSource.Sign);
            return new FloorEstimate(cameraY - eyeHeight, FloorSource.EyeHeight);
        }
    }
}
