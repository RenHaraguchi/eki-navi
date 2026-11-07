using NUnit.Framework;
using UnityEngine;

namespace EkiNavi.Alignment.Tests
{
    public class SignVisionGeometryTests
    {
        // 縦長の画面（縦 60 度、横:縦 = 1:2）
        static readonly Matrix4x4 Projection = Matrix4x4.Perspective(60f, 0.5f, 0.1f, 100f);

        /// <summary>AR の点を、カメラの画像の 0〜1（上が 0）に写す。</summary>
        static Vector2 ToImage(CaptureView view, Vector3 world)
        {
            var local = Quaternion.Inverse(view.Rotation) * (world - view.Position);
            var clip = view.Projection * new Vector4(local.x, local.y, -local.z, 1f);
            return new Vector2((clip.x / clip.w + 1f) * 0.5f, (1f - clip.y / clip.w) * 0.5f);
        }

        /// <summary>板（中心 center、縦 height、横 width、法線 normal）が写る枠。</summary>
        static Rect BoxOf(CaptureView view, Vector3 center, float width, float height, Vector3 normal)
        {
            var right = Vector3.Cross(Vector3.up, normal).normalized * (width * 0.5f);
            var up = Vector3.up * (height * 0.5f);
            float xMin = 1f, yMin = 1f, xMax = 0f, yMax = 0f;
            foreach (var corner in new[] { center + right + up, center + right - up, center - right + up, center - right - up })
            {
                var p = ToImage(view, corner);
                xMin = Mathf.Min(xMin, p.x); xMax = Mathf.Max(xMax, p.x);
                yMin = Mathf.Min(yMin, p.y); yMax = Mathf.Max(yMax, p.y);
            }
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        [Test]
        public void LocatesASignSeenHeadOn()
        {
            var view = new CaptureView(new Vector3(0f, 1.4f, 0f), Quaternion.identity, Projection);
            var center = new Vector3(0.3f, 1.6f, 5f);
            var box = BoxOf(view, center, 0.841f, 0.594f, Vector3.back);

            Assert.That(SignVisionGeometry.TryLocate(view, box.xMin, box.yMin, box.xMax, box.yMax, 0.594f,
                out var position, out var normal), Is.True);

            Assert.That(Vector3.Distance(position, center), Is.LessThan(0.02f));
            // 正面から撮ったとみなすので、法線はカメラへの水平の向き
            var expected = new Vector2(-0.3f, -5f).normalized;
            Assert.That(Vector2.Angle(normal, expected), Is.LessThan(0.5f));
        }

        [Test]
        public void UsesTheCaptureRotation()
        {
            // 東を向いて、少し下を向いたカメラ
            var rotation = Quaternion.Euler(10f, 90f, 0f);
            var view = new CaptureView(new Vector3(2f, 1.4f, -1f), rotation, Projection);
            var center = view.Position + rotation * new Vector3(0.2f, 0.3f, 4f);
            var box = BoxOf(view, center, 0.6f, 0.4f, -(rotation * Vector3.forward));

            Assert.That(SignVisionGeometry.TryLocate(view, box.xMin, box.yMin, box.xMax, box.yMax, 0.4f,
                out var position, out _), Is.True);

            // 板が少し傾いて見えるので、縦の長さの見え方がわずかに変わる
            Assert.That(Vector3.Distance(position, center), Is.LessThan(0.15f));
        }

        /// <summary>板の四隅（左上、右上、右下、左下）を画像に写す。normal は板の表の向き。</summary>
        static Vector2[] CornersOf(CaptureView view, Vector3 center, float width, float height, Vector3 normal)
        {
            // 表から見て右が +right
            var right = Vector3.Cross(normal, Vector3.up).normalized * (width * 0.5f);
            var up = Vector3.up * (height * 0.5f);
            return new[]
            {
                ToImage(view, center - right + up), ToImage(view, center + right + up),
                ToImage(view, center + right - up), ToImage(view, center - right - up),
            };
        }

        [Test]
        public void CornersGiveTheFacingWhenSeenAtAnAngle()
        {
            // 板は南（-z）を向いている。カメラは板の右斜め前、40 度ずれた所から、少し見上げる
            var center = new Vector3(0f, 2.2f, 0f);
            var normal = Vector3.back;
            var cameraPosition = center + Quaternion.Euler(0f, -40f, 0f) * (normal * 4f);
            cameraPosition.y = 1.4f;
            var look = Quaternion.LookRotation(center - cameraPosition);
            var view = new CaptureView(cameraPosition, look, Projection);
            var c = CornersOf(view, center, 0.841f, 0.594f, normal);

            Assert.That(SignVisionGeometry.TryLocateFromCorners(view, c[0], c[1], c[2], c[3], 0.841f, 0.594f,
                out var position, out var horizontalNormal, out var ratio, out var sideKnown), Is.True);

            Assert.That(Vector3.Distance(position, center), Is.LessThan(0.02f));
            Assert.That(Vector2.Angle(horizontalNormal, new Vector2(0f, -1f)), Is.LessThan(0.5f), "板の本当の向き（南）");
            Assert.That(ratio, Is.EqualTo(Mathf.Cos(40f * Mathf.Deg2Rad)).Within(0.03f), "横幅は cos 40° に縮んで見える");
            Assert.That(sideKnown, Is.True);

            // 正面から撮ったとみなす方法では、40 度ずれる
            var box = BoxOf(view, center, 0.841f, 0.594f, normal);
            SignVisionGeometry.TryLocate(view, box.xMin, box.yMin, box.xMax, box.yMax, 0.594f, out _, out var headOn);
            Assert.That(Vector2.Angle(headOn, new Vector2(0f, -1f)), Is.EqualTo(40f).Within(3f));
        }

        [Test]
        public void CornersWorkWithATiltedCamera()
        {
            // 吊り下げ看板を、3m ほど離れた斜め 35 度の所から見上げる（カメラは上を向き、8 度傾いている）
            var center = new Vector3(3f, 2.6f, 6f);
            var normal = new Vector3(-1f, 0f, -1f).normalized;
            var cameraPosition = center + Quaternion.Euler(0f, 35f, 0f) * (normal * 3f);
            cameraPosition.y = 1.3f;
            var look = Quaternion.LookRotation(center - cameraPosition) * Quaternion.Euler(0f, 0f, 8f);
            var view = new CaptureView(cameraPosition, look, Projection);
            var c = CornersOf(view, center, 1.2f, 0.4f, normal);

            Assert.That(SignVisionGeometry.TryLocateFromCorners(view, c[0], c[1], c[2], c[3], 1.2f, 0.4f,
                out var position, out var horizontalNormal, out _, out _), Is.True);

            Assert.That(Vector3.Distance(position, center), Is.LessThan(0.05f));
            Assert.That(Vector2.Angle(horizontalNormal, new Vector2(normal.x, normal.z)), Is.LessThan(2f));
        }

        [Test]
        public void CornerErrorsOfAFewPixelsKeepTheFacingRoughlyRight()
        {
            // 1920×1080 の画像で、四隅が 3px ずつずれたときの向きの誤差を見る（距離 4m、斜め 30 度）
            var center = new Vector3(0f, 2f, 0f);
            var normal = Vector3.back;
            var cameraPosition = center + Quaternion.Euler(0f, 30f, 0f) * (normal * 4f);
            cameraPosition.y = 1.4f;
            var view = new CaptureView(cameraPosition, Quaternion.LookRotation(center - cameraPosition), Projection);
            var c = CornersOf(view, center, 0.841f, 0.594f, normal);
            float px = 3f / 1920f;
            c[0] += new Vector2(-px, -px); c[1] += new Vector2(px, px); c[2] += new Vector2(-px, px); c[3] += new Vector2(px, -px);

            Assert.That(SignVisionGeometry.TryLocateFromCorners(view, c[0], c[1], c[2], c[3], 0.841f, 0.594f,
                out _, out var horizontalNormal, out _, out _), Is.True);

            float error = Vector2.Angle(horizontalNormal, new Vector2(0f, -1f));
            TestContext.WriteLine($"向きの誤差 {error:0.0} 度");
            Assert.That(error, Is.LessThan(8f));
        }

        [Test]
        public void NearlyHeadOnCornersFallBackToFacingTheCamera()
        {
            var center = new Vector3(0f, 1.6f, 4f);
            var view = new CaptureView(new Vector3(0.1f, 1.4f, 0f), Quaternion.identity, Projection);
            var c = CornersOf(view, center, 0.841f, 0.594f, Vector3.back);

            Assert.That(SignVisionGeometry.TryLocateFromCorners(view, c[0], c[1], c[2], c[3], 0.841f, 0.594f,
                out var position, out var horizontalNormal, out _, out var sideKnown), Is.True);

            Assert.That(sideKnown, Is.False, "左右の辺の長さがほぼ同じ");
            Assert.That(Vector3.Distance(position, center), Is.LessThan(0.03f));
            Assert.That(Vector2.Angle(horizontalNormal, Vector2.down), Is.LessThan(3f));
        }

        [Test]
        public void RejectsCornersWhoseWidthDoesNotMatch()
        {
            var center = new Vector3(0f, 1.5f, 4f);
            var view = new CaptureView(new Vector3(0f, 1.4f, 0f), Quaternion.identity, Projection);
            var c = CornersOf(view, center, 0.841f, 0.594f, Vector3.back);

            // 登録した横幅が実際の 0.4 倍（別の板の四隅を答えた、など）。横幅が 2.5 倍に見えるのはおかしい
            Assert.That(SignVisionGeometry.TryLocateFromCorners(view, c[0], c[1], c[2], c[3], 0.34f, 0.594f,
                out _, out _, out var ratio, out _), Is.False);
            Assert.That(ratio, Is.GreaterThan(1.6f));
        }

        [Test]
        public void RejectsBoxesThatImplyImpossibleDistances()
        {
            var view = new CaptureView(Vector3.zero, Quaternion.identity, Projection);

            // 高さ 0.001 の枠に 0.6m の板は 30m より遠い。上下が逆の枠も使わない
            Assert.That(SignVisionGeometry.TryLocate(view, 0.4f, 0.5f, 0.6f, 0.501f, 0.6f, out _, out _), Is.False);
            Assert.That(SignVisionGeometry.TryLocate(view, 0.4f, 0.6f, 0.6f, 0.5f, 0.6f, out _, out _), Is.False);
        }

        [Test]
        public void RayGoesThroughTheBoxCenter()
        {
            var view = new CaptureView(new Vector3(1f, 1.5f, 2f), Quaternion.Euler(0f, -30f, 0f), Projection);
            var target = view.Position + Quaternion.Euler(0f, -30f, 0f) * new Vector3(-0.5f, 0.2f, 3f);
            var p = ToImage(view, target);

            var ray = SignVisionGeometry.RayThrough(view, p.x, p.y);

            var toTarget = (target - view.Position).normalized;
            Assert.That(Vector3.Angle(ray.direction, toTarget), Is.LessThan(0.1f));
        }

        [Test]
        public void WallPlaneGivesTheTrueNormalWhenSeenAtAnAngle()
        {
            // 壁は北を向いている（法線 -z ではなく、カメラ側の -z）。カメラは斜め左から見る
            var ray = new Ray(new Vector3(-2f, 1.5f, 0f), new Vector3(2f, 0f, 5f).normalized);
            float distance = new Vector3(2f, 0f, 5f).magnitude;

            Assert.That(SignVisionGeometry.TryRefineWithPlane(ray, distance * 1.1f,
                new Vector3(0.5f, 1f, 5f), Vector3.forward, 3f, 0.3f, out var hit, out var normal), Is.True);

            Assert.That(Vector3.Distance(hit, new Vector3(0f, 1.5f, 5f)), Is.LessThan(0.01f));
            Assert.That(Vector2.Angle(normal, Vector2.down), Is.LessThan(0.1f), "カメラ側（南）を向く");
        }

        [Test]
        public void IgnoresPlanesFarFromTheEstimate()
        {
            var ray = new Ray(Vector3.zero, Vector3.forward);

            // 見積もり 5m に対して、壁は 12m 先
            Assert.That(SignVisionGeometry.TryRefineWithPlane(ray, 5f,
                new Vector3(0f, 0f, 12f), Vector3.back, 3f, 0.3f, out _, out _), Is.False);
            // 床（水平の面）は使わない
            Assert.That(SignVisionGeometry.TryRefineWithPlane(ray, 5f,
                new Vector3(0f, -1f, 5f), Vector3.up, 3f, 0.3f, out _, out _), Is.False);
        }
    }
}
