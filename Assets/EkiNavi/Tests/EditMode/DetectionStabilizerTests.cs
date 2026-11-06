using NUnit.Framework;
using UnityEngine;

namespace EkiNavi.Alignment.Tests
{
    public class DetectionStabilizerTests
    {
        const float Frame = 1f / 30f;
        static readonly Vector2 North = new Vector2(0f, 1f);

        static Vector2 Direction(float azimuthDeg)
        {
            var rad = azimuthDeg * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(rad), Mathf.Cos(rad));
        }

        [Test]
        public void BecomesStableAfterHalfASecondOfSteadyTracking()
        {
            var s = new DetectionStabilizer();
            var position = new Vector2(1f, 2f);
            int frames = 0;
            while (!s.Add(frames * Frame, position, North))
            {
                frames++;
                Assert.That(frames, Is.LessThan(100), "安定にならない");
            }
            Assert.That(frames * Frame, Is.EqualTo(0.5f).Within(Frame + 1e-4f));
            Assert.That((s.MeanPosition - position).magnitude, Is.LessThan(1e-5f));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(s.MeanAzimuthDeg, 0f)), Is.LessThan(1e-3f));
        }

        [Test]
        public void JitteryPositionIsNotStable()
        {
            var s = new DetectionStabilizer();
            for (int i = 0; i < 60; i++)
            {
                var jitter = (i % 2 == 0 ? 1f : -1f) * 0.03f; // ±3cm
                Assert.That(s.Add(i * Frame, new Vector2(jitter, 0f), North), Is.False, $"フレーム {i}");
            }
        }

        [Test]
        public void JitteryAzimuthIsNotStable()
        {
            var s = new DetectionStabilizer();
            for (int i = 0; i < 60; i++)
            {
                var yaw = i % 2 == 0 ? 1.5f : -1.5f;
                Assert.That(s.Add(i * Frame, Vector2.zero, Direction(yaw)), Is.False, $"フレーム {i}");
            }
        }

        [Test]
        public void AzimuthAveragesAcrossNorth()
        {
            var s = new DetectionStabilizer();
            bool stable = false;
            for (int i = 0; i < 30; i++)
            {
                // 359.7 度と 0.3 度を行き来する。差は 0.6 度
                stable = s.Add(i * Frame, Vector2.zero, Direction(i % 2 == 0 ? 359.7f : 0.3f));
            }
            Assert.That(stable, Is.True);
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(s.MeanAzimuthDeg, 0f)), Is.LessThan(0.2f));
            Assert.That((s.MeanNormal - North).magnitude, Is.LessThan(0.01f));
        }

        [Test]
        public void GapInTrackingStartsOver()
        {
            var s = new DetectionStabilizer();
            float t = 0f;
            for (int i = 0; i < 12; i++, t += Frame) s.Add(t, Vector2.zero, North);
            t += 0.5f; // 追跡の記録が 0.5 秒途切れた
            for (int i = 0; i < 12; i++, t += Frame)
                Assert.That(s.Add(t, Vector2.zero, North), Is.False, $"途切れた後のフレーム {i}");
        }

        [Test]
        public void ResetClearsStability()
        {
            var s = new DetectionStabilizer();
            for (int i = 0; i <= 20; i++) s.Add(i * Frame, Vector2.zero, North);
            Assert.That(s.IsStable, Is.True);
            s.Reset();
            Assert.That(s.IsStable, Is.False);
            Assert.That(s.Add(21 * Frame, Vector2.zero, North), Is.False);
        }

        [Test]
        public void OldJitterLeavesTheWindow()
        {
            var s = new DetectionStabilizer();
            float t = 0f;
            // 最初は揺れていて、その後落ち着く
            for (int i = 0; i < 10; i++, t += Frame) s.Add(t, new Vector2(i % 2 == 0 ? 0.05f : -0.05f, 0f), North);
            bool stable = false;
            for (int i = 0; i < 30; i++, t += Frame) stable = s.Add(t, Vector2.zero, North);
            Assert.That(stable, Is.True);
        }
    }
}
