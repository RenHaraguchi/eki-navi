using NUnit.Framework;
using UnityEngine;

namespace EkiNavi.Alignment.Tests
{
    public class GyroHeadingTests
    {
        [Test]
        public void IntegratesRotationAroundGravity()
        {
            var heading = new GyroHeading(1f);
            heading.Reset(350f);
            // 重力のまわりに毎秒 0.5 ラジアン（約 28.6 度）で 2 秒回る
            for (int i = 0; i < 120; i++) heading.Update(new Vector3(0f, 0.5f, 0f), Vector3.up, 1f / 60f);

            Assert.That(heading.HeadingDeg, Is.EqualTo(GyroHeading.Wrap360(350f + 57.3f)).Within(0.2f));
        }

        [Test]
        public void SignFlipsTheDirection()
        {
            var heading = new GyroHeading(-1f);
            heading.Reset(10f);
            for (int i = 0; i < 60; i++) heading.Update(new Vector3(0f, 0.5f, 0f), Vector3.up, 1f / 60f);

            Assert.That(heading.HeadingDeg, Is.EqualTo(GyroHeading.Wrap360(10f - 28.65f)).Within(0.2f));
        }

        [Test]
        public void OnlyRotationAroundGravityCounts()
        {
            // スマホを下に向けて持つと、重力は端末の z の向きになる。端末の y まわりの回転（手首を振る）は数えない
            Assert.That(GyroHeading.YawRateDeg(new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, -9.8f)), Is.EqualTo(0f).Within(1e-4f));
            Assert.That(GyroHeading.YawRateDeg(new Vector3(0f, 0f, 1f), new Vector3(0f, 0f, -9.8f)), Is.EqualTo(-57.2958f).Within(1e-3f));
        }

        [Test]
        public void IgnoresLongGapsAndMissingGravity()
        {
            var heading = new GyroHeading(1f);
            heading.Reset(90f);
            heading.Update(new Vector3(0f, 5f, 0f), Vector3.up, 2f); // 一時停止から戻った
            heading.Update(new Vector3(0f, 5f, 0f), Vector3.zero, 0.02f); // 重力が取れない

            Assert.That(heading.HeadingDeg, Is.EqualTo(90f));
        }

        [Test]
        public void WrapKeepsZeroTo360()
        {
            Assert.That(GyroHeading.Wrap360(-10f), Is.EqualTo(350f));
            Assert.That(GyroHeading.Wrap360(725f), Is.EqualTo(5f));
        }
    }
}
