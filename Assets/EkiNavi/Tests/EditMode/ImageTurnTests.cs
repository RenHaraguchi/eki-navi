using NUnit.Framework;
using UnityEngine;

namespace EkiNavi.Alignment.Tests
{
    public class ImageTurnTests
    {
        static readonly Matrix4x4 Projection = Matrix4x4.Perspective(60f, 0.5f, 0.1f, 100f);

        // 看板の正面に立ち、少し見上げる向き。roll は画面の向きを軸にした回転（正なら端末の上端が左に倒れる）
        static Quaternion Look(float roll)
        {
            return Quaternion.LookRotation(new Vector3(0f, 0.3f, 1f)) * Quaternion.Euler(0f, 0f, roll);
        }

        [Test]
        public void PortraitIsNotTurned()
        {
            Assert.That(ImageTurns.Choose(Look(0f)), Is.EqualTo(ImageTurn.None));
            Assert.That(ImageTurns.Choose(Look(30f)), Is.EqualTo(ImageTurn.None));
        }

        [Test]
        public void LandscapeTurnsSoTheWorldUpIsUp()
        {
            // 上端を左に倒すと、世界の上は画面の右に見える → 反時計回りに回す
            Assert.That(ImageTurns.Choose(Look(90f)), Is.EqualTo(ImageTurn.CounterClockwise));
            Assert.That(ImageTurns.Choose(Look(-90f)), Is.EqualTo(ImageTurn.Clockwise));
            Assert.That(ImageTurns.Choose(Look(180f)), Is.EqualTo(ImageTurn.Half));
        }

        [Test]
        public void TheChosenTurnPutsTheWorldUpAtTheTopOfTheTurnedImage()
        {
            foreach (float roll in new[] { 0f, 90f, -90f, 180f, 60f, -120f })
            {
                var rotation = Look(roll);
                var view = new CaptureView(Vector3.zero, rotation, Projection);
                // 画面の真ん中の点と、その少し上（世界の上）の点を画像に写す
                var forward = rotation * Vector3.forward;
                var a = ToImage(view, forward * 5f);
                var b = ToImage(view, forward * 5f + Vector3.up * 0.5f);
                var turn = ImageTurns.Choose(rotation);
                var ta = ImageTurns.ToTurned(turn, a);
                var tb = ImageTurns.ToTurned(turn, b);
                var d = tb - ta;
                Assert.That(d.y, Is.LessThan(0f), $"roll {roll}: 世界の上が回した画像の上を向く");
                Assert.That(Mathf.Abs(d.y), Is.GreaterThanOrEqualTo(Mathf.Abs(d.x) * 0.5f), $"roll {roll}");
            }
        }

        [Test]
        public void ToOriginalUndoesToTurned()
        {
            var p = new Vector2(0.2f, 0.7f);
            foreach (ImageTurn turn in System.Enum.GetValues(typeof(ImageTurn)))
            {
                var back = ImageTurns.ToOriginal(turn, ImageTurns.ToTurned(turn, p));
                Assert.That(Vector2.Distance(back, p), Is.LessThan(1e-6f), turn.ToString());
            }
        }

        [Test]
        public void RotatedPixelsLandWhereToTurnedSays()
        {
            // 3×2 の画像（左下が先頭、行は下から上）の各画素が、回したあと ToTurned の位置に来ること
            const int w = 3, h = 2;
            var src = new Color32[w * h];
            for (int i = 0; i < src.Length; i++) src[i] = new Color32((byte)i, 0, 0, 255);
            foreach (ImageTurn turn in System.Enum.GetValues(typeof(ImageTurn)))
            {
                var dst = ImageTurns.Rotate(src, w, h, turn, out int tw, out int th);
                Assert.That(tw * th, Is.EqualTo(w * h));
                for (int ay = 0; ay < h; ay++)
                for (int ax = 0; ax < w; ax++)
                {
                    // 画素の中心を、左上が原点の 0〜1 にする
                    var p = new Vector2((ax + 0.5f) / w, 1f - (ay + 0.5f) / h);
                    var t = ImageTurns.ToTurned(turn, p);
                    int tx = Mathf.FloorToInt(t.x * tw);
                    int ty = Mathf.FloorToInt((1f - t.y) * th);
                    Assert.That(dst[ty * tw + tx].r, Is.EqualTo(src[ay * w + ax].r), $"{turn} ({ax},{ay})");
                }
            }
        }

        [Test]
        public void CornersFromATurnedImageLocateTheSameSignAsPortrait()
        {
            // 斜め 30 度、3m 先の吊り下げ看板を、端末を横に倒して撮る。Gemini は回した画像で四隅を答える
            var center = new Vector3(0f, 2.6f, 0f);
            var normal = Vector3.back;
            var cameraPosition = center + Quaternion.Euler(0f, 30f, 0f) * (normal * 3f);
            cameraPosition.y = 1.4f;
            foreach (float roll in new[] { 90f, -90f })
            {
                var rotation = Quaternion.LookRotation(center - cameraPosition) * Quaternion.Euler(0f, 0f, roll);
                var view = new CaptureView(cameraPosition, rotation, Projection);
                var turn = ImageTurns.Choose(rotation);
                Assert.That(turn, Is.Not.EqualTo(ImageTurn.None));

                // 看板の四隅（左上・右上・右下・左下。看板を正面から見たときの左右）
                var right = Vector3.Cross(normal, Vector3.up).normalized; // 表から見て右
                Vector3 Corner(float sx, float sy) => center + right * (sx * 0.6f) + Vector3.up * (sy * 0.2f);
                var world = new[] { Corner(-1, 1), Corner(1, 1), Corner(1, -1), Corner(-1, -1) };
                // 回した画像で答えた座標を、元に戻して渡す（アプリと同じ流れ）
                var c = new Vector2[4];
                for (int i = 0; i < 4; i++)
                {
                    var answered = ImageTurns.ToTurned(turn, ToImage(view, world[i]));
                    c[i] = ImageTurns.ToOriginal(turn, answered);
                }

                Assert.That(SignVisionGeometry.TryLocateFromCorners(view, c[0], c[1], c[2], c[3], 1.2f, 0.4f,
                    out var position, out var horizontalNormal, out _, out var sideKnown), Is.True, $"roll {roll}");
                Assert.That(Vector3.Distance(position, center), Is.LessThan(0.05f), $"roll {roll}");
                Assert.That(sideKnown, Is.True, $"roll {roll}: 横に倒しても、どちらの辺が手前か決められる");
                Assert.That(Vector2.Angle(horizontalNormal, new Vector2(normal.x, normal.z)), Is.LessThan(2f), $"roll {roll}");
            }
        }

        [Test]
        public void VerticalLineFromATurnedBoxGivesTheSignPosition()
        {
            var center = new Vector3(0f, 2.5f, 4f);
            var cameraPosition = new Vector3(0f, 1.4f, 0f);
            var rotation = Quaternion.LookRotation(center - cameraPosition) * Quaternion.Euler(0f, 0f, -90f);
            var view = new CaptureView(cameraPosition, rotation, Projection);
            var top = ToImage(view, center + Vector3.up * 0.2f);
            var bottom = ToImage(view, center - Vector3.up * 0.2f);

            Assert.That(SignVisionGeometry.TryLocateFromVerticalLine(view, top, bottom, 0.4f,
                out var position, out _), Is.True);
            Assert.That(Vector3.Distance(position, center), Is.LessThan(0.05f));
        }

        [Test]
        public void ABoxAnsweredOnTheTurnedImageGivesTheSignPosition()
        {
            // アプリと同じ流れ: Gemini は回した画像で外枠を答え、枠の中央の上端と下端を元の画像に戻して使う
            var center = new Vector3(0f, 2.5f, 4f);
            var cameraPosition = new Vector3(0f, 1.4f, 0f);
            foreach (float roll in new[] { 90f, -90f })
            {
                var rotation = Quaternion.LookRotation(center - cameraPosition) * Quaternion.Euler(0f, 0f, roll);
                var view = new CaptureView(cameraPosition, rotation, Projection);
                var turn = ImageTurns.Choose(rotation);
                // 回した画像の上の外枠（看板の縦 0.4m、横 1.2m）
                var corners = new[]
                {
                    ImageTurns.ToTurned(turn, ToImage(view, center + new Vector3(-0.6f, 0.2f, 0f))),
                    ImageTurns.ToTurned(turn, ToImage(view, center + new Vector3(0.6f, 0.2f, 0f))),
                    ImageTurns.ToTurned(turn, ToImage(view, center + new Vector3(0.6f, -0.2f, 0f))),
                    ImageTurns.ToTurned(turn, ToImage(view, center + new Vector3(-0.6f, -0.2f, 0f))),
                };
                float xMin = Mathf.Min(corners[0].x, corners[3].x), xMax = Mathf.Max(corners[1].x, corners[2].x);
                float yMin = Mathf.Min(corners[0].y, corners[1].y), yMax = Mathf.Max(corners[2].y, corners[3].y);
                Assert.That(xMax - xMin, Is.GreaterThan(yMax - yMin), $"roll {roll}: 回した画像では看板が横長に見える");
                float cx = (xMin + xMax) * 0.5f;
                var top = ImageTurns.ToOriginal(turn, new Vector2(cx, yMin));
                var bottom = ImageTurns.ToOriginal(turn, new Vector2(cx, yMax));

                Assert.That(SignVisionGeometry.TryLocateFromVerticalLine(view, top, bottom, 0.4f,
                    out var position, out _), Is.True, $"roll {roll}");
                Assert.That(Vector3.Distance(position, center), Is.LessThan(0.1f), $"roll {roll}");
            }
        }

        static Vector2 ToImage(CaptureView view, Vector3 world)
        {
            var local = Quaternion.Inverse(view.Rotation) * (world - view.Position);
            var clip = view.Projection * new Vector4(local.x, local.y, -local.z, 1f);
            return new Vector2((clip.x / clip.w + 1f) * 0.5f, 1f - (clip.y / clip.w + 1f) * 0.5f);
        }
    }
}
