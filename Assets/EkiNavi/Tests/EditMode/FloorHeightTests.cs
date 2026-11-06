using NUnit.Framework;

namespace EkiNavi.Alignment.Tests
{
    public class FloorHeightTests
    {
        const float CameraY = 0f;
        const float EyeHeight = 1.4f;

        [Test]
        public void PrefersThePlaneNearTheSignEstimate()
        {
            // 床（-1.45）と机（-0.85）が見えている。案内板からは -1.5
            var floor = FloorHeight.Choose(CameraY, new[] { -0.85f, -1.45f }, -1.5f, EyeHeight);
            Assert.That(floor.Source, Is.EqualTo(FloorSource.Plane));
            Assert.That(floor.Y, Is.EqualTo(-1.45f).Within(1e-5f));
        }

        [Test]
        public void UsesTheSignWhenNoPlaneAgrees()
        {
            // 見えているのは机だけ。案内板から求めた高さと 0.5m 以上違う
            var floor = FloorHeight.Choose(CameraY, new[] { -0.85f }, -1.5f, EyeHeight);
            Assert.That(floor.Source, Is.EqualTo(FloorSource.Sign));
            Assert.That(floor.Y, Is.EqualTo(-1.5f).Within(1e-5f));
        }

        [Test]
        public void UsesTheSignWhenNoPlaneIsDetected()
        {
            var floor = FloorHeight.Choose(CameraY, new float[0], -1.3f, EyeHeight);
            Assert.That(floor.Source, Is.EqualTo(FloorSource.Sign));
            Assert.That(floor.Y, Is.EqualTo(-1.3f).Within(1e-5f));
        }

        [Test]
        public void WithoutASignPicksThePlaneNearEyeHeight()
        {
            var floor = FloorHeight.Choose(CameraY, new[] { -0.9f, -1.35f, -2.4f }, null, EyeHeight);
            Assert.That(floor.Source, Is.EqualTo(FloorSource.Plane));
            Assert.That(floor.Y, Is.EqualTo(-1.35f).Within(1e-5f));
        }

        [Test]
        public void IgnoresPlanesTooCloseOrTooFarBelowTheCamera()
        {
            // 0.5m 下（机の上を覗き込んだとき）と 3m 下（吹き抜け）は床とみなさない
            var floor = FloorHeight.Choose(CameraY, new[] { -0.5f, -3f, 0.2f }, null, EyeHeight);
            Assert.That(floor.Source, Is.EqualTo(FloorSource.EyeHeight));
            Assert.That(floor.Y, Is.EqualTo(-EyeHeight).Within(1e-5f));
        }

        [Test]
        public void NullPlaneListFallsBack()
        {
            Assert.That(FloorHeight.Choose(CameraY, null, null, EyeHeight).Source, Is.EqualTo(FloorSource.EyeHeight));
            Assert.That(FloorHeight.Choose(CameraY, null, -1.5f, EyeHeight).Source, Is.EqualTo(FloorSource.Sign));
        }
    }
}
