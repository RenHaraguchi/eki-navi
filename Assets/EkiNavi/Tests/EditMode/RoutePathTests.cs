using NUnit.Framework;
using UnityEngine;

namespace EkiNavi.Alignment.Tests
{
    public class RoutePathTests
    {
        // 北へ 10m 進んで右（東）へ 10m、さらに左（北）へ 5m
        static RoutePath Path() => new RoutePath(new[]
        {
            new Vector2(0f, 0f),
            new Vector2(0f, 10f),
            new Vector2(10f, 10f),
            new Vector2(10f, 15f),
        });

        [Test]
        public void LengthAndPointAt()
        {
            var path = Path();
            Assert.That(path.Length, Is.EqualTo(25f).Within(1e-4f));
            Assert.That((path.PointAt(12f) - new Vector2(2f, 10f)).magnitude, Is.LessThan(1e-4f));
            Assert.That(path.PointAt(-1f), Is.EqualTo(new Vector2(0f, 0f)));
            Assert.That(path.PointAt(100f), Is.EqualTo(new Vector2(10f, 15f)));
        }

        [Test]
        public void ProjectFindsNearestPointAndDistance()
        {
            var p = Path().Project(new Vector2(1f, 4f));
            Assert.That(p.Along, Is.EqualTo(4f).Within(1e-4f));
            Assert.That(p.DistanceFromRoute, Is.EqualTo(1f).Within(1e-4f));
        }

        [Test]
        public void NextTurnIsRightThenLeft()
        {
            var path = Path();
            Assert.That(path.TryNextTurn(3f, out var first), Is.True);
            Assert.That(first.Distance, Is.EqualTo(7f).Within(1e-4f));
            Assert.That(first.IsRight, Is.True);
            Assert.That(first.AngleDeg, Is.EqualTo(90f).Within(1e-3f));

            Assert.That(path.TryNextTurn(12f, out var second), Is.True);
            Assert.That(second.Distance, Is.EqualTo(8f).Within(1e-4f));
            Assert.That(second.IsRight, Is.False);

            Assert.That(path.TryNextTurn(21f, out _), Is.False);
        }

        [Test]
        public void GuideTargetLooksAheadOnRouteAndPointsBackWhenOff()
        {
            var path = Path();
            var ahead = path.GuideTarget(new Vector2(0.5f, 8f), 4f, 3f, out var onRoute);
            Assert.That(onRoute.DistanceFromRoute, Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That((ahead - new Vector2(2f, 10f)).magnitude, Is.LessThan(1e-4f), "曲がり角の先を指す");

            var back = path.GuideTarget(new Vector2(-6f, 5f), 4f, 3f, out var off);
            Assert.That(off.DistanceFromRoute, Is.EqualTo(6f).Within(1e-4f));
            Assert.That((back - new Vector2(0f, 5f)).magnitude, Is.LessThan(1e-4f), "経路のいちばん近い点を指す");
        }

        [Test]
        public void SmallBendsAreNotTurns()
        {
            var path = new RoutePath(new[] { new Vector2(0f, 0f), new Vector2(0f, 10f), new Vector2(2f, 20f) });
            Assert.That(path.TryNextTurn(0f, out _), Is.False, "約11度の折れは曲がり角にしない");
        }

        [Test]
        public void DuplicatePointsAreIgnored()
        {
            var path = new RoutePath(new[] { new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 5f) });
            Assert.That(path.Length, Is.EqualTo(5f).Within(1e-4f));
        }
    }
}
