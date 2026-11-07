using System.Collections.Generic;
using UnityEngine;

namespace EkiNavi.Route
{
    /// <summary>
    /// 経路を床の上に青い帯で描き、進む向きの矢印と、曲がり角の大きな矢印、ゴールの円を置く。
    /// このオブジェクトの位置が出発点、向きが地図の北に対する回転になる（合わせ方は ManualRouteAligner）。
    /// </summary>
    public class RouteRenderer : MonoBehaviour
    {
        [SerializeField] TextAsset routeJson;
        [SerializeField] Material lineMaterial;
        [SerializeField] Material arrowMaterial;

        [Tooltip("床に青い帯と矢印を描く。オフにすると線は描かず、RouteArrow の浮かぶ矢印だけで案内する")]
        [SerializeField] bool drawLine = true;

        [Header("見た目（単位はメートル）")]
        [SerializeField] float lineWidth = 0.4f;
        [SerializeField] float heightAboveFloor = 0.02f;
        [SerializeField] float chevronSpacing = 3f;
        [SerializeField] float turnAngleThreshold = 30f;
        [SerializeField] float goalRadius = 1f;

        [Tooltip("階が1つ違うときの高さの差（仮の値）")]
        [SerializeField] float floorHeight = 5f;

        readonly List<Vector3> points = new();
        GameObject visuals;

        public RouteData Data { get; private set; }
        public IReadOnlyList<Vector3> LocalPoints => points;
        public Vector3 GoalWorldPosition => transform.TransformPoint(points[^1]);

        /// <summary>経路を合わせて表示している間 true。線を描かない設定でも、案内中かどうかを表す。</summary>
        public bool IsVisible { get; private set; }

        void Awake()
        {
            Load();
            Build();
            SetVisible(false);
        }

        public void Load()
        {
            Data = RouteData.Parse(routeJson.text);
            points.Clear();
            points.AddRange(Data.ToLocalPoints(floorHeight));
        }

        public void SetVisible(bool visible)
        {
            IsVisible = visible;
            if (visuals != null) visuals.SetActive(visible && drawLine);
        }

        /// <summary>出発点から minDistance 以上離れた最初の点への向き（水平）。</summary>
        public Vector3 InitialDirection(float minDistance = 2f)
        {
            foreach (var p in points)
            {
                var d = Flat(p - points[0]);
                if (d.magnitude >= minDistance) return d.normalized;
            }
            return Flat(points[^1] - points[0]).normalized;
        }

        /// <summary>ワールド座標 worldPos から、経路に沿ってゴールまで残り何メートルか。</summary>
        public float RemainingDistance(Vector3 worldPos)
        {
            var local = transform.InverseTransformPoint(worldPos);
            float best = float.MaxValue, remaining = 0f;
            float along = 0f, total = 0f;
            for (int i = 1; i < points.Count; i++) total += Flat(points[i] - points[i - 1]).magnitude;

            for (int i = 1; i < points.Count; i++)
            {
                var a = Flat(points[i - 1]);
                var b = Flat(points[i]);
                var ab = b - a;
                float len = ab.magnitude;
                float t = len > 0f ? Mathf.Clamp01(Vector3.Dot(Flat(local) - a, ab) / (len * len)) : 0f;
                float dist = (a + ab * t - Flat(local)).magnitude;
                if (dist < best)
                {
                    best = dist;
                    remaining = total - (along + len * t);
                }
                along += len;
            }
            return remaining;
        }

        void Build()
        {
            visuals = new GameObject("Route Visuals");
            visuals.transform.SetParent(transform, false);

            AddMesh("Line", BuildRibbon(), lineMaterial);
            AddMesh("Arrows", BuildArrows(), arrowMaterial);
        }

        void AddMesh(string objectName, Mesh mesh, Material material)
        {
            var go = new GameObject(objectName);
            go.transform.SetParent(visuals.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        // ---- 帯 ----

        Mesh BuildRibbon()
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            float half = lineWidth * 0.5f;
            int n = points.Count;

            for (int i = 0; i < n; i++)
            {
                var dirPrev = Flat(i > 0 ? points[i] - points[i - 1] : points[1] - points[0]).normalized;
                var dirNext = Flat(i < n - 1 ? points[i + 1] - points[i] : dirPrev).normalized;
                var miter = (Left(dirPrev) + Left(dirNext)).normalized;
                if (miter == Vector3.zero) miter = Left(dirNext);
                float scale = 1f / Mathf.Max(0.5f, Vector3.Dot(miter, Left(dirNext)));

                var p = points[i] + Vector3.up * heightAboveFloor;
                verts.Add(p + miter * half * scale);
                verts.Add(p - miter * half * scale);

                if (i > 0)
                {
                    int a = (i - 1) * 2;
                    tris.AddRange(new[] { a, a + 2, a + 1, a + 1, a + 2, a + 3 });
                }
            }
            return MakeMesh("Route Line", verts, tris);
        }

        // ---- 矢印 ----

        Mesh BuildArrows()
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            float y = heightAboveFloor + 0.005f;

            // 帯の上に、進む向きを示す小さな三角を一定の間隔で置く
            float next = chevronSpacing * 0.5f, along = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                var seg = Flat(points[i] - points[i - 1]);
                float len = seg.magnitude;
                if (len <= 0f) continue;
                var dir = seg / len;
                while (next <= along + len)
                {
                    var pos = points[i - 1] + (points[i] - points[i - 1]) * ((next - along) / len);
                    AddTriangleArrow(verts, tris, pos + Vector3.up * y, dir, 0.3f);
                    next += chevronSpacing;
                }
                along += len;
            }

            // 曲がり角の先に、大きな矢印を置く
            for (int i = 1; i < points.Count - 1; i++)
            {
                var dirIn = Flat(points[i] - points[i - 1]).normalized;
                var dirOut = Flat(points[i + 1] - points[i]).normalized;
                if (Vector3.Angle(dirIn, dirOut) < turnAngleThreshold) continue;
                float segLen = Flat(points[i + 1] - points[i]).magnitude;
                var pos = points[i] + dirOut * Mathf.Min(1.2f, segLen * 0.5f);
                AddBigArrow(verts, tris, pos + Vector3.up * (y + 0.005f), dirOut, 1.2f);
            }

            // ゴールの円
            AddDisc(verts, tris, points[^1] + Vector3.up * y, goalRadius, 32);

            return MakeMesh("Route Arrows", verts, tris);
        }

        static void AddTriangleArrow(List<Vector3> v, List<int> t, Vector3 c, Vector3 f, float size)
        {
            var r = Right(f);
            int b = v.Count;
            v.Add(c + f * (0.6f * size));
            v.Add(c + r * (0.5f * size) - f * (0.4f * size));
            v.Add(c - r * (0.5f * size) - f * (0.4f * size));
            t.AddRange(new[] { b, b + 1, b + 2 });
        }

        static void AddBigArrow(List<Vector3> v, List<int> t, Vector3 c, Vector3 f, float size)
        {
            var r = Right(f);
            Vector3 P(float x, float z) => c + r * (x * size) + f * (z * size);
            int b = v.Count;
            // 軸
            v.Add(P(-0.1f, -0.5f)); v.Add(P(0.1f, -0.5f)); v.Add(P(-0.1f, 0.1f)); v.Add(P(0.1f, 0.1f));
            t.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
            // 頭
            v.Add(P(0f, 0.5f)); v.Add(P(0.3f, 0.1f)); v.Add(P(-0.3f, 0.1f));
            t.AddRange(new[] { b + 4, b + 5, b + 6 });
        }

        static void AddDisc(List<Vector3> v, List<int> t, Vector3 c, float radius, int segments)
        {
            int center = v.Count;
            v.Add(c);
            for (int k = 0; k <= segments; k++)
            {
                float a = Mathf.PI * 2f * k / segments;
                v.Add(c + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius);
                if (k > 0) t.AddRange(new[] { center, center + k + 1, center + k });
            }
        }

        static Mesh MakeMesh(string meshName, List<Vector3> verts, List<int> tris)
        {
            var mesh = new Mesh { name = meshName };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static Vector3 Flat(Vector3 v) => new(v.x, 0f, v.z);
        static Vector3 Left(Vector3 dir) => new(-dir.z, 0f, dir.x);
        static Vector3 Right(Vector3 dir) => new(dir.z, 0f, -dir.x);

        // エディターのシーンビューで、再生しなくても経路の形が見えるようにする
        void OnDrawGizmos()
        {
            if (routeJson == null) return;
            List<Vector3> pts;
            try { pts = points.Count > 0 ? points : RouteData.Parse(routeJson.text).ToLocalPoints(floorHeight); }
            catch { return; }
            Gizmos.color = new Color(0f, 0.38f, 1f);
            for (int i = 1; i < pts.Count; i++)
                Gizmos.DrawLine(transform.TransformPoint(pts[i - 1]), transform.TransformPoint(pts[i]));
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(transform.TransformPoint(pts[0]), 0.5f);
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(transform.TransformPoint(pts[^1]), 0.5f);
        }
    }
}
