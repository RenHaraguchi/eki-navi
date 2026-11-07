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
