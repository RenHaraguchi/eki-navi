using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>
    /// 歩数から求めた距離と、ARCore のカメラが動いた距離を並べて記録するための計算（確認用）。
    /// - 歩数は MotionGuard の数え方をそのまま使う（加速度の山を一歩と数える）
    /// - AR の距離は、カメラの水平な位置が <see cref="minSegmentMeters"/> 以上動くたびに足していく
    ///   （止まっているときの小さな揺れを距離に入れないため）
    /// - 両方がたまったら、「AR の距離 ÷ 歩数」でその人の歩幅を求められる
    /// </summary>
    public class StepDistanceProbe
    {
        readonly float minSegmentMeters;
        Vector2? anchor;

        public StepDistanceProbe(float minSegmentMeters = 0.25f)
        {
            this.minSegmentMeters = minSegmentMeters;
        }

        /// <summary>計り始めてからの AR の移動距離（m）。</summary>
        public float ArDistance { get; private set; }

        /// <summary>計り始めたときの歩数（MotionGuard の歩数は通し番号なので、差を取る）。</summary>
        public int StepsAtReset { get; private set; }

        public void Reset(Vector2 cameraPosition, int stepsNow)
        {
            anchor = cameraPosition;
            ArDistance = 0f;
            StepsAtReset = stepsNow;
        }

        /// <summary>カメラの水平な位置を渡す。追跡が止まっているときは呼ばない。</summary>
        public void AddPosition(Vector2 cameraPosition)
        {
            if (anchor == null)
            {
                anchor = cameraPosition;
                return;
            }
            float d = Vector2.Distance(anchor.Value, cameraPosition);
            if (d < minSegmentMeters) return;
            ArDistance += d;
            anchor = cameraPosition;
        }

        /// <summary>追跡が止まってから戻ったとき、飛んだ分を距離に入れないよう、基準をいまの位置にする。</summary>
        public void Rebase(Vector2 cameraPosition) => anchor = cameraPosition;

        public int Steps(int stepsNow) => Mathf.Max(0, stepsNow - StepsAtReset);

        public float StepDistance(int stepsNow, float strideMeters) => Steps(stepsNow) * strideMeters;

        /// <summary>AR の距離から逆算した歩幅（m）。歩数が少ないうちは当てにならないので 0 を返す。</summary>
        public float FittedStride(int stepsNow, int minSteps = 20)
        {
            int steps = Steps(stepsNow);
            return steps < minSteps ? 0f : ArDistance / steps;
        }
    }
}
