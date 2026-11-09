using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>
    /// 推測航法の位置と向きを、経路に沿わせる（経路の近くを、経路の向きに歩いているときだけ）。
    ///
    /// 看板で合わせたときの向きが 20 度ずれていると、50m 歩くあいだに 16m ほど横にずれ、
    /// 道なりに歩いていても「経路から外れた」と出ていた（2026-11-09 の現地の試験）。
    /// 経路の近くを経路の向きに歩いているなら、利用者は経路をたどっているとみなして、
    /// - 位置を経路の上の点に少しずつ寄せる
    /// - 歩く向きを、その区間の向きに少しずつ寄せる（合わせたときの向きのずれが、歩くうちに直る）
    /// 経路と違う向きに歩いているとき（曲がる所を間違えたなど）は何もしないので、外れたことは分かる。
    /// </summary>
    public static class RouteFollower
    {
        /// <summary>経路からこれより離れていたら、沿わせない（m）。</summary>
        public const float FollowRadius = 6f;

        /// <summary>歩く向きと区間の向きの差がこれより大きければ、経路をたどっていないとみなす（度）。</summary>
        public const float MaxHeadingGapDeg = 35f;

        /// <summary>1歩ごとに、経路の上の点へ寄せる割合。</summary>
        public const float PositionPull = 0.15f;

        /// <summary>1歩ごとに、区間の向きへ寄せる割合。</summary>
        public const float HeadingPull = 0.1f;

        /// <summary>
        /// 歩いたあとの位置と向きを、経路に沿わせる。
        /// </summary>
        /// <param name="path">経路（経路の座標）。</param>
        /// <param name="position">歩いたあとの位置（経路の座標）。</param>
        /// <param name="headingDeg">歩いている向き（経路の座標、北から時計回りの度）。</param>
        /// <param name="steps">今回進んだ歩数。</param>
        /// <param name="followedPosition">沿わせた位置。</param>
        /// <param name="followedHeadingDeg">沿わせた向き。</param>
        /// <returns>沿わせたか（経路をたどっているとみなしたか）。</returns>
        public static bool Follow(RoutePath path, Vector2 position, float headingDeg, int steps,
            out Vector2 followedPosition, out float followedHeadingDeg)
        {
            followedPosition = position;
            followedHeadingDeg = headingDeg;
            if (path == null || steps <= 0 || path.Length <= 0f) return false;

            var projection = path.Project(position);
            if (projection.DistanceFromRoute > FollowRadius) return false;

            // 区間の向き（いまいる所の前後 1m を結ぶ向き）
            var tangent = path.PointAt(Mathf.Min(path.Length, projection.Along + 1f))
                          - path.PointAt(Mathf.Max(0f, projection.Along - 1f));
            if (tangent.sqrMagnitude < 1e-6f) return false;
            float routeAzimuth = Azimuth.Of(tangent);
            float gap = Mathf.DeltaAngle(headingDeg, routeAzimuth);
            if (Mathf.Abs(gap) > MaxHeadingGapDeg) return false;

            // 歩数が多いほど強く寄せる（1歩ずつ寄せたのと同じになるように）
            float positionKeep = Mathf.Pow(1f - PositionPull, steps);
            float headingKeep = Mathf.Pow(1f - HeadingPull, steps);
            followedPosition = projection.Point + (position - projection.Point) * positionKeep;
            followedHeadingDeg = Wrap360(headingDeg + gap * (1f - headingKeep));
            return true;
        }

        static float Wrap360(float deg)
        {
            deg %= 360f;
            return deg < 0f ? deg + 360f : deg;
        }
    }
}
