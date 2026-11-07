using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace EkiNavi.Route
{
    /// <summary>
    /// 手動の位置合わせ（看板認識ができるまでの仮のもの）。
    /// 出発の改札に立ち、進む方向にスマホを向けた状態で、経路の始まりをカメラの足元に、
    /// 最初の進行方向をカメラの向きに合わせる。
    /// - AR の追跡が始まって少したったら自動で1回合わせる
    /// - 2本指でタップ（エディターでは R キー）すると合わせ直す
    /// </summary>
    [RequireComponent(typeof(RouteRenderer))]
    public class ManualRouteAligner : MonoBehaviour
    {
        [SerializeField] Transform cameraTransform;
        [Tooltip("あれば、カメラの下にある水平な面を床の高さに使う")]
        [SerializeField] ARPlaneManager planeManager;
        [Tooltip("床が見つからないときに使う、床からカメラまでの高さ")]
        [SerializeField] float eyeHeight = 1.4f;
        [SerializeField] float alignDelayAfterTracking = 1f;
        [SerializeField] float arrivalRadius = 5f;

        RouteRenderer route;
        bool aligned;
        float trackingSince = -1f;
        float remaining = -1f;

        public bool Arrived { get; private set; }

        void Awake()
        {
            route = GetComponent<RouteRenderer>();
        }

        void Update()
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            if (cameraTransform == null) return;

            if (!aligned && ARSession.state == ARSessionState.SessionTracking)
            {
                if (trackingSince < 0f) trackingSince = Time.time;
                if (Time.time - trackingSince >= alignDelayAfterTracking) Align();
            }

            if (RealignRequested()) Align();

            if (aligned)
            {
                remaining = route.RemainingDistance(cameraTransform.position);
                var toGoal = route.GoalWorldPosition - cameraTransform.position;
                toGoal.y = 0f;
                if (!Arrived && toGoal.magnitude <= arrivalRadius)
                {
                    Arrived = true;
                    Debug.Log($"[EkiNavi] {route.Data.goal.name} に到着しました");
                }
            }
        }

        static bool RealignRequested()
        {
            var ts = Touchscreen.current;
            if (ts != null && ts.touches.Count > 1 && ts.touches[1].press.wasPressedThisFrame) return true;
            var kb = Keyboard.current;
            return kb != null && kb.rKey.wasPressedThisFrame;
        }

        public void Align()
        {
            var cam = cameraTransform.position;
            var forward = cameraTransform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-4f) forward = cameraTransform.up; // 真下を向いているとき
            forward.y = 0f;

            float yaw = Vector3.SignedAngle(route.InitialDirection(), forward.normalized, Vector3.up);
            transform.SetPositionAndRotation(new Vector3(cam.x, FloorHeight(cam), cam.z), Quaternion.Euler(0f, yaw, 0f));

            aligned = true;
            Arrived = false;
            route.SetVisible(true);
            if (TryGetComponent<RouteMotionGuard>(out var guard)) guard.OnAligned();
            Debug.Log($"[EkiNavi] 経路を合わせました: {route.Data.start.name} → {route.Data.goal.name}（{route.Data.lengthMeters:0}m）");
        }

        float FloorHeight(Vector3 cam)
        {
            float best = cam.y - eyeHeight;
            if (planeManager == null) return best;

            float bestGap = float.MaxValue;
            foreach (var plane in planeManager.trackables)
            {
                if (plane.alignment != PlaneAlignment.HorizontalUp) continue;
                float gap = cam.y - plane.transform.position.y;
                // カメラより 0.8〜2.5m 下にある面だけを床とみなす（机などを避ける）
                if (gap < 0.8f || gap > 2.5f) continue;
                if (Mathf.Abs(gap - eyeHeight) < bestGap)
                {
                    bestGap = Mathf.Abs(gap - eyeHeight);
                    best = plane.transform.position.y;
                }
            }
            return best;
        }

        // 試作用の簡単な表示。画面の文字は英数字だけにしている（端末によって日本語のフォントがないため）
        void OnGUI()
        {
            var style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(Screen.height * 0.025f) };
            style.normal.textColor = Color.white;
            string text = !aligned
                ? "Point the camera at the floor and walking direction..."
                : Arrived ? "Arrived!" : $"Remaining: {remaining:0} m   (2-finger tap to re-align)";
            GUI.Label(new Rect(20, 40, Screen.width - 40, Screen.height * 0.1f), text, style);
        }
    }
}
