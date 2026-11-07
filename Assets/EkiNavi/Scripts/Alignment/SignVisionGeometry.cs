using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>撮った瞬間のカメラ。位置と向きは AR の座標、projection は Camera.projectionMatrix。</summary>
    public readonly struct CaptureView
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Matrix4x4 Projection;

        public CaptureView(Vector3 position, Quaternion rotation, Matrix4x4 projection)
        {
            Position = position;
            Rotation = rotation;
            Projection = projection;
        }
    }

    /// <summary>
    /// Gemini が答えた画像の中の枠から、案内板の AR 上の位置と向きを求める。
    ///
    /// 位置: 枠の中心を通る光線の上で、板の高さ（signs.json の heightMeters）が枠の高さに見える距離。
    /// 板を斜めから見ても縦の長さは変わらないので、横幅ではなく高さを使う。
    /// 向き: 正面から撮ったものとみなし、板からカメラへの水平の向きを板の法線にする。
    /// 縦の面（壁）が光線と交わっていれば、その面の位置と法線で置き換える（<see cref="TryRefineWithPlane"/>）。
    ///
    /// 座標は Unity に合わせて、x = 東、z = 北、y = 上。枠は画像の左上を原点とした 0〜1。
    /// 画像は画面と同じ向きと切り取り方で撮ったもの（ARCameraBackground の material で写したもの）とする。
    /// </summary>
    public static class SignVisionGeometry
    {
        /// <summary>これより近い・遠い答えは、枠がおかしいとみなして使わない（m）。</summary>
        public const float MinDistance = 0.3f;
        public const float MaxDistance = 30f;

        /// <summary>
        /// 枠の中心を通る光線（AR の座標）。
        /// </summary>
        /// <param name="view">撮った瞬間のカメラ。</param>
        /// <param name="centerX">枠の中心の x（左が 0、右が 1）。</param>
        /// <param name="centerY">枠の中心の y（上が 0、下が 1）。</param>
        public static Ray RayThrough(CaptureView view, float centerX, float centerY)
        {
            var local = LocalPoint(view.Projection, centerX, centerY, 1f);
            return new Ray(view.Position, view.Rotation * local.normalized);
        }

        /// <summary>
        /// 枠から板の位置と、正面から撮ったとみなした法線を求める。
        /// </summary>
        /// <param name="view">撮った瞬間のカメラ。</param>
        /// <param name="xMin">枠の左（0〜1）。</param>
        /// <param name="yMin">枠の上（0〜1、上が 0）。</param>
        /// <param name="xMax">枠の右。</param>
        /// <param name="yMax">枠の下。</param>
        /// <param name="signHeightMeters">板の縦の長さ（m）。</param>
        /// <param name="signPosition">板の中心（AR の座標）。</param>
        /// <param name="horizontalNormal">板の外向きの法線を水平にしたもの (x, z)。</param>
        /// <returns>距離がありうる範囲で、法線が決まったら true。</returns>
        public static bool TryLocate(
            CaptureView view,
            float xMin, float yMin, float xMax, float yMax,
            float signHeightMeters,
            out Vector3 signPosition,
            out Vector2 horizontalNormal)
        {
            signPosition = Vector3.zero;
            horizontalNormal = Vector2.zero;
            float boxHeight = yMax - yMin;
            if (boxHeight <= 0f || xMax <= xMin || signHeightMeters <= 0f) return false;

            // 画面の縦の長さは NDC で 2。板の高さ H が奥行き Z で見える NDC の高さは P11 * H / Z
            float depth = view.Projection.m11 * signHeightMeters / (2f * boxHeight);
            if (depth < MinDistance || depth > MaxDistance) return false;

            var local = LocalPoint(view.Projection, (xMin + xMax) * 0.5f, (yMin + yMax) * 0.5f, depth);
            signPosition = view.Position + view.Rotation * local;
            var toCamera = new Vector2(view.Position.x - signPosition.x, view.Position.z - signPosition.z);
            if (toCamera.magnitude < 1e-3f) return false;
            horizontalNormal = toCamera.normalized;
            return true;
        }

        /// <summary>
        /// 光線が縦の面と、見積もった距離の近くで交わるなら、その交点と面の法線を返す。
        /// 壁に貼った板なら、ARCore が見つけた壁の面のほうが、枠の大きさからの見積もりより確かなことが多い。
        /// </summary>
        /// <param name="ray">枠の中心を通る光線。</param>
        /// <param name="estimatedDistance">枠の大きさから見積もった光線の上の距離（m）。</param>
        /// <param name="planeCenter">面の中心。</param>
        /// <param name="planeNormal">面の法線（ARPlane の transform.up）。</param>
        /// <param name="planeRadius">面の中心から端までのおおよその長さ（m）。</param>
        /// <param name="tolerance">見積もりとの差をどこまで許すか（距離に対する割合）。</param>
        /// <param name="hit">交点。</param>
        /// <param name="horizontalNormal">カメラ側を向いた、水平にした面の法線 (x, z)。</param>
        public static bool TryRefineWithPlane(
            Ray ray,
            float estimatedDistance,
            Vector3 planeCenter,
            Vector3 planeNormal,
            float planeRadius,
            float tolerance,
            out Vector3 hit,
            out Vector2 horizontalNormal)
        {
            hit = Vector3.zero;
            horizontalNormal = Vector2.zero;
            if (!SignDetection.TryHorizontalNormal(planeNormal, out var n, 0.9f)) return false;

            var normal = new Vector3(n.x, 0f, n.y);
            float facing = Vector3.Dot(ray.direction, normal);
            if (Mathf.Abs(facing) < 0.2f) return false; // 面をかすめるように見ている
            float t = Vector3.Dot(planeCenter - ray.origin, normal) / facing;
            if (t <= 0f) return false;
            if (Mathf.Abs(t - estimatedDistance) > Mathf.Max(0.5f, estimatedDistance * tolerance)) return false;

            hit = ray.GetPoint(t);
            var offset = hit - planeCenter;
            offset.y = 0f;
            if (offset.magnitude > planeRadius + 0.5f) return false;

            // 法線はカメラ側を向ける
            horizontalNormal = facing > 0f ? -n : n;
            return true;
        }

        /// <summary>画像の点 (0〜1、上が 0) を、カメラから奥行き depth の位置にしたもの（カメラの local 座標、z が前）。</summary>
        static Vector3 LocalPoint(Matrix4x4 p, float x, float y, float depth)
        {
            float ndcX = 2f * x - 1f;
            float ndcY = 1f - 2f * y;
            // Unity の projection は view 空間の -z が前。clip.x = P00 x + P02 z_view、w = -z_view
            float localX = (ndcX + p.m02) * depth / p.m00;
            float localY = (ndcY + p.m12) * depth / p.m11;
            return new Vector3(localX, localY, depth);
        }
    }
}
