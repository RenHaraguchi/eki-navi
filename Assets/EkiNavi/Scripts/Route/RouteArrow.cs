using System.Collections.Generic;
using System.Linq;
using EkiNavi.Alignment;
using UnityEngine;

namespace EkiNavi.Route
{
    /// <summary>
    /// カメラの前に大きな矢印を1つ浮かべ、経路に沿って進む向きを指す。床の線の代わりに使う。
    ///
    /// - 置き場所: カメラの向いている方向へ distance メートル先。スマホを下に向けて足元を映していても画面に入る
    /// - 向き: 経路の上で、自分より lookahead メートル先の点を指す。曲がり角に近づくと自然に向きが変わる
    /// - 経路から offRouteDistance メートルより離れたら、経路のいちばん近い点（戻る向き）を指す
    /// - 画面の下に、次の曲がり角までの距離とゴールまでの距離を出す
    /// - 経路を表示していない間（案内板にかざす前、追跡が止まったとき）は出さない
    ///
    /// 位置のずれが 1〜2m あっても、向きが大きく変わらないので、床の線より目立ちにくい。
    /// </summary>
    [RequireComponent(typeof(RouteRenderer))]
    public class RouteArrow : MonoBehaviour
    {
        [SerializeField] Transform cameraTransform;
        [Tooltip("なければ明るい青の Unlit を作る")]
        [SerializeField] Material arrowMaterial;

        [Header("置き方（単位はメートル、数値は仮）")]
        [Tooltip("カメラから矢印までの距離")]
        [SerializeField] float distance = 1.2f;
        [Tooltip("画面の中央から少し上にずらす量。足元が矢印で隠れないようにする")]
        [SerializeField] float raise = 0.12f;
        [SerializeField] float arrowLength = 0.32f;
        [Tooltip("前を向いて持ったとき、矢印の表がカメラに見えるように先を上げる最大の角度（度）")]
        [SerializeField] float maxTilt = 40f;
        [Tooltip("向きを変えるときの速さ。大きいほどすぐ向く")]
        [SerializeField] float turnSpeed = 6f;

        [Header("案内（単位はメートル、数値は仮）")]
        [SerializeField] float lookahead = 4f;
        // 3m だと、看板の位置や経路の線の数 m のずれだけで「外れた」と出たので 6m にした（2026-11-09 の現地の試験）
        [SerializeField] float offRouteDistance = 6f;
        // 5m だと改札のすぐ前まで行かないと到着にならなかったので、3m 手前で着くよう 8m にした（2026-11-10 の現地の試験）
        [SerializeField] float arrivalRadius = 8f;
        [Tooltip("着いたとみなす、経路からの横の距離の上限（m）。外れの判定とは別に、狭くしておく")]
        [SerializeField] float arrivalLateral = 4.5f;
        [SerializeField] float turnAngleThreshold = 30f;

        [Header("開発用")]
        [SerializeField] bool showText = true;

        RouteRenderer route;
        RouteMotionGuard motionGuard;
        DeadReckoningTracker tracker;
        RoutePath path;
        GameObject arrow;
        float currentYaw;
        bool hasYaw;
        string guidance = "";

        /// <summary>矢印で案内している最中か（合わせ済みで、まだ着いていない）。EkiNaviUI が読む。</summary>
        public bool IsGuiding { get; private set; }
        /// <summary>ゴールに着いたか。</summary>
        public bool IsArrived { get; private set; }
        /// <summary>ゴールまでの残りの距離（m）。</summary>
        public float RemainingMeters { get; private set; }
        /// <summary>経路の長さ（m）。</summary>
        public float TotalMeters => path != null ? path.Length : 0f;
        /// <summary>次の曲がり角があるか、そこまでの距離（m）と向き。</summary>
        public bool HasNextTurn { get; private set; }
        public float NextTurnMeters { get; private set; }
        public bool NextTurnIsRight { get; private set; }
        /// <summary>経路から外れているか、経路までの距離（m）。</summary>
        public bool IsOffRoute { get; private set; }
        public float OffRouteMeters { get; private set; }
        /// <summary>利用者の向きが、矢印の向きから大きくずれているか。</summary>
        public bool IsFacingAway { get; private set; }
        /// <summary>画面の下の英語の案内文を出すか（EkiNaviUI があるときは隠す）。</summary>
        public bool ShowText { get => showText; set => showText = value; }
        /// <summary>横向きで撮って合わせ、縦に戻すのを待っている（矢印は出さない）。</summary>
        // 経路を隠しているとき（位置を見失った）は、縦に戻す案内ではなく見失った案内を出す
        public bool WaitingForPortrait => tracker != null && tracker.WaitingForPortrait && route != null && route.IsVisible;

        /// <summary>着いた状態を消す（UI で最初に戻ったとき）。</summary>
        public void ResetArrival() => IsArrived = false;
        GUIStyle style;

        void Awake()
        {
            route = GetComponent<RouteRenderer>();
            motionGuard = GetComponent<RouteMotionGuard>();
            tracker = GetComponent<DeadReckoningTracker>();
        }

        void Start()
        {
            // RouteRenderer.Awake で経路を読んだあとに作る
            path = new RoutePath(route.LocalPoints.Select(p => new Vector2(p.x, p.z)), turnAngleThreshold);
            arrow = CreateArrow();
            arrow.SetActive(false);
        }

        void OnDestroy()
        {
            if (arrow != null) Destroy(arrow);
        }

        void LateUpdate()
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            if (cameraTransform == null || path == null) return;

            if (!route.IsVisible)
            {
                arrow.SetActive(false);
                hasYaw = false;
                guidance = "";
                IsGuiding = false;
                return;
            }

            if (WaitingForPortrait)
            {
                // 縦に戻すまでは、向きが確かでないので矢印を出さない
                arrow.SetActive(false);
                hasYaw = false;
                guidance = "Hold the phone upright";
                return;
            }

            // 経路の座標（RouteRenderer の中の座標）で、いまの位置と指す先を求める。
            // 推測航法が使えるときは、ジャイロと歩数で進めた位置を使う（ARCore が見失っても崩れない）
            bool reckoning = tracker != null && tracker.Active;
            Vector2 position;
            if (reckoning)
            {
                position = tracker.LocalPosition;
            }
            else
            {
                var local = route.transform.InverseTransformPoint(cameraTransform.position);
                position = new Vector2(local.x, local.z);
            }
            var target = path.GuideTarget(position, lookahead, offRouteDistance, out var projection);
            float remaining = path.Length - projection.Along;

            if (remaining <= arrivalRadius && projection.DistanceFromRoute <= arrivalLateral)
            {
                arrow.SetActive(false);
                guidance = "Arrived";
                IsGuiding = false;
                IsArrived = true;
                RemainingMeters = 0f;
                return;
            }

            var toTarget = target - position;
            if (toTarget.sqrMagnitude < 1e-4f) toTarget = path.PointAt(projection.Along + lookahead + 1f) - position;
            float yaw;
            if (reckoning)
            {
                // 経路の座標での向きと、今のスマホの向きの差を、カメラの向きに足す
                if (toTarget.sqrMagnitude < 1e-6f) return;
                yaw = tracker.ArrowWorldYaw(Azimuth.Of(toTarget));
            }
            else
            {
                var worldDir = route.transform.TransformDirection(new Vector3(toTarget.x, 0f, toTarget.y));
                worldDir.y = 0f;
                if (worldDir.sqrMagnitude < 1e-6f) return;
                yaw = Mathf.Atan2(worldDir.x, worldDir.z) * Mathf.Rad2Deg;
            }
            IsFacingAway = FacingCheck.IsAway(IsFacingAway, FacingCheck.ErrorDeg(yaw, UserYaw()));
            currentYaw = hasYaw ? Mathf.LerpAngle(currentYaw, yaw, 1f - Mathf.Exp(-turnSpeed * Time.deltaTime)) : yaw;
            hasYaw = true;

            // カメラの見ている方向の先に置く。下を向いていれば足元の少し先、前を向いていれば目の前
            var cam = cameraTransform;
            arrow.transform.position = cam.position + cam.forward * distance + cam.up * raise;

            // 下を向いているほど水平に、前を向いているほど先を上げて、表がカメラに見えるようにする
            float pitchDown = Vector3.Angle(Vector3.ProjectOnPlane(cam.forward, Vector3.up), cam.forward);
            if (cam.forward.y > 0f) pitchDown = 0f;
            float tilt = Mathf.Clamp((90f - pitchDown) * 0.5f, 0f, maxTilt);
            arrow.transform.rotation = Quaternion.Euler(-tilt, currentYaw, 0f);
            arrow.SetActive(true);

            guidance = Describe(projection, remaining);
        }

        /// <summary>利用者が向いている方向（度）。真下や真上を向いているときは、カメラの上の向きで見る。</summary>
        float UserYaw()
        {
            var forward = cameraTransform.forward;
            var direction = Mathf.Abs(forward.y) > 0.85f
                ? cameraTransform.up * (forward.y < 0f ? 1f : -1f)
                : forward;
            return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        }

        string Describe(RouteProjection projection, float remaining)
        {
            IsGuiding = true;
            RemainingMeters = remaining;
            IsOffRoute = projection.DistanceFromRoute > offRouteDistance;
            OffRouteMeters = projection.DistanceFromRoute;
            HasNextTurn = path.TryNextTurn(projection.Along, out var nextTurn) && nextTurn.Distance < remaining;
            NextTurnMeters = HasNextTurn ? nextTurn.Distance : 0f;
            NextTurnIsRight = HasNextTurn && nextTurn.IsRight;
            if (projection.DistanceFromRoute > offRouteDistance)
                return $"Off route ({projection.DistanceFromRoute:0} m). Follow the arrow back";
            string next = path.TryNextTurn(projection.Along, out var turn) && turn.Distance < remaining
                ? $"Turn {(turn.IsRight ? "right" : "left")} in {turn.Distance:0} m"
                : "Go straight";
            return $"{next}   Goal {remaining:0} m";
        }

        GameObject CreateArrow()
        {
            var go = new GameObject("EkiNavi Floating Arrow");
            go.AddComponent<MeshFilter>().sharedMesh = BuildArrowMesh(arrowLength);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = arrowMaterial != null ? arrowMaterial : CreateDefaultMaterial();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            return go;
        }

        static Material CreateDefaultMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            var material = new Material(shader);
            material.SetColor("_BaseColor", new Color32(0x2F, 0x80, 0xED, 0xFF));  // パレットの青
            return material;
        }

        /// <summary>+Z を指す厚みのある矢印。中心が原点。</summary>
        static Mesh BuildArrowMesh(float length)
        {
            float l = length, w = length * 0.6f, shaftW = length * 0.22f, headL = length * 0.45f, thick = length * 0.12f;
            // 輪郭（上から見て、時計回り）
            var outline = new List<Vector2>
            {
                new Vector2(0f, l * 0.5f),
                new Vector2(w * 0.5f, l * 0.5f - headL),
                new Vector2(shaftW * 0.5f, l * 0.5f - headL),
                new Vector2(shaftW * 0.5f, -l * 0.5f),
                new Vector2(-shaftW * 0.5f, -l * 0.5f),
                new Vector2(-shaftW * 0.5f, l * 0.5f - headL),
                new Vector2(-w * 0.5f, l * 0.5f - headL),
            };

            var verts = new List<Vector3>();
            var tris = new List<int>();
            float top = thick * 0.5f, bottom = -thick * 0.5f;

            // 上の面と下の面（頭の三角と軸の四角）
            void Face(float y, bool up)
            {
                int b = verts.Count;
                foreach (var p in outline) verts.Add(new Vector3(p.x, y, p.y));
                int[] faces = { 0, 1, 6, 2, 3, 4, 2, 4, 5 };
                for (int i = 0; i < faces.Length; i += 3)
                {
                    if (up) tris.AddRange(new[] { b + faces[i], b + faces[i + 1], b + faces[i + 2] });
                    else tris.AddRange(new[] { b + faces[i], b + faces[i + 2], b + faces[i + 1] });
                }
            }
            Face(top, true);
            Face(bottom, false);

            // 側面
            for (int i = 0; i < outline.Count; i++)
            {
                var a = outline[i];
                var c = outline[(i + 1) % outline.Count];
                int b = verts.Count;
                verts.Add(new Vector3(a.x, top, a.y));
                verts.Add(new Vector3(c.x, top, c.y));
                verts.Add(new Vector3(a.x, bottom, a.y));
                verts.Add(new Vector3(c.x, bottom, c.y));
                // 輪郭は上から見て時計回りなので、外から見て時計回りになるのはこの順
                tris.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
            }

            var mesh = new Mesh { name = "Floating Arrow" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // 画面の文字は英数字だけにしている（端末によって日本語のフォントがないため）
        void OnGUI()
        {
            if (!showText || string.IsNullOrEmpty(guidance)) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.box)
                {
                    fontSize = Mathf.RoundToInt(Screen.height * 0.03f),
                    alignment = TextAnchor.MiddleCenter,
                };
                style.normal.textColor = Color.white;
            }
            string text = guidance;
            if (motionGuard != null && motionGuard.DriftSuspected)
                text += "\nPosition may be off. Re-align at a nearby sign";
            float h = Screen.height * (text.Contains("\n") ? 0.12f : 0.07f);
            GUI.Box(new Rect(20, Screen.height * 0.78f - h * 0.5f, Screen.width - 40, h), text, style);
        }
    }
}
