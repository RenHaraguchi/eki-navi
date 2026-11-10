using NUnit.Framework;
using UnityEngine;

namespace EkiNavi.Alignment.Tests
{
    public class RectangleFitTests
    {
        // 縦長の画面（縦 60 度、横:縦 = 1:2）。KYV46 の画面と同じ縦 1920px として、画素のずれを割合にする
        static readonly Matrix4x4 Projection = Matrix4x4.Perspective(60f, 0.5f, 0.1f, 100f);
        const float ImageHeightPx = 1920f;

        static Vector2 ToImage(CaptureView view, Vector3 world)
        {
            var local = Quaternion.Inverse(view.Rotation) * (world - view.Position);
            var clip = view.Projection * new Vector4(local.x, local.y, -local.z, 1f);
            return new Vector2((clip.x / clip.w + 1f) * 0.5f, (1f - clip.y / clip.w) * 0.5f);
        }

        static Vector2[] CornersOf(CaptureView view, Vector3 center, float width, float height, Vector3 normal)
        {
            var right = Vector3.Cross(normal, Vector3.up).normalized * (width * 0.5f);
            var up = Vector3.up * (height * 0.5f);
            return new[]
            {
                ToImage(view, center - right + up), ToImage(view, center + right + up),
                ToImage(view, center + right - up), ToImage(view, center - right - up),
            };
        }

        /// <summary>天井から吊った看板を、斜め angle 度・distance m の所から見上げて撮る。</summary>
        static (CaptureView view, Vector3 center, Vector3 normal) Scene(float angle, float distance, float width = 3f, float height = 0.4f)
        {
            var center = new Vector3(0f, 2.7f, 0f);
            var normal = Vector3.back;
            var cameraPosition = center + Quaternion.Euler(0f, angle, 0f) * (normal * distance);
            cameraPosition.y = 1.4f;
            var view = new CaptureView(cameraPosition, Quaternion.LookRotation(center - cameraPosition), Projection);
            return (view, center, normal);
        }

        static bool Fit(CaptureView view, Vector2[] c, float width, float height, out Vector3 position, out Vector2 normal,
            out float rms, out float confidence)
        {
            position = Vector3.zero;
            normal = Vector2.zero;
            rms = 0f;
            confidence = 0f;
            if (!SignVisionGeometry.TryLocateFromCorners(view, c[0], c[1], c[2], c[3], width, height,
                    out var p0, out var n0, out _, out _)) return false;
            return SignVisionGeometry.TryFitRectangle(view, c[0], c[1], c[2], c[3], width, height, p0, n0,
                out position, out normal, out rms, out confidence);
        }

        [Test]
        public void ExactCornersGiveTheTruePose()
        {
            var (view, center, normal) = Scene(30f, 4f);
            var c = CornersOf(view, center, 3f, 0.4f, normal);

            Assert.That(Fit(view, c, 3f, 0.4f, out var position, out var n, out var rms, out var confidence), Is.True);
            Assert.That(Vector3.Distance(position, center), Is.LessThan(0.02f));
            Assert.That(Vector2.Angle(n, new Vector2(normal.x, normal.z)), Is.LessThan(0.5f));
            Assert.That(rms * ImageHeightPx, Is.LessThan(0.5f));
            Assert.That(confidence, Is.GreaterThan(3f), "逆の向きでは四隅が合わない");
        }

        [Test]
        public void ASmallAngleThatTheEdgeLengthsCannotTellIsSolved()
        {
            // 斜め 12 度・5m。左右の辺の長さの差だけでは左右を決められず、正面とみなしていた場面
            var (view, center, normal) = Scene(12f, 5f);
            var c = CornersOf(view, center, 3f, 0.4f, normal);
            Assert.That(SignVisionGeometry.TryLocateFromCorners(view, c[0], c[1], c[2], c[3], 3f, 0.4f,
                out _, out _, out _, out var sideKnown), Is.True);

            Assert.That(Fit(view, c, 3f, 0.4f, out _, out var n, out _, out _), Is.True);
            Assert.That(Vector2.Angle(n, new Vector2(normal.x, normal.z)), Is.LessThan(1f));
            TestContext.WriteLine($"辺の長さで左右を決められたか: {sideKnown}");
        }

        [Test]
        public void CornerNoiseOfAFewPixelsKeepsTheFacingClose()
        {
            // 四隅がそれぞれ最大 4px ずれる。斜め 0〜40 度、3〜6m の 200 回で、向きの誤差を比べる
            var random = new System.Random(20261110);
            int trials = 0, fitBetter = 0;
            float fitSum = 0f, edgeSum = 0f, fitWorst = 0f;
            for (int t = 0; t < 200; t++)
            {
                float angle = (float)(random.NextDouble() * 80.0 - 40.0);
                float distance = 3f + (float)random.NextDouble() * 3f;
                var (view, center, normal) = Scene(angle, distance);
                var c = CornersOf(view, center, 3f, 0.4f, normal);
                for (int i = 0; i < 4; i++)
                {
                    float px = 4f / ImageHeightPx;
                    c[i] += new Vector2((float)(random.NextDouble() * 2 - 1) * px * 0.5f, (float)(random.NextDouble() * 2 - 1) * px);
                }
                if (!SignVisionGeometry.TryLocateFromCorners(view, c[0], c[1], c[2], c[3], 3f, 0.4f,
                        out var p0, out var n0, out _, out _)) continue;
                if (!SignVisionGeometry.TryFitRectangle(view, c[0], c[1], c[2], c[3], 3f, 0.4f, p0, n0,
                        out _, out var n, out _, out _)) continue;
                var truth = new Vector2(normal.x, normal.z);
                float fitError = Vector2.Angle(n, truth), edgeError = Vector2.Angle(n0, truth);
                trials++;
                fitSum += fitError;
                edgeSum += edgeError;
                fitWorst = Mathf.Max(fitWorst, fitError);
                if (fitError <= edgeError + 0.5f) fitBetter++;
            }
            TestContext.WriteLine($"試行 {trials}、向きの誤差の平均: 四隅の当てはめ {fitSum / trials:0.0} 度、辺の長さ {edgeSum / trials:0.0} 度、当てはめの最大 {fitWorst:0.0} 度、当てはめが同じか良い {fitBetter}");
            Assert.That(trials, Is.GreaterThan(180));
            Assert.That(fitSum / trials, Is.LessThan(edgeSum / trials), "平均で、辺の長さだけより良い");
        }

        [Test]
        public void ANormalFacingAwayFromTheCameraIsRejected()
        {
            var (view, center, normal) = Scene(20f, 4f);
            var c = CornersOf(view, center, 3f, 0.4f, normal);
            // 四隅の順を左右入れ替える（裏から見た板とみなす）と、表がカメラを向く答えは出ない
            bool ok = SignVisionGeometry.TryFitRectangle(view, c[1], c[0], c[3], c[2], 3f, 0.4f, center,
                new Vector2(normal.x, normal.z), out _, out var n, out var rms, out _);
            Assert.That(!ok || rms * ImageHeightPx > 2f || Vector2.Angle(n, new Vector2(normal.x, normal.z)) > 30f);
        }
    }
}
