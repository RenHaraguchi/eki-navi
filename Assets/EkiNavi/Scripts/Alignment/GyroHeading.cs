using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>
    /// ジャイロの値から、水平の向き（方位、北から時計回りの度）を積み上げる。
    /// ARCore の映像の追跡に頼らないので、ARCore が位置を見失っても向きを追い続けられる。
    ///
    /// 重力の向きのまわりの回転の速さだけを使うので、スマホを下に向けても前に向けても同じに扱える。
    /// 地磁気は使わない（地下は鉄骨で狂いやすい）ので、絶対の方位は分からない。
    /// 看板を Scan した瞬間に <see cref="Reset"/> で地図の上の向きを入れ、そこからの変化を足していく。
    ///
    /// 端末によって、センサーの座標の向き（右手系か左手系か）が違うと回る向きが逆になるので、
    /// <see cref="Sign"/> で合わせる（確認用の SensorDiagnostics で、ARCore の向きと比べて決める）。
    /// </summary>
    public class GyroHeading
    {
        /// <summary>今の方位（度、0 以上 360 未満、北から時計回り）。</summary>
        public float HeadingDeg { get; private set; }

        /// <summary>回転の速さに掛ける符号（+1 か -1）。</summary>
        public float Sign { get; set; }

        public GyroHeading(float sign = 1f)
        {
            Sign = sign >= 0f ? 1f : -1f;
        }

        /// <summary>今の方位を決める（看板で合わせたとき）。</summary>
        public void Reset(float headingDeg) => HeadingDeg = Wrap360(headingDeg);

        /// <summary>
        /// 重力の向きのまわりの回転の速さ（度/秒）。重力が取れないときは 0。
        /// </summary>
        /// <param name="angularVelocity">ジャイロの値（ラジアン/秒、端末の座標）。</param>
        /// <param name="gravity">重力の向き（端末の座標。大きさは問わない）。</param>
        public static float YawRateDeg(Vector3 angularVelocity, Vector3 gravity)
        {
            float g = gravity.magnitude;
            if (g < 1e-3f) return 0f;
            return Vector3.Dot(angularVelocity, gravity / g) * Mathf.Rad2Deg;
        }

        /// <summary>1フレーム分の回転を足す。</summary>
        /// <param name="angularVelocity">ジャイロの値（ラジアン/秒、端末の座標）。</param>
        /// <param name="gravity">重力の向き（端末の座標）。</param>
        /// <param name="dt">前のフレームからの時間（秒）。</param>
        public void Update(Vector3 angularVelocity, Vector3 gravity, float dt)
        {
            if (dt <= 0f || dt > 0.5f) return; // 一時停止から戻ったときなどの大きな間は足さない
            HeadingDeg = Wrap360(HeadingDeg + Sign * YawRateDeg(angularVelocity, gravity) * dt);
        }

        /// <summary>角度を 0 以上 360 未満にする。</summary>
        public static float Wrap360(float deg)
        {
            float d = deg % 360f;
            return d < 0f ? d + 360f : d;
        }
    }
}
