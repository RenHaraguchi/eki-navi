using System;
using System.Globalization;
using System.IO;
using EkiNavi.Alignment;
using UnityEngine;
using UnityEngine.InputSystem;
using Gyro = UnityEngine.InputSystem.Gyroscope;

namespace EkiNavi.Route
{
    /// <summary>
    /// 確認用。歩数の数え方と、ジャイロで取る向きを、端末で確かめる。
    ///
    /// 1. 歩数: 「Start step test」を押してから決まった歩数（例: 20歩）を歩き、「Stop」を押す。
    ///    アプリが数えた歩数（MotionGuard と同じ数え方）が出る
    /// 2. 向き: ジャイロで積み上げた向きと、ARCore の向きを並べて出す。その場で右に 90 度回ったとき、
    ///    両方が同じ向きに 90 度ほど変われば、ジャイロの符号（GyroHeading.Sign）は +1 でよい。逆なら -1
    ///
    /// 記録中は、加速度・ジャイロ・重力・ARCore の向き・歩数を CSV に書く（後で PC で数え方を調整するため）。
    /// 置き場所: Application.persistentDataPath（Android では /sdcard/Android/data/&lt;アプリ ID&gt;/files）
    /// </summary>
    public class SensorDiagnostics : MonoBehaviour
    {
        [SerializeField] Transform cameraTransform;
        [SerializeField] MotionGuardSettings settings = new MotionGuardSettings();
        [Tooltip("センサーに頼む読み取りの回数（毎秒）")]
        [SerializeField] float samplingFrequency = 60f;
        [SerializeField] bool showPanel = true;

        MotionGuard guard;
        readonly GyroHeading gyroHeading = new GyroHeading(1f);
        float gravityScale = -1f;
        float lastTime = -1f;

        bool recording;
        float startTime;
        int startSteps;
        float startArYaw, startGyroHeading;
        int samples;
        StreamWriter writer;
        string filePath = "";
        string lastSummary = "";

        // ジャイロと ARCore の回る向きが合っているか（直近の積み上げ）
        float agreement;
        float lastArYaw;
        bool hasLastArYaw;

        GUIStyle labelStyle, buttonStyle;

        void Awake()
        {
            guard = new MotionGuard(settings);
        }

        void OnEnable()
        {
            Enable(Accelerometer.current);
            Enable(Gyro.current);
            Enable(GravitySensor.current);
        }

        void OnDisable() => StopRecording();

        void Enable(Sensor sensor)
        {
            if (sensor == null) return;
            InputSystem.EnableDevice(sensor);
            try { sensor.samplingFrequency = samplingFrequency; }
            catch (Exception e) { Debug.Log($"[SensorTest] {sensor.name} の読み取りの回数を変えられません: {e.Message}"); }
        }

        void Update()
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            var accelerometer = Accelerometer.current;
            if (accelerometer == null) return;

            float now = Time.time;
            float dt = lastTime < 0f ? 0f : now - lastTime;
            lastTime = now;

            var acceleration = accelerometer.acceleration.ReadValue();
            float magnitude = acceleration.magnitude;
            if (magnitude <= 0f) return;
            if (gravityScale < 0f) gravityScale = magnitude > 3f ? 1f / 9.81f : 1f;
            guard.Update(now, magnitude * gravityScale, Vector2.zero);

            var gyro = Gyro.current != null ? Gyro.current.angularVelocity.ReadValue() : Vector3.zero;
            var gravity = GravitySensor.current != null ? GravitySensor.current.gravity.ReadValue() : acceleration;
            gyroHeading.Update(gyro, gravity, dt);

            float arYaw = cameraTransform != null ? WalkYaw() : 0f;
            if (hasLastArYaw && dt > 0f)
            {
                float arRate = Mathf.DeltaAngle(lastArYaw, arYaw) / dt;
                float gyroRate = GyroHeading.YawRateDeg(gyro, gravity);
                // ARCore が見失って飛んだ所は使わない
                if (Mathf.Abs(arRate) < 360f) agreement = Mathf.Clamp(agreement + arRate * gyroRate * dt * 1e-3f, -100f, 100f);
            }
            lastArYaw = arYaw;
            hasLastArYaw = true;

            if (recording && writer != null)
            {
                samples++;
                writer.WriteLine(string.Join(",", new[]
                {
                    F(now - startTime), F(acceleration.x), F(acceleration.y), F(acceleration.z), F(magnitude * gravityScale),
                    F(gyro.x), F(gyro.y), F(gyro.z), F(gravity.x), F(gravity.y), F(gravity.z),
                    F(arYaw), F(gyroHeading.HeadingDeg), guard.Steps.ToString(CultureInfo.InvariantCulture),
                    guard.IsStill ? "1" : "0",
                }));
            }
        }

        // 推測航法と同じ取り方の、歩く向きの角度（真下や真上に向けても飛ばない）
        float WalkYaw() => DeadReckoning.TryWalkDirection(cameraTransform.forward, cameraTransform.up, out var d)
            ? Azimuth.Of(d)
            : 0f;

        static string F(float v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

        void StartRecording()
        {
            StopRecording();
            recording = true;
            startTime = Time.time;
            startSteps = guard.Steps;
            startArYaw = cameraTransform != null ? WalkYaw() : 0f;
            startGyroHeading = gyroHeading.HeadingDeg;
            samples = 0;
            agreement = 0f;
            try
            {
                filePath = Path.Combine(Application.persistentDataPath, $"sensor_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                writer = new StreamWriter(filePath);
                writer.WriteLine("t,ax,ay,az,mag_g,gx,gy,gz,grav_x,grav_y,grav_z,ar_yaw,gyro_heading,steps,still");
            }
            catch (Exception e)
            {
                writer = null;
                filePath = "";
                Debug.LogWarning($"[SensorTest] CSV を作れません: {e.Message}");
            }
            Debug.Log($"[SensorTest] 記録を始めました: {filePath}");
        }

        void StopRecording()
        {
            if (!recording) return;
            recording = false;
            float duration = Time.time - startTime;
            int steps = guard.Steps - startSteps;
            lastSummary = $"Last test: {steps} steps in {duration:0.0} s ({(duration > 0 ? samples / duration : 0):0} Hz)";
            if (writer != null)
            {
                writer.Flush();
                writer.Dispose();
                writer = null;
            }
            Debug.Log($"[SensorTest] 記録を止めました: 歩数 {steps}、{duration:0.0} 秒、{samples} 件、{filePath}");
        }

        // 画面の文字は英数字だけにしている（端末によって日本語のフォントがないため）
        void OnGUI()
        {
            if (!showPanel || !DevOverlay.Visible) return;
            if (labelStyle == null)
            {
                int size = Mathf.RoundToInt(Screen.height * 0.022f);
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = true };
                labelStyle.normal.textColor = Color.yellow;
                buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = size };
            }

            float w = Screen.width - 40, h = Screen.height * 0.055f;
            float y = Screen.height * 0.10f;
            int steps = guard.Steps - (recording ? startSteps : 0);
            if (GUI.Button(new Rect(20, y, w * 0.5f, h), recording ? $"Stop ({steps} steps)" : "Start step test", buttonStyle))
            {
                if (recording) StopRecording();
                else StartRecording();
            }
            if (GUI.Button(new Rect(20 + w * 0.52f, y, w * 0.48f, h), "Zero heading", buttonStyle))
            {
                startArYaw = cameraTransform != null ? WalkYaw() : 0f;
                startGyroHeading = gyroHeading.HeadingDeg;
                agreement = 0f;
            }

            float arTurn = cameraTransform != null ? Mathf.DeltaAngle(startArYaw, WalkYaw()) : 0f;
            float gyroTurn = Mathf.DeltaAngle(startGyroHeading, gyroHeading.HeadingDeg);
            string sign = Mathf.Abs(agreement) < 1f ? "turn in place to check" : agreement > 0 ? "same (Sign +1)" : "opposite (Sign -1)";
            string text =
                (recording ? $"RECORDING {Time.time - startTime:0.0} s  steps {steps}\n" : lastSummary + "\n") +
                $"{(guard.IsStill ? "STILL" : "moving")}  accel sd {guard.AccelerationStdG:0.000}g\n" +
                $"Turn since zero: ARCore {arTurn:+0;-0} deg  gyro {gyroTurn:+0;-0} deg\n" +
                $"Gyro vs ARCore direction: {sign}";
            GUI.Label(new Rect(20, y + h + 8, w, h * 4f), text, labelStyle);
        }
    }
}
