using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>利用者の向きが、矢印の向きから大きくずれているかを決める。</summary>
    public static class FacingCheck
    {
        /// <summary>これ以上ずれたら「矢印の方向を向いてください」を出す（度）。</summary>
        public const float AwayDeg = 60f;
        /// <summary>これより戻ったら表示を消す（度）。境目で表示がちらつかないよう、出す角度より小さくする。</summary>
        public const float BackDeg = 50f;

        /// <summary>2 つの向き（度）の差の大きさ。0〜180。</summary>
        public static float ErrorDeg(float yawA, float yawB) => Mathf.Abs(Mathf.DeltaAngle(yawA, yawB));

        /// <summary>今回のずれから、向きがずれている状態かを決める。</summary>
        public static bool IsAway(bool wasAway, float errorDeg) => errorDeg >= AwayDeg || (wasAway && errorDeg > BackDeg);
    }
}
