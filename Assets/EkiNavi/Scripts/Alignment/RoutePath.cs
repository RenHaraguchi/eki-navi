using System.Collections.Generic;
using UnityEngine;

namespace EkiNavi.Alignment
{
    /// <summary>経路の上の、ある点にいちばん近い場所。</summary>
    public readonly struct RouteProjection
    {
        /// <summary>出発からの、経路に沿った距離（m）。</summary>
        public readonly float Along;

        /// <summary>経路からの距離（m）。</summary>
        public readonly float DistanceFromRoute;

        /// <summary>経路の上のいちばん近い点 (x, z)。</summary>
        public readonly Vector2 Point;

        public RouteProjection(float along, float distanceFromRoute, Vector2 point)
        {
            Along = along;
            DistanceFromRoute = distanceFromRoute;
            Point = point;
        }
    }

    /// <summary>次の曲がり角。</summary>
    public readonly struct RouteTurn
    {
        /// <summary>今いる所から曲がり角までの、経路に沿った距離（m）。</summary>
        public readonly float Distance;

        /// <summary>曲がる角度。右が正、左が負（度）。</summary>
        public readonly float AngleDeg;

        public RouteTurn(float distance, float angleDeg)
        {
            Distance = distance;
            AngleDeg = angleDeg;
        }

        public bool IsRight => AngleDeg > 0f;
    }

    /// <summary>
    /// 経路の折れ線を水平の (x, z) で扱う。浮かぶ矢印が指す先と、次の曲がり角を求めるのに使う。
    /// 座標の向きは地図と同じで、x = 東、z = 北。右回りが正の角度。
    /// </summary>
    public class RoutePath
    {
        readonly List<Vector2> points;
        readonly float[] cumulative;
        readonly List<(float along, float angle)> turns = new List<(float, float)>();

        /// <param name="points">経路の点 (x, z)。2つ以上。</param>
        /// <param name="turnAngleDeg">これ以上向きが変わる所を曲がり角とみなす（度）。</param>
        public RoutePath(IEnumerable<Vector2> points, float turnAngleDeg = 30f)
        {
            this.points = new List<Vector2>();
            foreach (var p in points)
            {
                // 同じ位置の点が続くと向きが決まらないので除く
                if (this.points.Count == 0 || (p - this.points[this.points.Count - 1]).sqrMagnitude > 1e-6f)
                    this.points.Add(p);
            }
            if (this.points.Count < 2) throw new System.ArgumentException("経路には位置の違う点が2つ以上必要です");

            cumulative = new float[this.points.Count];
            for (int i = 1; i < this.points.Count; i++)
                cumulative[i] = cumulative[i - 1] + (this.points[i] - this.points[i - 1]).magnitude;

            for (int i = 1; i < this.points.Count - 1; i++)
            {
                float angle = SignedTurnDeg(this.points[i] - this.points[i - 1], this.points[i + 1] - this.points[i]);
                if (Mathf.Abs(angle) >= turnAngleDeg) turns.Add((cumulative[i], angle));
            }
        }

        /// <summary>経路の長さ（m）。</summary>
        public float Length => cumulative[cumulative.Length - 1];

        /// <summary>点 p にいちばん近い経路の上の場所を返す。</summary>
        public RouteProjection Project(Vector2 p)
        {
            float best = float.MaxValue, bestAlong = 0f;
            Vector2 bestPoint = points[0];
            for (int i = 1; i < points.Count; i++)
            {
                var a = points[i - 1];
                var ab = points[i] - a;
                float len2 = ab.sqrMagnitude;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
                var q = a + ab * t;
                float d = (p - q).magnitude;
                if (d < best)
                {
                    best = d;
                    bestAlong = cumulative[i - 1] + Mathf.Sqrt(len2) * t;
                    bestPoint = q;
                }
            }
            return new RouteProjection(bestAlong, best, bestPoint);
        }

        /// <summary>出発から along メートル進んだ経路の上の点。範囲の外は端の点。</summary>
        public Vector2 PointAt(float along)
        {
            if (along <= 0f) return points[0];
            if (along >= Length) return points[points.Count - 1];
            int i = 1;
            while (cumulative[i] < along) i++;
            float segment = cumulative[i] - cumulative[i - 1];
            float t = segment > 0f ? (along - cumulative[i - 1]) / segment : 0f;
            return Vector2.Lerp(points[i - 1], points[i], t);
        }

        /// <summary>along より先にある、いちばん近い曲がり角。なければ false。</summary>
        public bool TryNextTurn(float along, out RouteTurn turn)
        {
            foreach (var (turnAlong, angle) in turns)
            {
                if (turnAlong > along)
                {
                    turn = new RouteTurn(turnAlong - along, angle);
                    return true;
                }
            }
            turn = default;
            return false;
        }

        /// <summary>
        /// 浮かぶ矢印が指す点。経路の近くにいれば along の lookahead メートル先、
        /// 経路から offRouteDistance より離れていれば、経路のいちばん近い点（戻る向き）。
        /// </summary>
        public Vector2 GuideTarget(Vector2 position, float lookahead, float offRouteDistance, out RouteProjection projection)
        {
            projection = Project(position);
            if (projection.DistanceFromRoute > offRouteDistance) return projection.Point;
            return PointAt(projection.Along + lookahead);
        }

        /// <summary>向き from から向き to への回転。右回りが正（度）。</summary>
        public static float SignedTurnDeg(Vector2 from, Vector2 to)
        {
            // x = 東、z = 北なので、北から東へ回るのが右回り
            float cross = from.x * to.y - from.y * to.x;
            float dot = Vector2.Dot(from, to);
            return -Mathf.Atan2(cross, dot) * Mathf.Rad2Deg;
        }
    }
}
