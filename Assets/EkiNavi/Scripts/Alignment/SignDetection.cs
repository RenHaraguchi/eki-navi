using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>
    /// 認識した案内板の姿勢から、位置合わせに使う値を取り出す。
    /// 設計書5章: ARCore の板の姿勢は +Y が板面の法線（実機で軸を描画して確かめること）。
    /// 法線は水平に投影して方位だけ使い、上方向は重力で決める。
    /// </summary>
    public static class SignDetection
    {
        /// <summary>
        /// 板の法線を水平に投影して、長さ1の (x, z) にする。
        /// 板が床や天井と平行に近い（水平成分が minHorizontal より小さい）ときは使わない。
        /// </summary>
        /// <param name="normal">板の外向きの法線。ARTrackedImage なら transform.up（実機で要確認）。</param>
        /// <param name="horizontal">水平にした法線 (x, z)。</param>
        /// <param name="minHorizontal">法線の水平成分の下限。0.5 は板が鉛直から60度まで（仮）。</param>
        public static bool TryHorizontalNormal(Vector3 normal, out Vector2 horizontal, float minHorizontal = 0.5f)
        {
            var n = normal.normalized;
            var xz = new Vector2(n.x, n.z);
            float length = xz.magnitude;
            if (length < minHorizontal || length <= 0f)
            {
                horizontal = Vector2.zero;
                return false;
            }
            horizontal = xz / length;
            return true;
        }

        /// <summary>
        /// カメラが板の表側にいるか。裏側からの検出は採用しない（設計書5章）。
        /// 水平だけで見る。
        /// </summary>
        /// <param name="signPosition">板の中心（AR 座標）。</param>
        /// <param name="horizontalNormal">水平にした板の外向きの法線 (x, z)。</param>
        /// <param name="cameraPosition">カメラの位置（AR 座標）。</param>
        public static bool IsViewedFromFront(Vector3 signPosition, Vector2 horizontalNormal, Vector3 cameraPosition)
        {
            var toCamera = new Vector2(cameraPosition.x - signPosition.x, cameraPosition.z - signPosition.z);
            return Vector2.Dot(toCamera, horizontalNormal) > 0f;
        }
    }
}
