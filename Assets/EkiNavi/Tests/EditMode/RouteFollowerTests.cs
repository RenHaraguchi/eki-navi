using NUnit.Framework;
using UnityEngine;

namespace EkiNavi.Alignment.Tests
{
    public class RouteFollowerTests
    {
        // 北へ 60m 進んで、東へ 30m 曲がる経路
        static RoutePath Path() => new RoutePath(new[] { new Vector2(0f, 0f), new Vector2(0f, 60f), new Vector2(30f, 60f) });

        /// <summary>歩く向きがずれたまま、1歩ずつ歩いて経路に沿わせる。</summary>
        static (Vector2 position, float heading) Walk(RoutePath path, Vector2 start, float heading, int steps,
            bool follow, float turnAt = float.MaxValue, float turnTo = 0f)
        {
            var reckoning = new DeadReckoning();
            reckoning.Reset(start);
            for (int i = 0; i < steps; i++)
            {
                if (i == (int)turnAt) heading = turnTo;
                reckoning.Advance(1, heading, 0.65f);
                if (follow && RouteFollower.Follow(path, reckoning.Position, heading, 1, out var p, out var h))
                {
                    reckoning.MoveTo(p);
                    heading = h;
                }
            }
            return (reckoning.Position, heading);
        }

        [Test]
        public void AHeadingOffBy20DegreesIsCorrectedWhileWalkingAlongTheRoute()
        {
            var path = Path();
            // 合わせたときの向きが 20 度ずれている。50 歩（約 33m）歩く
            var without = Walk(path, Vector2.zero, 20f, 50, follow: false);
            var with = Walk(path, Vector2.zero, 20f, 50, follow: true);

            Assert.That(path.Project(without.position).DistanceFromRoute, Is.GreaterThan(6f), "沿わせないと、6m より外れる");
            Assert.That(path.Project(with.position).DistanceFromRoute, Is.LessThan(1f));
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(with.heading, 0f)), Is.LessThan(2f), "歩く向きが経路の向きに直る");
        }

        [Test]
        public void ProgressAlongTheRouteKeepsUpWithTheSteps()
        {
            var path = Path();
            var with = Walk(path, Vector2.zero, 20f, 50, follow: true);
            // 50 歩 × 0.65m = 32.5m。向きのずれで少し短くなるが、大きくは遅れない
            Assert.That(path.Project(with.position).Along, Is.GreaterThan(29f));
        }

        [Test]
        public void WalkingTheWrongWayIsNotPulledBack()
        {
            var path = Path();
            // 10 歩進んでから、経路と直角（西）に曲がって歩く。経路をたどっていないので外れたと分かる
            var with = Walk(path, Vector2.zero, 0f, 30, follow: true, turnAt: 10f, turnTo: 270f);
            Assert.That(path.Project(with.position).DistanceFromRoute, Is.GreaterThan(6f));
        }

        [Test]
        public void FarFromTheRouteNothingChanges()
        {
            var path = Path();
            var far = new Vector2(10f, 20f);
            Assert.That(RouteFollower.Follow(path, far, 0f, 1, out var p, out var h), Is.False);
            Assert.That(p, Is.EqualTo(far));
            Assert.That(h, Is.EqualTo(0f));
        }

        [Test]
        public void TheCornerIsFollowedAfterTurning()
        {
            var path = Path();
            // 北へ 92 歩（約 60m）歩いてから、東へ曲がる（曲がったあとも少しずれた向き）
            var with = Walk(path, Vector2.zero, 0f, 120, follow: true, turnAt: 92f, turnTo: 100f);
            var projection = path.Project(with.position);
            Assert.That(projection.DistanceFromRoute, Is.LessThan(1.5f));
            Assert.That(projection.Along, Is.GreaterThan(70f), "曲がり角を過ぎて東の区間を進んでいる");
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(with.heading, 90f)), Is.LessThan(5f));
        }
    }
}
