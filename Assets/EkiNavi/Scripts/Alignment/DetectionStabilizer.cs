using System.Collections.Generic;
using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>
    /// 案内板の認識が落ち着くまで待って、位置合わせを確定してよいかを決める（設計書5章）。
    /// 一定の時間（既定 0.5 秒）続けて追跡でき、その間の位置のばらつきと方位のばらつきが
    /// 上限（既定 2cm、1度）以下になったら確定してよい。数値はすべて仮。
    ///
    /// 使い方: 板が追跡中（AR Foundation の TrackingState.Tracking）の間は毎フレーム <see cref="Add"/> を呼ぶ。
    /// 追跡中でなくなったら <see cref="Reset"/> を呼ぶ。イベントの更新だけで呼ぶと間が空くことがあるので、
    /// Update で毎フレーム呼ぶ。
    /// </summary>
    public class DetectionStabilizer
    {
        readonly float windowSeconds;
        readonly float maxPositionSpread;
        readonly float maxAzimuthSpreadDeg;
        readonly float maxGapSeconds;
        readonly int minSamples;

        readonly List<Sample> samples = new List<Sample>();
        float since = float.NaN;

        /// <param name="windowSeconds">続けて追跡できている必要がある時間（秒）。</param>
        /// <param name="maxPositionSpread">平均からの位置のずれの上限（メートル、水平）。</param>
        /// <param name="maxAzimuthSpreadDeg">平均からの方位のずれの上限（度）。</param>
        /// <param name="maxGapSeconds">これより間が空いたら、続いていないとみなして数え直す（秒）。</param>
        /// <param name="minSamples">判定に使う最小の件数。</param>
        public DetectionStabilizer(
            float windowSeconds = 0.5f,
            float maxPositionSpread = 0.02f,
            float maxAzimuthSpreadDeg = 1f,
            float maxGapSeconds = 0.2f,
            int minSamples = 5)
        {
            this.windowSeconds = windowSeconds;
            this.maxPositionSpread = maxPositionSpread;
            this.maxAzimuthSpreadDeg = maxAzimuthSpreadDeg;
            this.maxGapSeconds = maxGapSeconds;
            this.minSamples = minSamples;
        }

        /// <summary>確定してよいか。</summary>
        public bool IsStable { get; private set; }

        /// <summary>直近の時間の、板の位置 (x, z) の平均。</summary>
        public Vector2 MeanPosition { get; private set; }

        /// <summary>直近の時間の、板の法線の方位の平均。0 以上 360 未満。</summary>
        public float MeanAzimuthDeg { get; private set; }

        /// <summary>水平にした法線 (x, z) から作った、平均の方位の向き。<see cref="SignAlignment.FromDetection"/> にそのまま渡せる。</summary>
        public Vector2 MeanNormal
        {
            get
            {
                float rad = MeanAzimuthDeg * Mathf.Deg2Rad;
                return new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
            }
        }

        /// <summary>追跡が止まったときに呼ぶ。</summary>
        public void Reset()
        {
            samples.Clear();
            since = float.NaN;
            IsStable = false;
        }

        /// <summary>
        /// 1フレーム分の認識結果を足す。
        /// </summary>
        /// <param name="time">時刻（秒）。Time.time など。</param>
        /// <param name="position">板の位置 (x, z)。</param>
        /// <param name="horizontalNormal">水平にした板の法線 (x, z)。</param>
        /// <returns>確定してよいか。</returns>
        public bool Add(float time, Vector2 position, Vector2 horizontalNormal)
        {
            if (samples.Count > 0 && time - samples[samples.Count - 1].Time > maxGapSeconds) Reset();
            if (float.IsNaN(since)) since = time;

            samples.Add(new Sample(time, position, Azimuth.Of(horizontalNormal)));
            while (samples.Count > 0 && samples[0].Time < time - windowSeconds) samples.RemoveAt(0);

            Evaluate(time);
            return IsStable;
        }

        void Evaluate(float now)
        {
            // 方位は 0 度をまたぐことがあるので、最新の値からの差で平均を取る
            float reference = samples[samples.Count - 1].AzimuthDeg;
            var sumPosition = Vector2.zero;
            float sumDelta = 0f;
            foreach (var s in samples)
            {
                sumPosition += s.Position;
                sumDelta += Azimuth.Normalize(s.AzimuthDeg - reference);
            }
            MeanPosition = sumPosition / samples.Count;
            float meanDelta = sumDelta / samples.Count;
            MeanAzimuthDeg = Azimuth.Of(Azimuth.RotateY(new Vector2(0f, 1f), reference + meanDelta));

            float positionSpread = 0f, azimuthSpread = 0f;
            foreach (var s in samples)
            {
                positionSpread = Mathf.Max(positionSpread, (s.Position - MeanPosition).magnitude);
                azimuthSpread = Mathf.Max(azimuthSpread, Mathf.Abs(Azimuth.Normalize(s.AzimuthDeg - reference) - meanDelta));
            }

            IsStable = now - since >= windowSeconds
                && samples.Count >= minSamples
                && positionSpread <= maxPositionSpread
                && azimuthSpread <= maxAzimuthSpreadDeg;
        }

        readonly struct Sample
        {
            public readonly float Time;
            public readonly Vector2 Position;
            public readonly float AzimuthDeg;

            public Sample(float time, Vector2 position, float azimuthDeg)
            {
                Time = time;
                Position = position;
                AzimuthDeg = azimuthDeg;
            }
        }
    }
}
