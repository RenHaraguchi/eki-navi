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
        // 横向きで撮って合わせたあと、縦に戻るのを待っている。縦に戻ったら向きを AR のカメラで合わせ直す
        bool waitingForPortrait;
        [Tooltip("経路の近くを経路の向きに歩いているとき、位置と向きを経路に寄せる")]
        [SerializeField] bool followRoute = true;
        RoutePath path;
        float portraitSince = -1f;
        const float PortraitHoldSeconds = 0.3f;
        const float PortraitGiveUpSeconds = 3f;
        // 縦に戻す待ちを AR が見失ったままあきらめたとき、AR が戻ったら一度だけ向きを合わせ直す
        bool reanchorWhenTracking;
        GUIStyle style;

        /// <summary>推測航法の位置を使えるか（合わせ済みで、ジャイロがある）。</summary>
        public bool Active => useDeadReckoning && aligned && sensorsReady;

        /// <summary>経路の座標での今の位置 (x, z)。</summary>
        public Vector2 LocalPosition => reckoning.Position;

        /// <summary>経路の座標での、スマホの歩く向き（北から時計回りの度）。</summary>
        public float HeadingDeg => heading.HeadingDeg;

        /// <summary>
        /// 横向きで撮って合わせ、まだ縦に戻していない。この間は矢印を出さない。
        /// 見上げたまま横から縦に回すと、その回転の一部がジャイロでは重力のまわりの回転に数えられ、
        /// 歩く向きがずれる（見上げる角度 30 度で 45 度ほど）。縦に戻ってから AR のカメラで向きを合わせ直す。
        /// </summary>
        // 推測航法が働いていないとき（ジャイロがない端末など）は、矢印を ARCore の位置で出すので待たない
        public bool WaitingForPortrait => Active && waitingForPortrait;

        void Awake()
        {
            // 画面が横向きに切り替わると、カメラの上の向きが 90 度回り、足元に向けているときの
            // 歩く向き（スマホの上端の向き）がずれる。案内は縦向きで使うので固定する
            Screen.orientation = ScreenOrientation.Portrait;
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
            waitingForPortrait = ImageTurns.Choose(cameraTransform.rotation) != ImageTurn.None;
            reanchorWhenTracking = false;
            portraitSince = -1f;
        }

        /// <summary>
        /// 経路の近くを経路の向きに歩いているなら、位置と向きを経路に少しずつ寄せる（RouteFollower）。
        /// 看板で合わせたときの向きのずれで、道なりに歩いても「外れた」と出るのを防ぐ。
        /// </summary>
        void FollowRoute(int steps)
        {
            if (!followRoute || route == null || route.LocalPoints == null || route.LocalPoints.Count < 2) return;
            if (path == null)
            {
                var points = new System.Collections.Generic.List<Vector2>();
                foreach (var p in route.LocalPoints) points.Add(new Vector2(p.x, p.z));
                path = new RoutePath(points);
            }
            if (RouteFollower.Follow(path, reckoning.Position, heading.HeadingDeg, steps, out var position, out var headingDeg))
            {
                reckoning.MoveTo(position);
                heading.Reset(headingDeg);
            }
        }

        /// <summary>縦に戻ったのを見て、向きを AR のカメラで合わせ直す。</summary>
        void UpdatePortraitWait(float now, bool tracking)
        {
            if (!waitingForPortrait)
            {
                if (reanchorWhenTracking && tracking && ImageTurns.Choose(cameraTransform.rotation) == ImageTurn.None)
                {
                    heading.Reset(LocalWalkAzimuth());
                    reanchorWhenTracking = false;
                    Debug.Log($"[EkiNavi] AR が戻ったので、歩く向きを合わせ直しました（{heading.HeadingDeg:0} 度）");
                }
                return;
            }
            if (ImageTurns.Choose(cameraTransform.rotation) != ImageTurn.None)
            {
                portraitSince = -1f;
                return;
            }
            if (portraitSince < 0f) portraitSince = now;
            // 縦の向きが少し続いてから。AR が位置を見失っている間は合わせ直さない（経路は AR の座標に置いてあるため）
            if (now - portraitSince < PortraitHoldSeconds) return;
            if (tracking)
            {
                heading.Reset(LocalWalkAzimuth());
                Debug.Log($"[EkiNavi] 縦に戻ったので、歩く向きを合わせ直しました（{heading.HeadingDeg:0} 度）");
            }
            else if (now - portraitSince < PortraitGiveUpSeconds)
            {
                return; // AR が戻るのを少し待つ
            }
            else
            {
                // AR が戻らないまま待ち続けると矢印が出ないので、ジャイロの向きのまま案内に戻る
                Debug.Log("[EkiNavi] 縦に戻ったが AR が位置を見失っているので、ジャイロの向きのまま案内します");
                reanchorWhenTracking = true;
            }
            waitingForPortrait = false;
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
                if (aligned && steps > lastSteps)
                {
                    reckoning.Advance(steps - lastSteps, heading.HeadingDeg, motion.StrideMeters);
                    FollowRoute(steps - lastSteps);
                }
                lastSteps = steps;
            }
        }

        /// <summary>ARCore が追跡できている間に、ジャイロと ARCore の回る向きを比べて符号を決める。</summary>
        void DecideSign(Vector3 angular, Vector3 gravity, float dt)
        {
            float cameraYaw = CameraWalkYaw();
            bool tracking = ARSession.state == ARSessionState.SessionTracking;
            UpdatePortraitWait(Time.time, tracking);
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
            if (!showStatus || !DevOverlay.Visible) return;
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
