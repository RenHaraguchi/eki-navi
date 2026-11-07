using System.Collections.Generic;
using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>MotionGuard の判定の数値。どれも仮の値で、現地で調整する。</summary>
    [System.Serializable]
    public class MotionGuardSettings
    {
        [Tooltip("揺れを見る時間（秒）")]
        public float varianceWindowSeconds = 0.6f;

        [Tooltip("加速度の揺れ（標準偏差、g）がこれより小さい状態が続いたら止まっているとみなす")]
        public float stillThresholdG = 0.025f;

        [Tooltip("揺れがこれより大きくなったら歩き出したとみなす（止まっている判定から戻る値）")]
        public float movingThresholdG = 0.05f;

        [Tooltip("止まっているとみなすまでに、揺れが小さい状態が続く時間（秒）")]
        public float stillHoldSeconds = 0.3f;

        [Tooltip("一歩と数える、加速度の山の高さ（重力を除いた分、g）")]
        public float stepThresholdG = 0.08f;

        [Tooltip("一歩と次の一歩の間の最短の時間（秒）")]
        public float minStepIntervalSeconds = 0.3f;

        [Tooltip("一歩の長さ（m）。人や混み具合で 0.5〜0.8m くらい変わる")]
        public float strideMeters = 0.65f;

        [Tooltip("歩く速さを、直近この秒数の歩数から求める（秒）")]
        public float plausibilityWindowSeconds = 1f;

        [Tooltip("歩数から求めた速さの何倍までを、ありうる速さとみなすか")]
        public float allowedRatio = 1.5f;

        [Tooltip("歩数から求めた速さに足す余裕（m/秒）。端末の揺れや、歩き出しの数え漏れの分")]
        public float allowedSlackMetersPerSecond = 1f;

        [Tooltip("ずれを打ち消したあと、ずれの知らせを出しておく時間（秒）")]
        public float driftNoticeSeconds = 3f;
    }

    /// <summary>
    /// 加速度センサーで「止まっているか」と歩数を数え、ARCore の移動量がありえないときに打ち消す量を返す。
    ///
    /// ARCore はカメラの映像の特徴点とセンサーから自分の位置を推定するので、
    /// 歩く人が画面を占めると、自分が止まっていても動いたと判断することがある。
    /// 加速度センサーは映像を使わないので、人の動きに引っ張られない。
    ///
    /// - 止まっている間に ARCore が動いたと言ったら、その分をすべて打ち消す
    /// - 歩いている間は、ARCore の速さが「直近1秒の歩数 × 歩幅 × 1.5 + 1m」（m/秒）を超えた分を打ち消す。
    ///   人に引っ張られて急に横へ飛ぶような動きを抑える。ゆっくり少しずつずれるものは打ち消せない
    ///
    /// 返す量は、地図の線（経路）を同じだけ動かして、自分との位置関係を保つためのもの（水平の x, z、メートル）。
    /// 向きのずれは打ち消さない（向きは主にジャイロで測られていて、人の動きに引っ張られにくい）。
    /// </summary>
    public class MotionGuard
    {
        readonly MotionGuardSettings settings;

        readonly Queue<(float time, float magnitude)> samples = new Queue<(float, float)>();
        float sum, sumSquares;

        readonly Queue<(float time, Vector2 position, int steps)> history = new Queue<(float, Vector2, int)>();

        float lowStdSince = -1f;
        float baseline = -1f;
        bool aboveStepThreshold;
        float lastStepTime = float.NegativeInfinity;
        Vector2 lastPosition;
        float lastTime;
        bool hasLastPosition;
        float driftUntil = float.NegativeInfinity;

        public MotionGuard(MotionGuardSettings settings = null)
        {
            this.settings = settings ?? new MotionGuardSettings();
        }

        /// <summary>止まっているとみなしているか。</summary>
        public bool IsStill { get; private set; }

        /// <summary>数えた歩数（Reset からの合計）。</summary>
        public int Steps { get; private set; }

        /// <summary>直近の加速度の揺れ（標準偏差、g）。</summary>
        public float AccelerationStdG { get; private set; }

        /// <summary>直近 1 秒の ARCore の移動量（m）。</summary>
        public float RecentArDistance { get; private set; }

        /// <summary>歩数からありうるとみなした速さ（m/秒）。止まっているときは 0。</summary>
        public float AllowedSpeed { get; private set; }

        /// <summary>Reset から打ち消した量の合計（m）。</summary>
        public float TotalCompensatedMeters { get; private set; }

        /// <summary>止まっているとき以外で、歩数と合わない移動を打ち消してから数秒以内か。合わせ直しを促すのに使う。</summary>
        public bool DriftSuspected(float time) => time < driftUntil;

        /// <summary>
        /// 打ち消しの基準をいまの位置にする。経路を合わせ直したときに呼ぶ。
        /// 止まっているかの判定と歩数の数え方は続ける。
        /// </summary>
        public void Reset(Vector2 cameraPosition)
        {
            history.Clear();
            lastPosition = cameraPosition;
            hasLastPosition = true;
            Steps = 0;
            TotalCompensatedMeters = 0f;
            driftUntil = float.NegativeInfinity;
        }

        /// <summary>
        /// 1フレーム分の値を入れ、経路を動かす量を返す。
        /// </summary>
        /// <param name="time">時刻（秒）。</param>
        /// <param name="accelerationMagnitudeG">加速度センサーの値の大きさ（重力を含む、g）。止まっていれば約 1。</param>
        /// <param name="cameraPosition">ARCore が推定したカメラの水平の位置 (x, z)。</param>
        /// <returns>経路の位置に足す量 (x, z)。打ち消さないときは 0。</returns>
        public Vector2 Update(float time, float accelerationMagnitudeG, Vector2 cameraPosition)
        {
            UpdateStillness(time, accelerationMagnitudeG);
            UpdateSteps(time, accelerationMagnitudeG);

            if (!hasLastPosition)
            {
                lastPosition = cameraPosition;
                lastTime = time;
                hasLastPosition = true;
            }
            var frameDelta = cameraPosition - lastPosition;
            float dt = Mathf.Max(0f, time - lastTime);
            lastPosition = cameraPosition;
            lastTime = time;

            history.Enqueue((time, cameraPosition, Steps));
            while (history.Count > 1 && time - history.Peek().time > settings.plausibilityWindowSeconds) history.Dequeue();
            var oldest = history.Peek();

            RecentArDistance = (cameraPosition - oldest.position).magnitude;
            int recentSteps = Steps - oldest.steps;
            AllowedSpeed = IsStill
                ? 0f
                : recentSteps * settings.strideMeters * settings.allowedRatio / settings.plausibilityWindowSeconds
                  + settings.allowedSlackMetersPerSecond;

            // このフレームで動いてよい量を超えた分を打ち消す
            float moved = frameDelta.magnitude;
            float allowed = AllowedSpeed * dt;
            var compensation = Vector2.zero;
            if (moved > allowed && moved > 1e-6f)
            {
                compensation = frameDelta * ((moved - allowed) / moved);
                if (!IsStill) driftUntil = time + settings.driftNoticeSeconds;
            }
            TotalCompensatedMeters += compensation.magnitude;
            return compensation;
        }

        void UpdateStillness(float time, float magnitude)
        {
            samples.Enqueue((time, magnitude));
            sum += magnitude;
            sumSquares += magnitude * magnitude;
            while (samples.Count > 1 && time - samples.Peek().time > settings.varianceWindowSeconds)
            {
                var old = samples.Dequeue();
                sum -= old.magnitude;
                sumSquares -= old.magnitude * old.magnitude;
            }

            int n = samples.Count;
            float mean = sum / n;
            AccelerationStdG = Mathf.Sqrt(Mathf.Max(0f, sumSquares / n - mean * mean));

            // 揺れを見る時間の半分もたまっていないうちは判定しない
            float span = time - samples.Peek().time;
            if (span < settings.varianceWindowSeconds * 0.5f)
            {
                IsStill = false;
                lowStdSince = -1f;
                return;
            }

            if (IsStill)
            {
                if (AccelerationStdG > settings.movingThresholdG)
                {
                    IsStill = false;
                    lowStdSince = -1f;
                }
                return;
            }

            if (AccelerationStdG < settings.stillThresholdG)
            {
                if (lowStdSince < 0f) lowStdSince = time;
                if (time - lowStdSince >= settings.stillHoldSeconds) IsStill = true;
            }
            else
            {
                lowStdSince = -1f;
            }
        }

        void UpdateSteps(float time, float magnitude)
        {
            // 重力の分をゆっくり追う平均で引き、一歩ごとの山を数える
            if (baseline < 0f) baseline = magnitude;
            baseline += (magnitude - baseline) * 0.02f;
            float d = magnitude - baseline;

            if (!aboveStepThreshold && d > settings.stepThresholdG)
            {
                aboveStepThreshold = true;
                if (time - lastStepTime >= settings.minStepIntervalSeconds)
                {
                    Steps++;
                    lastStepTime = time;
                }
            }
            else if (aboveStepThreshold && d < 0f)
            {
                aboveStepThreshold = false;
            }
        }
    }
}
