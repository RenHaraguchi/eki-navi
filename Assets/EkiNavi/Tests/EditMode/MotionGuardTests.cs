using NUnit.Framework;
using UnityEngine;

namespace EkiNavi.Alignment.Tests
{
    public class MotionGuardTests
    {
        const float Frame = 1f / 60f;

        // 机に置いた端末くらいの小さな揺れ（±0.005g）
        static float StillAcceleration(int frame) => 1f + 0.005f * Mathf.Sin(frame * 1.7f);

        // 歩いているときの揺れ。1秒に2歩、振れ幅 0.25g
        static float WalkingAcceleration(float time) => 1f + 0.25f * Mathf.Sin(2f * Mathf.PI * 2f * time);

        [Test]
        public void CancelsArcoreMotionWhileStandingStill()
        {
            var guard = new MotionGuard();
            int frame = 0;
            // 1秒止まって、止まっている判定になるのを待つ
            for (; frame < 60; frame++) guard.Update(frame * Frame, StillAcceleration(frame), Vector2.zero);
            Assert.That(guard.IsStill, Is.True);

            // 止まったまま、ARCore の位置だけが2秒で 1.5m 動く（人の動きに引っ張られた）
            var total = Vector2.zero;
            for (int i = 1; i <= 120; i++, frame++)
            {
                var camera = new Vector2(1.5f * i / 120f, 0f);
                total += guard.Update(frame * Frame, StillAcceleration(frame), camera);
            }
            Assert.That(total.x, Is.EqualTo(1.5f).Within(0.01f), "動いた分をすべて打ち消す");
            Assert.That(guard.IsStill, Is.True);
        }

        [Test]
        public void LeavesNormalWalkingAlone()
        {
            var guard = new MotionGuard();
            var total = Vector2.zero;
            // 1.3m/s で 5秒歩く（1秒に2歩、歩幅 0.65m に合う）
            for (int frame = 0; frame < 300; frame++)
            {
                float t = frame * Frame;
                total += guard.Update(t, WalkingAcceleration(t), new Vector2(0f, 1.3f * t));
            }
            Assert.That(guard.IsStill, Is.False);
            Assert.That(guard.Steps, Is.InRange(9, 11), "5秒で約10歩");
            Assert.That(total.magnitude, Is.LessThan(0.1f), "普通に歩いているときは、歩き出しのわずかな分しか打ち消さない");
            Assert.That(guard.DriftSuspected(300 * Frame), Is.False);
        }

        [Test]
        public void CancelsJumpThatStepsCannotExplain()
        {
            var guard = new MotionGuard();
            int frame = 0;
            float z = 0f;
            for (; frame < 120; frame++)
            {
                float t = frame * Frame;
                z = 1.3f * t;
                guard.Update(t, WalkingAcceleration(t), new Vector2(0f, z));
            }

            // 歩いている途中で、0.5秒の間に ARCore の位置が横に 4m 飛ぶ
            var total = Vector2.zero;
            for (int i = 1; i <= 30; i++, frame++)
            {
                float t = frame * Frame;
                z = 1.3f * t;
                total += guard.Update(t, WalkingAcceleration(t), new Vector2(4f * i / 30f, z));
            }
            Assert.That(total.magnitude, Is.GreaterThan(2f), "歩数で説明できない分を打ち消す");
            Assert.That(guard.DriftSuspected(frame * Frame), Is.True);
        }

        [Test]
        public void StartsMovingAgainWhenShakingStarts()
        {
            var guard = new MotionGuard();
            int frame = 0;
            for (; frame < 60; frame++) guard.Update(frame * Frame, StillAcceleration(frame), Vector2.zero);
            Assert.That(guard.IsStill, Is.True);
            for (int i = 0; i < 30; i++, frame++)
                guard.Update(frame * Frame, WalkingAcceleration(frame * Frame), Vector2.zero);
            Assert.That(guard.IsStill, Is.False);
        }

        [Test]
        public void ResetClearsStepsAndDriftNotice()
        {
            var guard = new MotionGuard();
            for (int frame = 0; frame < 120; frame++)
            {
                float t = frame * Frame;
                guard.Update(t, WalkingAcceleration(t), new Vector2(frame < 100 ? 0f : 5f, 0f));
            }
            Assert.That(guard.Steps, Is.GreaterThan(0));
            guard.Reset(new Vector2(5f, 0f));
            Assert.That(guard.Steps, Is.EqualTo(0));
            Assert.That(guard.DriftSuspected(120 * Frame), Is.False);
            Assert.That(guard.TotalCompensatedMeters, Is.EqualTo(0f));
        }
    }
}
