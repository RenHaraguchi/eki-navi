using System;
using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>
    /// 方位と水平の回転の計算。座標は x = 東、z = 北（Unity と同じ左手系）、方位は北から時計回りの度。
    /// 水平のベクトルは Vector2 の (x, y) に (x, z) を入れて扱う。
    /// server/app/alignment.py と同じ計算をする。
    /// </summary>
    public static class Azimuth
    {
        /// <summary>水平のベクトル (x, z) の方位。0 以上 360 未満。</summary>
        public static float Of(Vector2 xz)
        {
            double deg = Math.Atan2(xz.x, xz.y) * (180.0 / Math.PI);
            deg %= 360.0;
            if (deg < 0.0) deg += 360.0;
            return deg >= 360.0 ? 0f : (float)deg;
        }

        /// <summary>角度を -180 度より大きく 180 度以下にする。</summary>
        public static float Normalize(float deg)
        {
            double wrapped = ((deg + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
            return wrapped <= -180.0 ? 180f : (float)wrapped;
        }

        /// <summary>
        /// 水平のベクトル (x, z) を鉛直軸まわりに回す。正は上から見て時計回り。
        /// Unity の Quaternion.Euler(0, yawDeg, 0) * new Vector3(x, 0, z) と同じ。
        /// </summary>
        public static Vector2 RotateY(Vector2 xz, float yawDeg)
        {
            double rad = yawDeg * (Math.PI / 180.0);
            double cos = Math.Cos(rad), sin = Math.Sin(rad);
            return new Vector2(
                (float)(xz.x * cos + xz.y * sin),
                (float)(-xz.x * sin + xz.y * cos));
        }
    }
}
