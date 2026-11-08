using NUnit.Framework;
using UnityEngine;

namespace EkiNavi.Alignment.Tests
{
    public class CameraConfigChooserTests
    {
        [Test]
        public void PicksTheLargestWithinTheLimit()
        {
            // KYV46 で選べた3つ
            var sizes = new[] { new Vector2Int(640, 480), new Vector2Int(1280, 720), new Vector2Int(1920, 1080) };

            Assert.That(CameraConfigChooser.Choose(sizes), Is.EqualTo(2));
            Assert.That(CameraConfigChooser.Choose(sizes, 1280), Is.EqualTo(1));
        }

        [Test]
        public void SkipsSizesAboveTheLimit()
        {
            var sizes = new[] { new Vector2Int(3840, 2160), new Vector2Int(1920, 1080), new Vector2Int(640, 480) };

            Assert.That(CameraConfigChooser.Choose(sizes), Is.EqualTo(1));
        }

        [Test]
        public void FallsBackToTheSmallestWhenAllAreTooLarge()
        {
            var sizes = new[] { new Vector2Int(4000, 3000), new Vector2Int(3840, 2160) };

            Assert.That(CameraConfigChooser.Choose(sizes), Is.EqualTo(1));
        }

        [Test]
        public void EmptyListGivesMinusOne()
        {
            Assert.That(CameraConfigChooser.Choose(new Vector2Int[0]), Is.EqualTo(-1));
        }
    }
}
