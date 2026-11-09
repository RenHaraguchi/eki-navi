using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>
    /// 歩行者の推測航法。看板で合わせた位置と向きを起点に、一歩ごとに今の向きへ歩幅の分だけ進める。
    /// 座標は経路の座標（x = 東、z = 北、メートル）、向きは北から時計回りの度。
    ///
    /// ARCore の映像の追跡に頼らないので、地下で ARCore が位置を見失っても位置を出し続けられる。
    /// 歩幅の誤差（人や混み具合で 0.5〜0.8m）がたまるので、看板で合わせ直して位置を戻す。
    /// </summary>
    public class DeadReckoning
    {
        /// <summary>今の位置 (x, z)。</summary>
        public Vector2 Position { get; private set; }

        /// <summary>合わせてから進んだ歩数。</summary>
        public int Steps { get; private set; }

        /// <summary>合わせてから進んだ距離（m）。</summary>
        public float Distance { get; private set; }

        /// <summary>位置と向きの起点を決める（看板で合わせたとき）。</summary>
        public void Reset(Vector2 position)
        {
            Position = position;
            Steps = 0;
            Distance = 0f;
        }

        /// <summary>歩数と距離はそのままで、位置だけを動かす（経路に沿わせたとき）。</summary>
        public void MoveTo(Vector2 position)
        {
            Position = position;
        }

        /// <summary>
        /// 歩数を進める。
        /// </summary>
        /// <param name="steps">増えた歩数。</param>
        /// <param name="headingDeg">歩いている向き（北から時計回りの度）。</param>
        /// <param name="strideMeters">歩幅（m）。</param>
        public void Advance(int steps, float headingDeg, float strideMeters)
        {
            if (steps <= 0 || strideMeters <= 0f) return;
            float rad = headingDeg * Mathf.Deg2Rad;
            float d = steps * strideMeters;
            Position += new Vector2(Mathf.Sin(rad), Mathf.Cos(rad)) * d;
            Steps += steps;
            Distance += d;
        }

        /// <summary>
        /// スマホの姿勢から、歩いている向きとみなす水平の向き。
        /// 前に向けて持っていればカメラの向き、足元に向けていればスマホの上端の向き。
        /// </summary>
        /// <param name="forward">カメラの前の向き（ワールド）。</param>
        /// <param name="up">カメラの上の向き（ワールド）。</param>
        /// <param name="direction">水平の向き (x, z)、長さ 1。</param>
        public static bool TryWalkDirection(Vector3 forward, Vector3 up, out Vector2 direction)
        {
            Vector3 v = forward;
            if (forward.y < -0.75f) v = up;        // 足元に向けている。上端が進む向き
            else if (forward.y > 0.75f) v = -up;   // 真上に向けている。上端は後ろを向く
            direction = new Vector2(v.x, v.z);
            float length = direction.magnitude;
            if (length < 1e-3f) return false;
            direction /= length;
            return true;
        }
    }
}
