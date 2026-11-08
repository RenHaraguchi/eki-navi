using NUnit.Framework;
using UnityEngine;

namespace EkiNavi.Alignment.Tests
{
    public class StepDistanceProbeTests
    {
        [Test]
        public void AddsDistanceOnlyForMovesLongerThanTheMinimum()
        {
            var probe = new StepDistanceProbe(0.25f);
            probe.Reset(Vector2.zero, 0);
            // 止まっているときの小さな揺れ（10cm 以内）は足さない
            for (int i = 0; i < 50; i++) probe.AddPosition(new Vector2(0.1f * Mathf.Sin(i), 0f));
            Assert.That(probe.ArDistance, Is.EqualTo(0f));
            // 10m まっすぐ歩く
            for (int i = 1; i <= 100; i++) probe.AddPosition(new Vector2(0f, 0.1f * i));
            Assert.That(probe.ArDistance, Is.EqualTo(10f).Within(0.3f));
        }

        [Test]
        public void FitsStrideFromArDistanceAndSteps()
        {
            var probe = new StepDistanceProbe();
            probe.Reset(Vector2.zero, 100); // MotionGuard の歩数は通し番号
            for (int i = 1; i <= 140; i++) probe.AddPosition(new Vector2(0f, 0.1f * i));
            Assert.That(probe.Steps(120), Is.EqualTo(20));
            Assert.That(probe.FittedStride(120), Is.EqualTo(14f / 20f).Within(0.02f));
            Assert.That(probe.StepDistance(120, 0.7f), Is.EqualTo(14f).Within(0.001f));
            // 歩数が少ないうちは歩幅を出さない
            Assert.That(probe.FittedStride(110), Is.EqualTo(0f));
        }

        [Test]
        public void RebaseSkipsTheJumpAfterTrackingLoss()
        {
            var probe = new StepDistanceProbe();
            probe.Reset(Vector2.zero, 0);
            probe.AddPosition(new Vector2(0f, 1f));
            probe.Rebase(new Vector2(30f, 30f)); // 追跡が戻ったら、飛んだ分は入れない
            probe.AddPosition(new Vector2(30f, 31f));
            Assert.That(probe.ArDistance, Is.EqualTo(2f).Within(0.001f));
        }
    }
}
