using System.Globalization;
using System.IO;
using System.Text;
using EkiNavi.Alignment;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace EkiNavi.Route
{
    /// <summary>
    /// 歩数計測の確認用（実機で、駅の通路を歩いて使う）。
    /// 歩数 × 歩幅で求めた距離と、ARCore のカメラが動いた距離を画面に並べ、1 秒ごとに CSV に書く。
    /// 歩数の数え方は RouteMotionGuard と同じ MotionGuard を使う（打ち消しはしない。数えるだけ）。
    ///
    /// CSV: Application.persistentDataPath/steps_yyyyMMdd_HHmmss.csv
    ///   取り出し: adb pull /sdcard/Android/data/com.ekinavi.app/files/
    ///
    /// 画面の文字は英語（外国人向けのアプリのため）。
    /// </summary>
    public class StepProbe : MonoBehaviour
    {
        [SerializeField] Transform cameraTransform;
        [SerializeField] MotionGuardSettings settings = new MotionGuardSettings();
        [Tooltip("この距離以上カメラが動いたら AR の距離に足す（m）")]
        [SerializeField] float minSegmentMeters = 0.25f;

        MotionGuard guard;
        StepDistanceProbe probe;
        float gravityScale = -1f;
        float stride;
        bool wasTracking;
        float nextLog;
        int marks;
        StreamWriter log;
        string logPath;
        GUIStyle label, button;

        void Awake()
        {
            guard = new MotionGuard(settings);
            probe = new StepDistanceProbe(minSegmentMeters);
            stride = settings.strideMeters;
        }

        void OnEnable()
        {
            if (Accelerometer.current != null) InputSystem.EnableDevice(Accelerometer.current);
            logPath = Path.Combine(Application.persistentDataPath, $"steps_{System.DateTime.Now:yyyyMMdd_HHmmss}.csv");
            log = new StreamWriter(logPath, false, new UTF8Encoding(false));
            log.WriteLine("time_s,steps,stride_m,step_dist_m,ar_dist_m,diff_m,fitted_stride_m,cam_x,cam_z,tracking,mark");
            log.Flush();
        }

        void OnDisable()
        {
            log?.Dispose();
            log = null;
        }

        void Update()
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            if (cameraTransform == null) return;
            var cam = new Vector2(cameraTransform.position.x, cameraTransform.position.z);

            var accelerometer = Accelerometer.current;
            if (accelerometer != null && accelerometer.enabled)
            {
                float magnitude = accelerometer.acceleration.ReadValue().magnitude;
                if (magnitude > 0f)
                {
                    if (gravityScale < 0f) gravityScale = magnitude > 3f ? 1f / 9.81f : 1f;
                    guard.Update(Time.time, magnitude * gravityScale, cam); // 歩数を数えるためだけに呼ぶ
                }
            }

            bool tracking = ARSession.state == ARSessionState.SessionTracking;
            if (tracking && !wasTracking) probe.Rebase(cam);
            if (tracking) probe.AddPosition(cam);
            wasTracking = tracking;

            if (Time.time >= nextLog)
            {
                nextLog = Time.time + 1f;
                WriteRow(cam, tracking, "");
            }
        }

        void WriteRow(Vector2 cam, bool tracking, string mark)
        {
            if (log == null) return;
            int now = guard.Steps;
            float stepDist = probe.StepDistance(now, stride);
            var c = CultureInfo.InvariantCulture;
            log.WriteLine(string.Join(",",
                Time.time.ToString("F2", c), probe.Steps(now).ToString(c), stride.ToString("F3", c), stepDist.ToString("F2", c),
                probe.ArDistance.ToString("F2", c), (stepDist - probe.ArDistance).ToString("F2", c),
                probe.FittedStride(now).ToString("F3", c), cam.x.ToString("F2", c), cam.y.ToString("F2", c),
                tracking ? "1" : "0", mark));
            log.Flush();
        }

        Vector2 CameraFlat() => cameraTransform == null ? Vector2.zero : new Vector2(cameraTransform.position.x, cameraTransform.position.z);

        void OnGUI()
        {
            float u = Mathf.Max(Screen.width, Screen.height) / 64f;
            label ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, wordWrap = true };
            button ??= new GUIStyle(GUI.skin.button);
            label.fontSize = Mathf.RoundToInt(u * 0.9f);
            button.fontSize = Mathf.RoundToInt(u * 0.9f);
            label.normal.textColor = Color.white;

            int now = guard.Steps;
            float stepDist = probe.StepDistance(now, stride), ar = probe.ArDistance, diff = stepDist - ar;
            float fitted = probe.FittedStride(now);
            string state = ARSession.state == ARSessionState.SessionTracking ? "tracking" : ARSession.state.ToString();
            string text =
                $"STEP PROBE  (AR: {state}{(Accelerometer.current == null ? ", no accelerometer" : "")})\n" +
                $"Steps: {probe.Steps(now)}\n" +
                $"Step distance: {stepDist:F1} m  (stride {stride:F2} m)\n" +
                $"AR distance:   {ar:F1} m\n" +
                $"Difference:    {diff:+0.0;-0.0} m  ({(ar > 1f ? diff / ar * 100f : 0f):+0;-0} %)\n" +
                $"Fitted stride: {(fitted > 0f ? fitted.ToString("F2") + " m" : "walk 20+ steps")}\n" +
                $"Log: {Path.GetFileName(logPath)}  marks {marks}";
            float w = Screen.width - u * 2, h = u * 9.5f;
            GUI.Box(new Rect(u, u * 2, w, h), text, label);

            float bw = (w - u * 3) / 4f, bh = u * 3f, by = Screen.height - bh - u * 2;
            if (GUI.Button(new Rect(u, by, bw, bh), "Reset", button)) { probe.Reset(CameraFlat(), now); marks = 0; WriteRow(CameraFlat(), wasTracking, "reset"); }
            if (GUI.Button(new Rect(u * 2 + bw, by, bw, bh), "Mark", button)) { marks++; WriteRow(CameraFlat(), wasTracking, "mark" + marks); }
            if (GUI.Button(new Rect(u * 3 + bw * 2, by, bw, bh), "Stride -", button)) stride = Mathf.Max(0.3f, stride - 0.02f);
            if (GUI.Button(new Rect(u * 4 + bw * 3, by, bw, bh), "Stride +", button)) stride = Mathf.Min(1.2f, stride + 0.02f);
            if (fitted > 0f && GUI.Button(new Rect(u, by - bh - u, w, bh), $"Use fitted stride ({fitted:F2} m)", button)) stride = fitted;
        }
    }
}
