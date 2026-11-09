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
    /// Gemini が答えた画像の中の板の位置（四隅か外枠）から、案内板の AR 上の位置と向きを求める。
    ///
    /// 基本の考え方: 板の縦の辺は重力と平行なので、辺の上端と下端が見える角度（仰角）の差と、
    /// 板の縦の長さ（signs.json の heightMeters）から、その辺までの水平の距離が決まる。
    /// カメラを上下に傾けていても、斜めから見ていても成り立つ。
    ///
    /// - 四隅があるとき（<see cref="TryLocateFromCorners"/>）: 左右の辺の位置をそれぞれ求め、
    ///   2本を結ぶ向きから板の向きを決める。正面から撮らなくてよい
    /// - 外枠しかないとき（<see cref="TryLocate"/>）: 外枠の中央の縦の線で位置を求め、
    ///   向きは正面から撮ったとみなす
    /// - 縦の面（壁）が光線と交わっていれば、その面の位置と法線で置き換えられる（<see cref="TryRefineWithPlane"/>）
    ///
    /// 座標は Unity に合わせて、x = 東、z = 北、y = 上。画像の点は左上を原点とした 0〜1。
    /// 画像は画面と同じ向きと切り取り方で撮ったもの（ARCameraBackground の material で写したもの）とする。
    /// </summary>
    public static class SignVisionGeometry
    {
        /// <summary>これより近い・遠い答えは、四隅や枠がおかしいとみなして使わない（m）。</summary>
        public const float MinDistance = 0.3f;
        public const float MaxDistance = 30f;

        /// <summary>見えている横幅（正面換算）が登録した横幅のこの範囲を外れたら、四隅を信用しない。下限はこの半分で使う（斜め 70 度ほど）。</summary>
        public const float MinWidthRatio = 0.6f;
        public const float MaxWidthRatio = 1.6f;

        /// <summary>画像の点 (x, y) を通る光線（AR の座標）。</summary>
        public static Ray RayThrough(CaptureView view, float x, float y)
        {
            var local = LocalDirection(view.Projection, x, y);
            return new Ray(view.Position, view.Rotation * local.normalized);
        }

        /// <summary>
        /// 縦の線（上端と下端の画像の点）が、縦の長さ height の線として見えるときの、その線の中心（AR の座標）。
        /// </summary>
        public static bool TryLocateVerticalEdge(
            CaptureView view, Vector2 top, Vector2 bottom, float height, out Vector3 center)
        {
            center = Vector3.zero;
            if (height <= 0f) return false;
            var up = RayThrough(view, top.x, top.y).direction;
            var down = RayThrough(view, bottom.x, bottom.y).direction;
            var upXZ = new Vector2(up.x, up.z);
            var downXZ = new Vector2(down.x, down.z);
            if (upXZ.magnitude < 1e-4f || downXZ.magnitude < 1e-4f) return false; // 真上か真下を見ている

            float tanTop = up.y / upXZ.magnitude;
            float tanBottom = down.y / downXZ.magnitude;
            float gap = tanTop - tanBottom;
            if (gap <= 1e-5f) return false; // 上端が下端より下に見えている
            float distance = height / gap;
            if (distance < MinDistance || distance > MaxDistance) return false;

            var direction = (upXZ.normalized + downXZ.normalized).normalized;
            float y = distance * (tanTop + tanBottom) * 0.5f;
            center = view.Position + new Vector3(direction.x * distance, y, direction.y * distance);
            return true;
        }

        /// <summary>
        /// 外枠から板の位置を求め、向きは正面から撮ったとみなす。
        /// </summary>
        /// <param name="view">撮った瞬間のカメラ。</param>
        /// <param name="xMin">枠の左（0〜1）。</param>
        /// <param name="yMin">枠の上（0〜1、上が 0）。</param>
        /// <param name="xMax">枠の右。</param>
        /// <param name="yMax">枠の下。</param>
        /// <param name="signHeightMeters">板の縦の長さ（m）。</param>
        /// <param name="signPosition">板の中心（AR の座標）。</param>
        /// <param name="horizontalNormal">板の外向きの法線を水平にしたもの (x, z)。</param>
        public static bool TryLocate(
            CaptureView view,
            float xMin, float yMin, float xMax, float yMax,
            float signHeightMeters,
            out Vector3 signPosition,
            out Vector2 horizontalNormal)
        {
            signPosition = Vector3.zero;
            horizontalNormal = Vector2.zero;
            if (yMax <= yMin || xMax <= xMin) return false;
            float cx = (xMin + xMax) * 0.5f;
            return TryLocateFromVerticalLine(view, new Vector2(cx, yMin), new Vector2(cx, yMax), signHeightMeters,
                out signPosition, out horizontalNormal);
        }

        /// <summary>
        /// 板の中央の縦の線（上端と下端の画像の点）から板の位置を求め、向きは正面から撮ったとみなす。
        /// 端末を横に倒して撮ると、板の縦の線は画像の中で横に見えるので、外枠ではなく2点で渡す。
        /// </summary>
        public static bool TryLocateFromVerticalLine(
            CaptureView view, Vector2 top, Vector2 bottom, float signHeightMeters,
            out Vector3 signPosition, out Vector2 horizontalNormal)
        {
            horizontalNormal = Vector2.zero;
            if (!TryLocateVerticalEdge(view, top, bottom, signHeightMeters, out signPosition)) return false;
            return TryFacingCamera(view.Position, signPosition, out horizontalNormal);
        }

        /// <summary>
        /// 四隅から板の位置と向きを求める。斜めから撮ってもよい。
        ///
        /// 左右の辺の距離の差から向きを出すと、四隅が数 px ずれただけで向きが大きくずれる（5m 先で 3px なら最大 17 度）。
        /// そこで、次のように分けて求める。
        /// - 距離: 左右の辺の距離の平均（ずれが打ち消し合う）
        /// - 斜めの角度の大きさ: 左右の辺が見える向きの開きと、登録した横幅から（横幅が縮んで見える分）
        /// - 斜めの向き（左右どちらから見ているか）: 近い辺ほど長く見えるので、どちらの辺が長いか。
        ///   長さの差が minEdgeDifference より小さいときは言い切れないので、正面から撮ったとみなす
        ///   （逆を選ぶと誤差が斜めの角度の2倍になるが、正面とみなせば斜めの角度の分で済む）
        /// </summary>
        /// <param name="view">撮った瞬間のカメラ。</param>
        /// <param name="topLeft">板の左上の角（画像の 0〜1）。</param>
        /// <param name="topRight">右上。</param>
        /// <param name="bottomRight">右下。</param>
        /// <param name="bottomLeft">左下。</param>
        /// <param name="signWidthMeters">板の横の長さ（m）。</param>
        /// <param name="signHeightMeters">板の縦の長さ（m）。</param>
        /// <param name="signPosition">板の中心（AR の座標）。</param>
        /// <param name="horizontalNormal">カメラ側を向いた、板の法線を水平にしたもの (x, z)。</param>
        /// <param name="widthRatio">見えている横幅 ÷ 正面から見たときの横幅。1 なら正面、小さいほど斜め。</param>
        /// <param name="sideKnown">左右どちらから見ているかを決められたか。false なら向きは正面とみなした値。</param>
        /// <param name="minEdgeDifference">左右の辺の長さの差として信用できる最小の値（画像の縦に対する割合）。
        /// 左右の辺までの距離の差がこれに見合わなければ、左右を決めない。
        /// 既定の 0.006 は、縦 1920px の画像で約 12px（四隅がそれぞれ数 px ずれても逆にならない差）。</param>
        public static bool TryLocateFromCorners(
            CaptureView view,
            Vector2 topLeft, Vector2 topRight, Vector2 bottomRight, Vector2 bottomLeft,
            float signWidthMeters, float signHeightMeters,
            out Vector3 signPosition,
            out Vector2 horizontalNormal,
            out float widthRatio,
            out bool sideKnown,
            float minEdgeDifference = 0.006f)
        {
            signPosition = Vector3.zero;
            horizontalNormal = Vector2.zero;
            widthRatio = 0f;
            sideKnown = false;
            if (signWidthMeters <= 0f) return false;
            if (!TryLocateVerticalEdge(view, topLeft, bottomLeft, signHeightMeters, out var left)) return false;
            if (!TryLocateVerticalEdge(view, topRight, bottomRight, signHeightMeters, out var right)) return false;

            var c = new Vector2(view.Position.x, view.Position.z);
            var toLeft = new Vector2(left.x, left.z) - c;
            var toRight = new Vector2(right.x, right.z) - c;
            float dLeft = toLeft.magnitude, dRight = toRight.magnitude;
            if (dLeft < 1e-3f || dRight < 1e-3f) return false;
            var uLeft = toLeft / dLeft;
            var uRight = toRight / dRight;
            float cos = Mathf.Clamp(Vector2.Dot(uLeft, uRight), -1f, 1f);
            if (cos <= 0f) return false; // 90 度以上開いて見えるのはおかしい

            // 左右の辺までの距離を dm ∓ s/2 とおき、2点の間が横幅 W になる s を求める
            // W² = dL² + dR² − 2 dL dR cos φ  →  s² = (W² − 2 dm² (1 − cos φ)) · 2 / (1 + cos φ)
            float dm = (dLeft + dRight) * 0.5f;
            float w = signWidthMeters;
            float apparent = dm * Mathf.Sqrt(2f * (1f - cos)); // 正面から見たときの横幅として見えている長さ
            widthRatio = apparent / w;
            if (widthRatio < MinWidthRatio * 0.5f || widthRatio > MaxWidthRatio) return false;
            float s2 = (w * w - 2f * dm * dm * (1f - cos)) * 2f / (1f + cos);
            float s = s2 > 0f ? Mathf.Sqrt(s2) : 0f; // 横幅より広く見えるときは、正面とみなす
            // 辺の見かけの長さ h に対して、四隅のずれ δ は距離に δ/h の割合で効く。
            // 左右の距離の差がその割合より小さければ、どちらが手前か言い切れない
            // 辺の長さは画像の縦に対する割合で測る。端末を横に倒して撮ると縦の辺が画像の横向きに見えるので、
            // 縦の差だけでなく、横の差も縦横比で直して足す
            float aspect = AspectOf(view.Projection);
            float edgeHeight = (ImageLength(topLeft, bottomLeft, aspect) + ImageLength(topRight, bottomRight, aspect)) * 0.5f;
            float relativeDifference = (dLeft - dRight) / dm;
            sideKnown = edgeHeight > 0f && Mathf.Abs(relativeDifference) >= minEdgeDifference / edgeHeight;
            if (!sideKnown) s = 0f; // 左右を決められないので、正面とみなす
            else if (relativeDifference > 0f) s = -s; // 右の辺のほうが近いなら、右が手前（s = 右 − 左の距離が負）

            var leftPoint = c + uLeft * (dm - s * 0.5f);
            var rightPoint = c + uRight * (dm + s * 0.5f);
            var along = rightPoint - leftPoint;
            if (along.magnitude < 1e-4f) return false;

            var center = (leftPoint + rightPoint) * 0.5f;
            signPosition = new Vector3(center.x, (left.y + right.y) * 0.5f, center.y);
            var normal = new Vector2(along.y, -along.x).normalized;
            if (Vector2.Dot(normal, c - center) < 0f) normal = -normal;
            horizontalNormal = normal;
            return true;
        }

        /// <summary>
        /// 光線が縦の面と、見積もった距離の近くで交わるなら、その交点と面の法線を返す。
        /// 壁に貼った板なら、ARCore が見つけた壁の面のほうが、画像から求めた値より確かなことが多い。
        /// </summary>
        /// <param name="ray">板の中心を通る光線。</param>
        /// <param name="estimatedDistance">画像から見積もった光線の上の距離（m）。</param>
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

        /// <summary>画像の横 ÷ 縦（projection から）。</summary>
        static float AspectOf(Matrix4x4 projection)
        {
            return Mathf.Abs(projection.m00) > 1e-6f ? projection.m11 / projection.m00 : 1f;
        }

        /// <summary>画像の2点の間の長さ（画像の縦を 1 とした割合）。</summary>
        static float ImageLength(Vector2 a, Vector2 b, float aspect)
        {
            float dx = (b.x - a.x) * aspect, dy = b.y - a.y;
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        static bool TryFacingCamera(Vector3 cameraPosition, Vector3 signPosition, out Vector2 horizontalNormal)
        {
            var toCamera = new Vector2(cameraPosition.x - signPosition.x, cameraPosition.z - signPosition.z);
            horizontalNormal = toCamera.magnitude < 1e-3f ? Vector2.zero : toCamera.normalized;
            return horizontalNormal != Vector2.zero;
        }

        /// <summary>画像の点 (0〜1、上が 0) の方向（カメラの local 座標、z が前、奥行き 1）。</summary>
        /// <summary>
        /// 四隅の4点すべてが最もよく重なる、板の位置と向きを最小二乗で求める（カメラの位置と向きは ARCore の値で決まっている）。
        ///
        /// 未知数は板の中心 (x, y, z) と、水平な法線の向き（板は重力に対して立っているとみなす）の4つ。
        /// 四隅の8つの座標との差（画像の縦を 1 とした長さ）の二乗和を、ガウス・ニュートン法で小さくする。
        /// 左右の辺の長さの差だけで向きを決める <see cref="TryLocateFromCorners"/> と違い、上下の辺の傾き（遠近）も使うので、
        /// 斜めの角度が小さいときでも、どちら側から見ているかを決めやすい。
        /// 初めの値は TryLocateFromCorners の答えと、それをカメラへの向きで折り返したものの2つから始め、残差の小さいほうを選ぶ。
        /// </summary>
        /// <param name="rmsError">四隅の残差の二乗平均の平方根（画像の縦に対する割合）。</param>
        /// <param name="sideConfidence">もう一方の向きの残差 ÷ 選んだ向きの残差。大きいほど、向きの左右が確か。</param>
        public static bool TryFitRectangle(
            CaptureView view,
            Vector2 topLeft, Vector2 topRight, Vector2 bottomRight, Vector2 bottomLeft,
            float signWidthMeters, float signHeightMeters,
            Vector3 initialPosition, Vector2 initialNormal,
            out Vector3 signPosition, out Vector2 horizontalNormal, out float rmsError, out float sideConfidence)
        {
            signPosition = initialPosition;
            horizontalNormal = initialNormal;
            rmsError = float.MaxValue;
            sideConfidence = 1f;
            if (signWidthMeters <= 0f || signHeightMeters <= 0f || initialNormal.sqrMagnitude < 1e-8f) return false;
            var observed = new[] { topLeft, topRight, bottomRight, bottomLeft };
            float aspect = AspectOf(view.Projection);

            // 初めの向き: そのまま と、カメラへの向きで折り返したもの
            var c = new Vector2(view.Position.x, view.Position.z);
            var toCamera = (c - new Vector2(initialPosition.x, initialPosition.z)).normalized;
            var n0 = initialNormal.normalized;
            var mirrored = (2f * Vector2.Dot(n0, toCamera) * toCamera - n0).normalized;

            bool ok = false;
            double bestRms = double.MaxValue, otherRms = double.MaxValue;
            Vector2 otherNormal = Vector2.zero;
            foreach (var start in new[] { n0, mirrored })
            {
                if (!Fit(view, observed, aspect, signWidthMeters, signHeightMeters, initialPosition, start,
                        out var position, out var normal, out double rms)) continue;
                if (rms < bestRms)
                {
                    otherRms = bestRms;
                    otherNormal = horizontalNormal;
                    bestRms = rms;
                    signPosition = position;
                    horizontalNormal = normal;
                    ok = true;
                }
                else if (rms < otherRms)
                {
                    otherRms = rms;
                    otherNormal = normal;
                }
            }
            if (!ok) return false;
            // 板の表がカメラを向いていなければ、おかしな答え
            var center = new Vector2(signPosition.x, signPosition.z);
            if (Vector2.Dot(horizontalNormal, c - center) <= 0f) return false;
            float distance = Vector3.Distance(view.Position, signPosition);
            if (distance < MinDistance || distance > MaxDistance) return false;
            rmsError = (float)bestRms;
            // 2つの初めの値から同じ答えに着いたなら、もう一方の向きの答えはない（左右は確か）
            bool sameAnswer = otherRms < double.MaxValue && Vector2.Angle(otherNormal, horizontalNormal) < 5f;
            sideConfidence = otherRms >= double.MaxValue || sameAnswer
                ? float.MaxValue
                : (float)(otherRms / System.Math.Max(bestRms, 1e-9));
            return true;
        }

        static bool Fit(CaptureView view, Vector2[] observed, float aspect, float width, float height,
            Vector3 startPosition, Vector2 startNormal, out Vector3 position, out Vector2 normal, out double rms)
        {
            // p = (x, y, z, 法線の方位 θ)。法線 = (sin θ, cos θ)
            var p = new double[] { startPosition.x, startPosition.y, startPosition.z, System.Math.Atan2(startNormal.x, startNormal.y) };
            double lambda = 1e-3;
            double cost = Cost(view, observed, aspect, width, height, p, out var r);
            for (int iteration = 0; iteration < 50 && cost < double.MaxValue; iteration++)
            {
                // 数値微分のヤコビ行列（8×4）
                var J = new double[8, 4];
                for (int k = 0; k < 4; k++)
                {
                    var q = (double[])p.Clone();
                    double h = k < 3 ? 1e-4 : 1e-5;
                    q[k] += h;
                    Cost(view, observed, aspect, width, height, q, out var rq);
                    if (rq == null) return Fail(out position, out normal, out rms);
                    for (int i = 0; i < 8; i++) J[i, k] = (rq[i] - r[i]) / h;
                }
                // (JᵀJ + λ diag) δ = −Jᵀ r（レーベンバーグ・マーカート）
                var A = new double[4, 4];
                var g = new double[4];
                for (int a = 0; a < 4; a++)
                {
                    for (int b = 0; b < 4; b++)
                        for (int i = 0; i < 8; i++) A[a, b] += J[i, a] * J[i, b];
                    for (int i = 0; i < 8; i++) g[a] -= J[i, a] * r[i];
                }
                bool improved = false;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    var M = (double[,])A.Clone();
                    for (int a = 0; a < 4; a++) M[a, a] += lambda * (A[a, a] + 1e-9);
                    if (!Solve4(M, g, out var delta)) break;
                    var q = new double[4];
                    for (int a = 0; a < 4; a++) q[a] = p[a] + delta[a];
                    double c2 = Cost(view, observed, aspect, width, height, q, out var r2);
                    if (c2 < cost)
                    {
                        double gain = cost - c2;
                        p = q; cost = c2; r = r2;
                        lambda = System.Math.Max(lambda * 0.3, 1e-9);
                        improved = true;
                        if (gain < 1e-14) iteration = 50;
                        break;
                    }
                    lambda *= 10;
                }
                if (!improved) break;
            }
            if (cost >= double.MaxValue) return Fail(out position, out normal, out rms);
            position = new Vector3((float)p[0], (float)p[1], (float)p[2]);
            normal = new Vector2((float)System.Math.Sin(p[3]), (float)System.Math.Cos(p[3]));
            rms = System.Math.Sqrt(cost / 8.0);
            return true;
        }

        static bool Fail(out Vector3 position, out Vector2 normal, out double rms)
        {
            position = Vector3.zero;
            normal = Vector2.zero;
            rms = double.MaxValue;
            return false;
        }

        /// <summary>板の四隅を画像に写したときの、観測との差（8つ）と、その二乗和。カメラの後ろに回ったら double.MaxValue。</summary>
        static double Cost(CaptureView view, Vector2[] observed, float aspect, float width, float height, double[] p,
            out double[] residual)
        {
            residual = null;
            var center = new Vector3((float)p[0], (float)p[1], (float)p[2]);
            var n = new Vector3((float)System.Math.Sin(p[3]), 0f, (float)System.Math.Cos(p[3]));
            // 表から見て右（テストの CornersOf と同じ向き）
            var right = Vector3.Cross(n, Vector3.up).normalized * (width * 0.5f);
            var up = Vector3.up * (height * 0.5f);
            var corners = new[] { center - right + up, center + right + up, center + right - up, center - right - up };
            var r = new double[8];
            double sum = 0;
            var inverse = Quaternion.Inverse(view.Rotation);
            for (int i = 0; i < 4; i++)
            {
                var local = inverse * (corners[i] - view.Position);
                var clip = view.Projection * new Vector4(local.x, local.y, -local.z, 1f);
                if (clip.w <= 1e-4f) return double.MaxValue;
                double x = (clip.x / clip.w + 1.0) * 0.5;
                double y = (1.0 - clip.y / clip.w) * 0.5;
                // 画像の縦を 1 とした長さにそろえる（横は縦横比を掛ける）
                r[2 * i] = (x - observed[i].x) * aspect;
                r[2 * i + 1] = y - observed[i].y;
                sum += r[2 * i] * r[2 * i] + r[2 * i + 1] * r[2 * i + 1];
            }
            residual = r;
            return sum;
        }

        /// <summary>4×4 の連立一次方程式を、部分ピボットのガウスの消去法で解く。</summary>
        static bool Solve4(double[,] a, double[] b, out double[] x)
        {
            const int n = 4;
            var m = (double[,])a.Clone();
            x = (double[])b.Clone();
            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                for (int row = col + 1; row < n; row++)
                    if (System.Math.Abs(m[row, col]) > System.Math.Abs(m[pivot, col])) pivot = row;
                if (System.Math.Abs(m[pivot, col]) < 1e-15) return false;
                if (pivot != col)
                {
                    for (int k = 0; k < n; k++) (m[col, k], m[pivot, k]) = (m[pivot, k], m[col, k]);
                    (x[col], x[pivot]) = (x[pivot], x[col]);
                }
                for (int row = col + 1; row < n; row++)
                {
                    double f = m[row, col] / m[col, col];
                    for (int k = col; k < n; k++) m[row, k] -= f * m[col, k];
                    x[row] -= f * x[col];
                }
            }
            for (int row = n - 1; row >= 0; row--)
            {
                double s2 = x[row];
                for (int k = row + 1; k < n; k++) s2 -= m[row, k] * x[k];
                x[row] = s2 / m[row, row];
            }
            return true;
        }

        static Vector3 LocalDirection(Matrix4x4 p, float x, float y)
        {
            float ndcX = 2f * x - 1f;
            float ndcY = 1f - 2f * y;
            // Unity の projection は view 空間の -z が前。clip.x = P00 x + P02 z_view、w = -z_view
            return new Vector3((ndcX + p.m02) / p.m00, (ndcY + p.m12) / p.m11, 1f);
        }
    }
}
