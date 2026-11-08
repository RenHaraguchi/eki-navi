using EkiNavi.Alignment;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using Gyro = UnityEngine.InputSystem.Gyroscope;

namespace EkiNavi.Route
{
    /// <summary>
    /// 看板で合わせたあとの自分の位置を、ジャイロ（向き）と歩数×歩幅（距離）で進める（歩行者の推測航法）。
    /// RouteArrow はこの位置から経路の先を指す。
    ///
    /// 地下では ARCore が数秒〜十数秒ごとに位置を見失って座標を作り直すことがあり（2026-11-08 の現地検証）、
    /// ARCore の位置に頼ると矢印が消えたりずれたりする。ジャイロと歩数は映像を使わないので、その影響を受けない。
    ///
    /// - 起点: 看板で合わせた瞬間のカメラの位置と、歩く向き（経路の座標）
    /// - 向き: GyroHeading で、重力のまわりの回転を積み上げる
    /// - 距離: RouteMotionGuard が数えた歩数 × 歩幅
    /// - ジャイロの回る向き（符号）は端末によって逆になりうるので、ARCore が追跡できている間に、
    ///   ARCore の回り方と比べて自動で決め、端末に覚えておく
    ///
    /// 端末にジャイロがないとき（エディターなど）は働かず、RouteArrow は ARCore の位置を使う。
    /// </summary>
    [RequireComponent(typeof(RouteRenderer))]
    public class DeadReckoningTracker : MonoBehaviour
    {
        const string SignKey = "EkiNavi.GyroSign";

        [SerializeField] Transform cameraTransform;
        [SerializeField] bool useDeadReckoning = true;
        [Tooltip("ジャイロの符号を決めるのに要る、ARCore で測った回転の合計（度）")]
        [SerializeField] float signDecisionTurnDeg = 90f;
        [Header("開発用")]
        [SerializeField] bool showStatus = true;

        RouteRenderer route;
        RouteMotionGuard motion;
        readonly DeadReckoning reckoning = new DeadReckoning();
        readonly GyroHeading heading = new GyroHeading(1f);
        bool aligned;
        bool sensorsReady;
        int lastSteps;
        float lastTime = -1f;

        bool signDecided;
        float agreement, measuredTurn;
        float lastCameraYaw;
        bool hasCameraYaw;
        GUIStyle style;

        /// <summary>推測航法の位置を使えるか（合わせ済みで、ジャイロがある）。</summary>
        public bool Active => useDeadReckoning && aligned && sensorsReady;

        /// <summary>経路の座標での今の位置 (x, z)。</summary>
        public Vector2 LocalPosition => reckoning.Position;

        /// <summary>経路の座標での、スマホの歩く向き（北から時計回りの度）。</summary>
        public float HeadingDeg => heading.HeadingDeg;

        void Awake()
        {
            route = GetComponent<RouteRenderer>();
            motion = GetComponent<RouteMotionGuard>();
            float saved = PlayerPrefs.GetFloat(SignKey, 0f);
            if (saved != 0f)
            {
                heading.Sign = saved;
                signDecided = true;
            }
        }

        void OnEnable()
        {
            if (Gyro.current != null) InputSystem.EnableDevice(Gyro.current);
            if (GravitySensor.current != null) InputSystem.EnableDevice(GravitySensor.current);
        }

        /// <summary>看板で合わせたときに aligner が呼ぶ。今のカメラの位置と向きを起点にする。</summary>
        public void OnAligned()
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            if (cameraTransform == null) return;
            var local = route.transform.InverseTransformPoint(cameraTransform.position);
            reckoning.Reset(new Vector2(local.x, local.z));
            heading.Reset(LocalWalkAzimuth());
            lastSteps = motion != null ? motion.Steps : 0;
            aligned = true;
        }

        /// <summary>
        /// 経路の座標で desiredAzimuth の向きを指す矢印の、ワールドでの向き（度）。
        /// 今のスマホの向きとの差をカメラに足すので、ARCore の座標が作り直されても崩れない。
        /// </summary>
        public float ArrowWorldYaw(float desiredAzimuth) =>
            CameraWalkYaw() + Mathf.DeltaAngle(heading.HeadingDeg, desiredAzimuth);

        float CameraWalkYaw()
        {
            return DeadReckoning.TryWalkDirection(cameraTransform.forward, cameraTransform.up, out var d)
                ? Azimuth.Of(d)
                : cameraTransform.eulerAngles.y;
        }

        float LocalWalkAzimuth()
        {
            if (!DeadReckoning.TryWalkDirection(cameraTransform.forward, cameraTransform.up, out var d)) return heading.HeadingDeg;
            var local = route.transform.InverseTransformDirection(new Vector3(d.x, 0f, d.y));
            return Azimuth.Of(new Vector2(local.x, local.z));
        }

        void Update()
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            var gyro = Gyro.current;
            sensorsReady = gyro != null && gyro.enabled && cameraTransform != null;
            if (!sensorsReady) return;

            float now = Time.time;
            float dt = lastTime < 0f ? 0f : now - lastTime;
            lastTime = now;

            var angular = gyro.angularVelocity.ReadValue();
            var gravity = GravitySensor.current != null && GravitySensor.current.enabled
                ? GravitySensor.current.gravity.ReadValue()
                : (Accelerometer.current != null ? Accelerometer.current.acceleration.ReadValue() : Vector3.zero);
            heading.Update(angular, gravity, dt);

            DecideSign(angular, gravity, dt);

            if (motion != null)
            {
                int steps = motion.Steps;
                if (steps < lastSteps) lastSteps = steps; // RouteMotionGuard が数え直した
                if (aligned && steps > lastSteps) reckoning.Advance(steps - lastSteps, heading.HeadingDeg, motion.StrideMeters);
                lastSteps = steps;
            }
        }

        /// <summary>ARCore が追跡できている間に、ジャイロと ARCore の回る向きを比べて符号を決める。</summary>
        void DecideSign(Vector3 angular, Vector3 gravity, float dt)
        {
            float cameraYaw = CameraWalkYaw();
            bool tracking = ARSession.state == ARSessionState.SessionTracking;
            if (!signDecided && tracking && hasCameraYaw && dt > 0f)
            {
                float arRate = Mathf.DeltaAngle(lastCameraYaw, cameraYaw) / dt;
                if (Mathf.Abs(arRate) < 360f) // 座標の作り直しで飛んだ所は使わない
                {
                    agreement += arRate * GyroHeading.YawRateDeg(angular, gravity) * dt;
                    measuredTurn += Mathf.Abs(arRate) * dt;
                    if (measuredTurn >= signDecisionTurnDeg)
                    {
                        heading.Sign = agreement >= 0f ? 1f : -1f;
                        signDecided = true;
                        PlayerPrefs.SetFloat(SignKey, heading.Sign);
                        PlayerPrefs.Save();
                        // 決まるまでの向きは符号が違っていたかもしれないので、ARCore の向きで合わせ直す
                        if (aligned) heading.Reset(LocalWalkAzimuth());
                        Debug.Log($"[EkiNavi] ジャイロの符号を {heading.Sign:+0;-0} に決めました");
                    }
                }
            }
            lastCameraYaw = cameraYaw;
            hasCameraYaw = true;
        }

        // 画面の文字は英数字だけにしている（端末によって日本語のフォントがないため）
        void OnGUI()
        {
            if (!showStatus) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(Screen.height * 0.018f) };
                style.normal.textColor = Color.cyan;
            }
            string text = !sensorsReady
                ? "Dead reckoning: no gyroscope"
                : $"Dead reckoning: {(Active ? "on" : "waiting for sign scan")}  steps {reckoning.Steps}  " +
                  $"{reckoning.Distance:0.0} m  heading {heading.HeadingDeg:0}  gyro sign {(signDecided ? heading.Sign.ToString("+0;-0") : "? (turn around once)")}";
            float h = Screen.height * 0.04f;
            GUI.Label(new Rect(20, Screen.height - h * 3.2f, Screen.width - 40, h), text, style);
        }
    }
}
