using EkiNavi.Alignment;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EkiNavi.Route
{
    /// <summary>
    /// 人混みで ARCore の位置がずれたときに、経路を同じだけ動かして打ち消す（中身は MotionGuard）。
    /// 経路を表示している間だけ働く。合わせ直したら、aligner が OnAligned を呼ぶ。
    ///
    /// 端末に加速度センサーがないとき（エディターなど）は何もしない。
    /// </summary>
    [RequireComponent(typeof(RouteRenderer))]
    public class RouteMotionGuard : MonoBehaviour
    {
        [SerializeField] Transform cameraTransform;
        [SerializeField] MotionGuardSettings settings = new MotionGuardSettings();

        [Header("開発用")]
        [Tooltip("画面の左下に、止まっているかの判定と歩数を出す")]
        [SerializeField] bool showStatus = true;

        RouteRenderer route;
        MotionGuard guard;
        bool wasVisible;
        float gravityScale = -1f; // 加速度の値を g に直す係数（m/s² で来る端末なら 1/9.81）
        GUIStyle style;

        public bool IsStill => guard != null && guard.IsStill;
        /// <summary>合わせてから数えた歩数（DeadReckoningTracker が使う）。</summary>
        public int Steps => guard != null ? guard.Steps : 0;
        /// <summary>歩幅（m）。</summary>
        public float StrideMeters => settings.strideMeters;
        public bool DriftSuspected => guard != null && guard.DriftSuspected(Time.time);

        void Awake()
        {
            route = GetComponent<RouteRenderer>();
            guard = new MotionGuard(settings);
        }

        void OnEnable()
        {
            if (Accelerometer.current != null) InputSystem.EnableDevice(Accelerometer.current);
        }

        /// <summary>経路を合わせ直したときに呼ぶ。打ち消しの基準をいまの位置にする。</summary>
        public void OnAligned()
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            if (cameraTransform != null) guard.Reset(Flat(cameraTransform.position));
        }

        void Update()
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            var accelerometer = Accelerometer.current;
            if (cameraTransform == null || accelerometer == null || !accelerometer.enabled) return;

            float magnitude = accelerometer.acceleration.ReadValue().magnitude;
            if (magnitude <= 0f) return;
            if (gravityScale < 0f) gravityScale = magnitude > 3f ? 1f / 9.81f : 1f;

            var camera = Flat(cameraTransform.position);
            var compensation = guard.Update(Time.time, magnitude * gravityScale, camera);

            bool visible = route.IsVisible;
            if (visible && !wasVisible) guard.Reset(camera);
            wasVisible = visible;
            if (!visible) return;

            if (compensation != Vector2.zero)
                transform.position += new Vector3(compensation.x, 0f, compensation.y);
        }

        static Vector2 Flat(Vector3 v) => new Vector2(v.x, v.z);

        // 画面の文字は英数字だけにしている（端末によって日本語のフォントがないため）
        void OnGUI()
        {
            if (!showStatus || !DevOverlay.Visible || guard == null) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(Screen.height * 0.018f) };
                style.normal.textColor = Color.white;
            }
            string text = Accelerometer.current == null
                ? "Motion guard: no accelerometer"
                : $"{(guard.IsStill ? "STILL" : "moving")}  accel sd {guard.AccelerationStdG:0.000}g  steps {guard.Steps}  " +
                  $"AR {guard.RecentArDistance:0.0}m/s  allowed {guard.AllowedSpeed:0.0}m/s  cancelled {guard.TotalCompensatedMeters:0.0}m";
            float h = Screen.height * 0.05f;
            GUI.Label(new Rect(20, Screen.height - h - 10, Screen.width - 40, h), text, style);
        }
    }
}
