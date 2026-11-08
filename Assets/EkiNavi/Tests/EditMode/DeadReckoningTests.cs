using NUnit.Framework;
using UnityEngine;

namespace EkiNavi.Alignment.Tests
{
    public class DeadReckoningTests
    {
        [Test]
        public void StepsMoveAlongTheHeading()
        {
            var dr = new DeadReckoning();
            dr.Reset(new Vector2(10f, 20f));

            dr.Advance(10, 0f, 0.65f);    // 北へ 6.5m
            dr.Advance(4, 90f, 0.5f);     // 東へ 2m

            Assert.That(Vector2.Distance(dr.Position, new Vector2(12f, 26.5f)), Is.LessThan(1e-4f));
            Assert.That(dr.Steps, Is.EqualTo(14));
            Assert.That(dr.Distance, Is.EqualTo(8.5f).Within(1e-4f));
        }

        [Test]
        public void WestIs270()
        {
            var dr = new DeadReckoning();
            dr.Reset(Vector2.zero);
            dr.Advance(2, 270f, 1f);

            Assert.That(Vector2.Distance(dr.Position, new Vector2(-2f, 0f)), Is.LessThan(1e-4f));
        }

        [Test]
        public void ResetClearsTheCount()
        {
            var dr = new DeadReckoning();
            dr.Reset(Vector2.zero);
            dr.Advance(5, 45f, 0.65f);
            dr.Reset(new Vector2(1f, 1f));

            Assert.That(dr.Position, Is.EqualTo(new Vector2(1f, 1f)));
            Assert.That(dr.Steps, Is.EqualTo(0));
            Assert.That(dr.Distance, Is.EqualTo(0f));
        }

        [Test]
        public void WalkDirectionUsesTheTopOfThePhoneWhenLookingDown()
        {
            // 北を向いて立ち、スマホを足元に向ける: カメラは真下、上端は北
            Assert.That(DeadReckoning.TryWalkDirection(Vector3.down, Vector3.forward, out var d), Is.True);
            Assert.That(Vector2.Distance(d, new Vector2(0f, 1f)), Is.LessThan(1e-4f));

            // 東を向いて前に構える
            var forward = new Vector3(1f, -0.3f, 0f).normalized;
            Assert.That(DeadReckoning.TryWalkDirection(forward, Vector3.up, out d), Is.True);
            Assert.That(Vector2.Distance(d, new Vector2(1f, 0f)), Is.LessThan(1e-4f));
        }
    }
}
