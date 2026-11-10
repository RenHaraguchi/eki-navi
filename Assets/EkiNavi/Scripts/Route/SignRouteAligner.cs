using System.Collections.Generic;
using EkiNavi.Alignment;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace EkiNavi.Route
{
    /// <summary>
    /// 案内板の画像認識で経路を置く（設計書5章）。ManualRouteAligner の代わりに使う。
    ///
    /// 流れ:
    /// 1. ARTrackedImageManager が認識した画像の名前で、案内板の一覧（signs.json）を引く
    /// 2. 板が追跡中の間、位置と法線を DetectionStabilizer に入れ、落ち着くまで待つ（0.5秒、2cm、1度。仮）
    /// 3. 落ち着いたら、一覧の値と組み合わせて地図と AR の変換を作り、RouteRenderer を置く
    /// 4. AR の追跡が1秒止まったら（またはアプリが一時停止したら）線を消し、案内板にかざし直すまで出さない
    ///
    /// 開発用に、認識した板に軸（赤 = X、緑 = Y、青 = Z）を出す。緑が板の表から手前に向いていれば、
    /// 「+Y が板の法線」という前提が合っている。
    /// </summary>
    [RequireComponent(typeof(RouteRenderer))]
    public class SignRouteAligner : MonoBehaviour
    {
        [SerializeField] ARTrackedImageManager imageManager;
        [Tooltip("あれば、床の高さに使う")]
        [SerializeField] ARPlaneManager planeManager;
        [SerializeField] Transform cameraTransform;
        [Tooltip("案内板の一覧（Assets/EkiNavi/Data/signs.json）")]
        [SerializeField] TextAsset signsJson;

        [Header("判定（数値は仮）")]
        [Tooltip("床が見つからず、案内板の高さも使えないときの、床からカメラまでの高さ")]
        [SerializeField] float eyeHeight = 1.4f;
        [Tooltip("AR の追跡がこの秒数続けて止まったら線を消す")]
        [SerializeField] float hideAfterTrackingLostSeconds = 1f;
        [Tooltip("オンなら、AR の追跡が止まっても線を消さない。DeadReckoningTracker で位置を進めるときに使う")]
        [SerializeField] bool keepRouteWhenTrackingLost = false;
        [SerializeField] float arrivalRadius = 5f;

        [Header("開発用")]
        [SerializeField] bool showImageAxes = true;
        [SerializeField] bool showStatus = true;

        enum State
        {
            WaitingForSign,
            Stabilizing,
            Aligned,
        }

        RouteRenderer route;
        SignCatalog catalog;
        readonly DetectionStabilizer stabilizer = new DetectionStabilizer();
        readonly Dictionary<TrackableId, GameObject> axes = new Dictionary<TrackableId, GameObject>();

        State state = State.WaitingForSign;
        TrackableId stabilizingId = TrackableId.invalidId;
        TrackableId usedId = TrackableId.invalidId;
        float trackingLostSince = -1f;
        float remaining = -1f;
        string message = "";

        // 最後に合わせたときの値（画面に出す）
        SignEntry alignedSign;
        SignAlignment alignment;
        FloorEstimate floor;
        float detectedAzimuth;

        GUIStyle style;
        Material[] axisMaterials;

        public bool Arrived { get; private set; }

        /// <summary>画面の上の英語の状態の文を出すか（EkiNaviUI があるときは隠す）。</summary>
        public bool ShowStatus { get => showStatus; set => showStatus = value; }

        /// <summary>案内板の一覧。SignVisionAligner が候補を選ぶのに使う。</summary>
        public SignCatalog Catalog => catalog;

        /// <summary>サーバーの案内板の一覧に置き換える。同梱の一覧は、サーバーから取れないときの予備として残す。</summary>
        public void ReplaceCatalog(SignCatalog serverCatalog)
        {
            if (serverCatalog != null) catalog = serverCatalog;
        }

        /// <summary>経路が案内板に合わせて置かれているか。</summary>
        public bool IsAligned => state == State.Aligned;

        void Awake()
        {
            route = GetComponent<RouteRenderer>();
            if (signsJson == null)
            {
                Debug.LogError("[EkiNavi] SignRouteAligner に signsJson が設定されていません");
                enabled = false;
                return;
            }
            catalog = SignCatalog.Parse(signsJson.text);
            if (imageManager == null) imageManager = FindFirstObjectByType<ARTrackedImageManager>();
            if (planeManager == null) planeManager = FindFirstObjectByType<ARPlaneManager>();
            message = "Point the camera at a sign";
        }

        void Update()
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            if (cameraTransform == null || imageManager == null) return;

            WatchSessionTracking();
            if (RealignRequested()) LoseAlignment("Re-align requested. Point the camera at a sign");

            var candidate = FindCandidate(out var candidateSign, out var candidateNormal);
            if (candidate == null)
            {
                stabilizer.Reset();
                stabilizingId = TrackableId.invalidId;
                if (state == State.Stabilizing) state = State.WaitingForSign;
            }
            else
            {
                if (candidate.trackableId != stabilizingId)
                {
                    stabilizer.Reset();
                    stabilizingId = candidate.trackableId;
                }
                if (state != State.Aligned) state = State.Stabilizing;

                var p = candidate.transform.position;
                if (stabilizer.Add(Time.time, new Vector2(p.x, p.z), candidateNormal))
                    Align(candidate, candidateSign);
            }

            if (state == State.Aligned) UpdateProgress();
        }

        void OnApplicationPause(bool paused)
        {
            // 一時停止から戻ると座標がずれていることがあるので、かざし直してもらう
            if (paused) LoseAlignment("Resumed. Point the camera at a sign again");
        }

        // ---- 追跡の監視 ----

        void WatchSessionTracking()
        {
            if (ARSession.state == ARSessionState.SessionTracking)
            {
                trackingLostSince = -1f;
                return;
            }
            if (trackingLostSince < 0f) trackingLostSince = Time.time;
            if (keepRouteWhenTrackingLost) return;
            if (state == State.Aligned && Time.time - trackingLostSince >= hideAfterTrackingLostSeconds)
                LoseAlignment("Tracking lost. Point the camera at a sign again");
        }

        void LoseAlignment(string reason)
        {
            route.SetVisible(false);
            state = State.WaitingForSign;
            stabilizer.Reset();
            stabilizingId = TrackableId.invalidId;
            // 追跡が戻っても、かざし直すまで線を出さない。同じ板が追跡中のまま残っていても使わない
            usedId = TrackableId.invalidId;
            if (imageManager != null)
            {
                foreach (var image in imageManager.trackables)
                {
                    if (image.trackingState == TrackingState.Tracking) usedId = image.trackableId;
                }
            }
            remaining = -1f;
            message = reason;
            Debug.Log($"[EkiNavi] {reason}");
        }

        static bool RealignRequested()
        {
#if !(UNITY_EDITOR || DEVELOPMENT_BUILD)
            // 通常のビルドでは、指が 2 本触れただけで位置合わせを外さない（合わせ直しは「読み取り直す」で行う）
            return false;
#else
            var ts = Touchscreen.current;
            if (ts != null && ts.touches.Count > 1 && ts.touches[1].press.wasPressedThisFrame) return true;
            var kb = Keyboard.current;
            return kb != null && kb.rKey.wasPressedThisFrame;
#endif
        }

        // ---- 板の選択 ----

        /// <summary>追跡中で、一覧にあり、表側から見ていて、まだ使っていない板のうち、カメラに一番近いもの。</summary>
        ARTrackedImage FindCandidate(out SignEntry sign, out Vector2 horizontalNormal)
        {
            sign = null;
            horizontalNormal = Vector2.zero;
            ARTrackedImage best = null;
            float bestDistance = float.MaxValue;
            bool usedStillTracking = false;
            var cam = cameraTransform.position;

            foreach (var image in imageManager.trackables)
            {
                UpdateAxes(image);
                if (image.trackingState != TrackingState.Tracking) continue;
                if (image.trackableId == usedId)
                {
                    usedStillTracking = true;
                    continue;
                }

                var imageName = image.referenceImage.name;
                if (!catalog.TryGetByImageName(imageName, out var s))
                {
                    message = $"Unknown image: {imageName}";
                    continue;
                }
                if (!SignDetection.TryHorizontalNormal(image.transform.up, out var n)) continue;
                if (!SignDetection.IsViewedFromFront(image.transform.position, n, cam)) continue;
                if (Mathf.Abs(s.ordinal - route.Data.points[0].ordinal) > 0.5f)
                {
                    message = $"{s.id} is on floor {s.ordinal}, the route starts on {route.Data.points[0].ordinal}";
                    continue;
                }

                float d = (image.transform.position - cam).sqrMagnitude;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = image;
                    sign = s;
                    horizontalNormal = n;
                }
            }

            // 使った板が一度でも追跡から外れたら、また使ってよい（同じ板にかざし直したとき）
            if (!usedStillTracking) usedId = TrackableId.invalidId;
            return best;
        }

        // ---- 位置合わせ ----

        void Align(ARTrackedImage image, SignEntry sign)
        {
            detectedAzimuth = stabilizer.MeanAzimuthDeg;
            ApplyAlignment(sign, stabilizer.MeanPosition, stabilizer.MeanNormal, image.transform.position.y);
            usedId = image.trackableId;
            stabilizer.Reset();
            stabilizingId = TrackableId.invalidId;
        }

        /// <summary>
        /// 画像認識を使わずに見つけた案内板（SignVisionAligner が Gemini で見分けたもの）で経路を合わせる。
        /// </summary>
        /// <param name="sign">見分けた板。</param>
        /// <param name="signPosition">板の中心（AR の座標）。</param>
        /// <param name="horizontalNormal">板の外向きの法線を水平にしたもの (x, z)。</param>
        public void AlignFromVision(SignEntry sign, Vector3 signPosition, Vector2 horizontalNormal)
        {
            if (cameraTransform == null && Camera.main != null) cameraTransform = Camera.main.transform;
            if (cameraTransform == null || sign == null) return;
            detectedAzimuth = Azimuth.Of(horizontalNormal);
            ApplyAlignment(sign, new Vector2(signPosition.x, signPosition.z), horizontalNormal, signPosition.y);
            stabilizer.Reset();
            stabilizingId = TrackableId.invalidId;
        }

        void ApplyAlignment(SignEntry sign, Vector2 signPositionXZ, Vector2 horizontalNormal, float signCenterY)
        {
            alignedSign = sign;
            alignment = sign.Align(signPositionXZ, horizontalNormal);
            floor = FloorHeight.Choose(
                cameraTransform.position.y,
                HorizontalPlaneHeights(),
                signCenterY - sign.centerHeightMeters,
                eyeHeight);

            var first = route.Data.points[0];
            var pose = alignment.RouteRendererPose(new Vector2(first.x, first.z), floor.Y);
            route.transform.SetPositionAndRotation(pose.position, pose.rotation);
            route.SetVisible(true);
            if (TryGetComponent<RouteMotionGuard>(out var guard)) guard.OnAligned();
            if (TryGetComponent<DeadReckoningTracker>(out var tracker)) tracker.OnAligned();

            state = State.Aligned;
            Arrived = false;
            message = "";
            Debug.Log($"[EkiNavi] {sign.id} で経路を合わせました: yaw {alignment.YawDeg:0.0}°、床 {floor.Y:0.00}m（{floor.Source}）");
        }

        IEnumerable<float> HorizontalPlaneHeights()
        {
            if (planeManager == null) yield break;
            foreach (var plane in planeManager.trackables)
            {
                if (plane.alignment == PlaneAlignment.HorizontalUp) yield return plane.transform.position.y;
            }
        }

        void UpdateProgress()
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

        // ---- 開発用の表示 ----

        void UpdateAxes(ARTrackedImage image)
        {
            if (!showImageAxes) return;
            if (!axes.TryGetValue(image.trackableId, out var root) || root == null)
            {
                root = CreateAxes(image.transform);
                axes[image.trackableId] = root;
            }
            root.SetActive(image.trackingState == TrackingState.Tracking);
        }

        GameObject CreateAxes(Transform parent)
        {
            if (axisMaterials == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                axisMaterials = new Material[3];
                var colors = new[] { Color.red, Color.green, Color.blue };
                for (int i = 0; i < 3; i++)
                {
                    axisMaterials[i] = new Material(shader);
                    axisMaterials[i].SetColor("_BaseColor", colors[i]);
                }
            }

            var root = new GameObject("EkiNavi Axes");
            root.transform.SetParent(parent, false);
            const float length = 0.3f, thickness = 0.01f;
            AddAxis(root.transform, new Vector3(length, thickness, thickness), new Vector3(length / 2f, 0f, 0f), axisMaterials[0]);
            AddAxis(root.transform, new Vector3(thickness, length, thickness), new Vector3(0f, length / 2f, 0f), axisMaterials[1]);
            AddAxis(root.transform, new Vector3(thickness, thickness, length), new Vector3(0f, 0f, length / 2f), axisMaterials[2]);
            return root;
        }

        static void AddAxis(Transform parent, Vector3 scale, Vector3 position, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
        }

        // 画面の文字は英数字だけにしている（端末によって日本語のフォントがないため）
        void OnGUI()
        {
            if (!showStatus) return;
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(Screen.height * 0.022f) };
                style.normal.textColor = Color.white;
            }

            string text;
            switch (state)
            {
                case State.Aligned:
                    text = Arrived
                        ? $"Arrived: {route.Data.goal.name}"
                        : $"Remaining: {remaining:0} m   (2-finger tap to re-align)";
                    if (DevOverlay.Visible)
                        text += $"\nSign {alignedSign.id}  yaw {alignment.YawDeg:0.0}  normal {detectedAzimuth:0.0}  floor {floor.Source} {floor.Y:0.00}";
                    break;
                case State.Stabilizing:
                    text = "Hold still on the sign...";
                    break;
                default:
                    text = message;
                    break;
            }

            GUI.Label(new Rect(20, 40, Screen.width - 40, Screen.height * 0.2f), text, style);
        }
    }
}
