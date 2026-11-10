using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using EkiNavi.Alignment;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using UnityEngine.XR.ARFoundation;

namespace EkiNavi.Route
{
    /// <summary>
    /// 「Scan sign」を押すと、カメラの画像をサーバー（POST /v1/sign/identify）に送り、
    /// Gemini でどの案内板かを見分けて、経路を合わせる。ARCore の画像認識で見つけられない板のために使う。
    ///
    /// 流れ:
    /// 1. 0.3 秒ほどの間に数フレーム写し、カメラの回転がいちばん小さかった（ぶれにくい）1枚を選ぶ
    ///    画像は画面と同じ向きと切り取り方（ARCameraBackground の material で写す）
    /// 2. 写した瞬間のカメラの位置・向き・projection を覚えておく。答えが返るまでに歩いても、この値で計算する
    /// 3. 候補（経路の出発の階にある板）と画像を送り、板の ID と、画像の中の枠と四隅を受け取る
    /// 4. 板の四隅と板の大きさから、位置と向きを求める（SignVisionGeometry）。斜めから撮ってもよい。
    ///    四隅が使えなければ、外枠から位置を求め、向きは正面から撮ったとみなす。
    ///    光線が縦の面（壁）と交われば、その面の位置と向きを使う
    /// 5. SignRouteAligner.AlignFromVision で経路を置く
    /// </summary>
    [RequireComponent(typeof(SignRouteAligner))]
    public class SignVisionAligner : MonoBehaviour
    {
        [Tooltip("サーバーの URL（例: https://xxxx.a.run.app）。末尾の / はなくてよい。"
            + "シーンを作るメニューは、環境変数 EKINAVI_SERVER_URL があればそれを入れる")]
        [SerializeField] string serverUrl = "";
        [SerializeField] ARCameraBackground cameraBackground;
        [SerializeField] Camera arCamera;
        [Tooltip("あれば、壁の面で位置と向きを確かめる")]
        [SerializeField] ARPlaneManager planeManager;

        [Header("画像（数値は仮）")]
        [Tooltip("送る画像の長辺（px）。大きいほど遠くの文字が読めるが、送るのに時間がかかる")]
        // 小さい文字を読みやすくするため、画面と同じ解像度で送る（2026-11-09 の試験で 1280 から上げた）
        [SerializeField] int maxLongSide = 1920;
        [Tooltip("四隅の当てはめを使う、残差の上限（画像の縦に対する割合。0.012 は縦 1920px で約 23px）")]
        [SerializeField] float maxFitRms = 0.012f;
        [Tooltip("四隅の当てはめで、逆の向きの残差が選んだ向きの何倍以上なら左右を信じるか")]
        [SerializeField] float minSideConfidence = 1.5f;
        [Range(30, 95)]
        [SerializeField] int jpegQuality = 80;
        [Tooltip("ぶれの少ない1枚を選ぶために写すフレーム数")]
        [SerializeField] int burstFrames = 5;
        [SerializeField] float timeoutSeconds = 10f;
        [Tooltip("案内板の一覧を読むときの待ち時間（秒）。止まっているサーバーの起動を待てる長さにする")]
        [SerializeField] float stationTimeoutSeconds = 20f;

        [Header("位置の求め方（数値は仮）")]
        [Tooltip("壁の面を使うとき、枠から見積もった距離との差をどこまで許すか（割合）")]
        [SerializeField] float planeTolerance = 0.35f;

        [Header("開発用")]
        [SerializeField] bool showButton = true;

        [Serializable]
        class IdentifyRequest
        {
            public string imageBase64;
            public string mimeType = "image/jpeg";
            public string[] candidateSignIds;
        }

        [Serializable]
        class ImageBox
        {
            public float xMin, yMin, xMax, yMax;
        }

        [Serializable]
        class ImagePoint
        {
            public float x, y;
        }

        [Serializable]
        class IdentifyResponse
        {
            public string status;
            public string signId;
            public ImageBox box;
            public ImagePoint[] corners;
            public string readText;
            public float matchScore;
            public string engine;
            public int latencyMs;
        }

        SignRouteAligner aligner;
        RouteRenderer route;
        RenderTexture best;
        Texture2D turned; // 横向きで撮った画像を回して入れる
        Texture2D readback;
        bool busy;
        string status = "";
        GUIStyle buttonStyle, labelStyle;

        public bool Busy => busy;
        /// <summary>直近の Scan sign の結果（開発用の詳しい文）。</summary>
        public string Status => status;
        /// <summary>直近の位置合わせで、向きが確かでなかったか（近づいて読み取り直すとよい）。</summary>
        public bool DirectionUncertain { get; private set; }
        /// <summary>直近の Scan sign の結果を消す（読み取りの画面を開き直したとき）。</summary>
        public void ClearStatus() => status = "";
        /// <summary>画面の右下の Scan sign のボタンを出すか（EkiNaviUI があるときは隠す）。</summary>
        public bool ShowButton { get => showButton; set => showButton = value; }
        /// <summary>看板で経路を合わせたときに呼ばれる。</summary>
        public event Action<SignEntry> Aligned;

        bool signsLoaded;

        void Awake()
        {
            // 歩いている間は画面に触らないので、端末の自動消灯で画面が消えると、カメラと追跡が止まって
            // 経路が消える（2026-11-08 の現地検証で確認）。案内している間は画面を消さない
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            aligner = GetComponent<SignRouteAligner>();
            route = GetComponent<RouteRenderer>();
            if (arCamera == null) arCamera = Camera.main;
            if (cameraBackground == null && arCamera != null) cameraBackground = arCamera.GetComponent<ARCameraBackground>();
            if (planeManager == null) planeManager = FindFirstObjectByType<ARPlaneManager>();
        }

        void Start()
        {
            if (!string.IsNullOrWhiteSpace(serverUrl)) StartCoroutine(LoadSigns());
        }

        /// <summary>
        /// 案内板の一覧をサーバー（GET /v1/station）から読む。案内板のデータはサーバーにだけ置くので、
        /// 同梱の signs.json は取れないときの予備。起動時に読むと、止まっていたサーバーの起動も先に済む。
        /// </summary>
        IEnumerator LoadSigns()
        {
            using var request = UnityWebRequest.Get(serverUrl.TrimEnd('/') + "/v1/station");
            request.timeout = Mathf.CeilToInt(stationTimeoutSeconds);
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[EkiNavi] 案内板の一覧をサーバーから読めません。同梱の一覧を使います: {request.responseCode} {request.error}");
                yield break;
            }
            try
            {
                var catalog = SignCatalog.Parse(request.downloadHandler.text);
                aligner.ReplaceCatalog(catalog);
                signsLoaded = true;
                Debug.Log($"[EkiNavi] 案内板の一覧をサーバーから読みました: {catalog.Signs.Count} 枚");
            }
            catch (FormatException e)
            {
                Debug.LogWarning($"[EkiNavi] サーバーの案内板の一覧を読めません。同梱の一覧を使います: {e.Message}");
            }
        }

        void OnDestroy()
        {
            if (best != null) best.Release();
            if (readback != null) Destroy(readback);
            if (turned != null) Destroy(turned);
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.sKey.wasPressedThisFrame) Scan();
        }

        /// <summary>画像を写して送る。送っている間は何もしない。</summary>
        public void Scan()
        {
            if (busy) return;
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                status = "Set Server Url on SignVisionAligner";
                return;
            }
            if (arCamera == null || cameraBackground == null || cameraBackground.material == null)
            {
                status = "No AR camera image";
                return;
            }
            StartCoroutine(ScanRoutine());
        }

        IEnumerator ScanRoutine()
        {
            busy = true;
            try
            {
                status = "Hold still...";
                CaptureView view = default;
                yield return CaptureSharpest(v => view = v);

                // 起動時に読めなかったら（圏外だったなど）、ここでもう一度試す
                if (!signsLoaded) yield return LoadSigns();
                var candidates = CandidateIds();
                if (candidates.Length == 0)
                {
                    status = "No signs on this floor in signs.json";
                    yield break;
                }

                var selector = arCamera.GetComponent<CameraResolutionSelector>();
                if (selector != null && selector.Selected.Length == 0)
                    Debug.LogWarning("[EkiNavi] カメラの解像度がまだ既定のままです。遠くの文字は読めないかもしれません");

                // 端末を横に倒して撮ったら、文字がまっすぐになるよう画像を回してから送る
                var turn = ImageTurns.Choose(view.Rotation);
                byte[] jpeg = Encode(best, turn);
                status = $"Sending {jpeg.Length / 1024} KB...";
                var body = JsonUtility.ToJson(new IdentifyRequest
                {
                    imageBase64 = Convert.ToBase64String(jpeg),
                    candidateSignIds = candidates,
                });

                float started = Time.realtimeSinceStartup;
                using (var request = new UnityWebRequest(serverUrl.TrimEnd('/') + "/v1/sign/identify", "POST"))
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    request.downloadHandler = new DownloadHandlerBuffer();
                    request.SetRequestHeader("Content-Type", "application/json");
                    request.timeout = Mathf.CeilToInt(timeoutSeconds);
                    yield return request.SendWebRequest();

                    int roundTrip = Mathf.RoundToInt((Time.realtimeSinceStartup - started) * 1000f);
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        status = $"Server error: {request.responseCode} {request.error}";
                        Debug.LogWarning($"[EkiNavi] 案内板の判別に失敗しました: {request.responseCode} {request.error} {request.downloadHandler.text}");
                        yield break;
                    }
                    var response = JsonUtility.FromJson<IdentifyResponse>(request.downloadHandler.text);
                    Debug.Log($"[EkiNavi] 案内板の判別: {response.status} {response.signId}（重なり {response.matchScore:0.00}、Gemini {response.latencyMs} ms、往復 {roundTrip} ms、{jpeg.Length / 1024} KB、画像の回転 {turn}）読んだ文字: {response.readText}");
                    Apply(response, view, turn, roundTrip);
                }
            }
            finally
            {
                busy = false;
            }
        }

        /// <summary>数フレーム写し、カメラの回転がいちばん小さかったフレームを best に残す。</summary>
        IEnumerator CaptureSharpest(Action<CaptureView> done)
        {
            EnsureTextures();
            float bestTurn = float.MaxValue;
            CaptureView bestView = default;
            var lastRotation = arCamera.transform.rotation;
            for (int i = 0; i < Mathf.Max(1, burstFrames); i++)
            {
                yield return null;
                var t = arCamera.transform;
                float turn = Quaternion.Angle(lastRotation, t.rotation) / Mathf.Max(Time.deltaTime, 1e-3f);
                lastRotation = t.rotation;
                if (i > 0 && turn >= bestTurn) continue; // 最初のフレームは比べる前の値がないので必ず写す

                Graphics.Blit(null, best, cameraBackground.material);
                bestTurn = i == 0 ? float.MaxValue : turn;
                bestView = new CaptureView(t.position, t.rotation, arCamera.projectionMatrix);
            }
            done(bestView);
        }

        void EnsureTextures()
        {
            float scale = Mathf.Min(1f, maxLongSide / (float)Mathf.Max(Screen.width, Screen.height));
            int w = Mathf.Max(16, Mathf.RoundToInt(Screen.width * scale));
            int h = Mathf.Max(16, Mathf.RoundToInt(Screen.height * scale));
            if (best != null && best.width == w && best.height == h) return;
            if (best != null) best.Release();
            best = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            if (readback != null) Destroy(readback);
            readback = new Texture2D(w, h, TextureFormat.RGB24, false);
        }

        byte[] Encode(RenderTexture source, ImageTurn turn)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = source;
            readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            RenderTexture.active = previous;
            if (turn == ImageTurn.None) return readback.EncodeToJPG(jpegQuality);

            var pixels = ImageTurns.Rotate(readback.GetPixels32(), readback.width, readback.height, turn,
                out int width, out int height);
            if (turned == null || turned.width != width || turned.height != height)
            {
                if (turned != null) Destroy(turned);
                turned = new Texture2D(width, height, TextureFormat.RGBA32, false);
            }
            turned.SetPixels32(pixels);
            turned.Apply(false);
            return turned.EncodeToJPG(jpegQuality);
        }

        string[] CandidateIds()
        {
            if (aligner.Catalog == null || route.Data == null) return Array.Empty<string>();
            float ordinal = route.Data.points[0].ordinal;
            var ids = new List<string>();
            foreach (var sign in aligner.Catalog.Signs)
            {
                if (Mathf.Abs(sign.ordinal - ordinal) <= 0.5f) ids.Add(sign.id);
            }
            return ids.ToArray();
        }

        void Apply(IdentifyResponse response, CaptureView view, ImageTurn turn, int roundTrip)
        {
            switch (response.status)
            {
                case "ok":
                    break;
                case "not_found":
                    status = $"No known sign in view ({roundTrip} ms). Face a sign and try again";
                    return;
                default:
                    status = $"Sign check unavailable ({response.engine}). Use manual alignment";
                    return;
            }

            if (!aligner.Catalog.TryGetById(response.signId, out var sign))
            {
                status = $"Unknown sign id: {response.signId}";
                return;
            }
            // Gemini の答えは送った（回した）画像の座標なので、撮った画像の座標に戻してから使う
            var box = response.box;
            Vector2 Back(float x, float y) => ImageTurns.ToOriginal(turn, new Vector2(x, y));
            float boxCenterX = (box.xMin + box.xMax) * 0.5f;
            var boxCenter = Back(boxCenterX, (box.yMin + box.yMax) * 0.5f);
            Vector3 position;
            Vector2 normal;
            string source;
            var c = response.corners;
            if (c != null && c.Length == 4 && SignVisionGeometry.TryLocateFromCorners(view,
                    Back(c[0].x, c[0].y), Back(c[1].x, c[1].y),
                    Back(c[2].x, c[2].y), Back(c[3].x, c[3].y),
                    sign.widthMeters, sign.heightMeters, out position, out normal, out var ratio, out var sideKnown))
            {
                // 四隅から、斜めから見ても板の向きが分かる。左右が決まらないときは正面とみなした向き
                source = sideKnown ? $"corners, {Mathf.Acos(Mathf.Clamp01(ratio)) * Mathf.Rad2Deg:0} deg" : "corners, front";
                // 四隅の4点すべてに当てはめ直す。上下の辺の遠近も使うので、斜めの角度が小さくても左右を決めやすい
                if (SignVisionGeometry.TryFitRectangle(view,
                        Back(c[0].x, c[0].y), Back(c[1].x, c[1].y), Back(c[2].x, c[2].y), Back(c[3].x, c[3].y),
                        sign.widthMeters, sign.heightMeters, position, normal,
                        out var fitPosition, out var fitNormal, out var fitRms, out var sideConfidence)
                    && fitRms <= maxFitRms && sideConfidence >= minSideConfidence)
                {
                    position = fitPosition;
                    normal = fitNormal;
                    var toCamera = new Vector2(view.Position.x - position.x, view.Position.z - position.z);
                    source = $"fit, {Vector2.Angle(normal, toCamera):0} deg";
                }
            }
            else if (box.yMax > box.yMin && box.xMax > box.xMin &&
                     SignVisionGeometry.TryLocateFromVerticalLine(view,
                         Back(boxCenterX, box.yMin), Back(boxCenterX, box.yMax), sign.heightMeters,
                         out position, out normal))
            {
                // 四隅が使えないときは、正面から撮ったとみなす
                source = "front";
            }
            else
            {
                status = $"{sign.id}: box looks wrong. Try again closer";
                return;
            }

            var ray = SignVisionGeometry.RayThrough(view, boxCenter.x, boxCenter.y);
            float estimated = Vector3.Distance(view.Position, position);
            if (TryWall(ray, estimated, out var hit, out var wallNormal))
            {
                position = hit;
                normal = wallNormal;
                source = "wall";
            }

            aligner.AlignFromVision(sign, position, normal);
            float distance = Vector3.Distance(view.Position, position);
            status = $"Aligned at {sign.id} ({source}, {distance:0.0} m, {roundTrip} ms)";
            // 横向きと縦向きで同じ看板を撮ったときに比べられるよう、求めた看板の向きとカメラの向きを残す
            var camForward = view.Rotation * Vector3.forward;
            Debug.Log($"[EkiNavi] 看板の位置: {sign.id} {source}、距離 {distance:0.0} m、看板の表の向き {Azimuth.Of(normal):0} 度、カメラの向き {Azimuth.Of(new Vector2(camForward.x, camForward.z)):0} 度、画像の回転 {turn}");
            // 向きが当てにならないのは、四隅の当てはめも壁の面も使えず、カメラの正面とみなしたときに遠い場合だけ。
            // 斜めから撮ると、正面とみなした向きはその角度のぶんずれるので、近づいて読み直してもらう
            DirectionUncertain = (source == "front" || source == "corners, front") && distance > 3.5f;
            if (DirectionUncertain)
                status += "  Direction may be off. Scan again within 3 m";
            Aligned?.Invoke(sign);
        }

        bool TryWall(Ray ray, float estimated, out Vector3 hit, out Vector2 normal)
        {
            hit = Vector3.zero;
            normal = Vector2.zero;
            if (planeManager == null) return false;
            float bestGap = float.MaxValue;
            foreach (var plane in planeManager.trackables)
            {
                if (plane.alignment != UnityEngine.XR.ARSubsystems.PlaneAlignment.Vertical) continue;
                if (!SignVisionGeometry.TryRefineWithPlane(ray, estimated, plane.transform.position, plane.transform.up,
                        plane.size.magnitude * 0.5f, planeTolerance, out var h, out var n)) continue;
                float gap = Mathf.Abs(Vector3.Distance(ray.origin, h) - estimated);
                if (gap < bestGap)
                {
                    bestGap = gap;
                    hit = h;
                    normal = n;
                }
            }
            return bestGap < float.MaxValue;
        }

        // 画面の文字は英数字だけにしている（端末によって日本語のフォントがないため）
        void OnGUI()
        {
            if (!showButton) return;
            if (buttonStyle == null)
            {
                int size = Mathf.RoundToInt(Screen.height * 0.022f);
                buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = size };
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(size * 0.8f), wordWrap = true };
                labelStyle.normal.textColor = Color.white;
            }
            float w = Screen.width * 0.42f, h = Screen.height * 0.06f;
            var rect = new Rect(Screen.width - w - 20, Screen.height * 0.70f, w, h);
            GUI.enabled = !busy;
            if (GUI.Button(rect, busy ? "Scanning..." : "Scan sign", buttonStyle)) Scan();
            GUI.enabled = true;
            string shown = DevOverlay.Visible ? status : ForUser(status);
            if (!string.IsNullOrEmpty(shown))
                GUI.Label(new Rect(20, rect.y - h * 1.2f, Screen.width - 40, h * 1.2f), shown, labelStyle);
        }

        /// <summary>開発用の表示を隠しているときの、利用者向けの短い案内。</summary>
        static string ForUser(string status)
        {
            if (string.IsNullOrEmpty(status)) return status;
            if (status.StartsWith("Aligned at"))
                return status.Contains("Direction may be off")
                    ? "Aligned. For a better direction, scan again within 3 m at an angle"
                    : "Aligned. Follow the arrow";
            if (status.StartsWith("Sending")) return "Checking the sign...";
            if (status.StartsWith("No known sign")) return "No registered sign found. Point at a sign and try again";
            if (status.StartsWith("Server error") || status.StartsWith("Sign check unavailable"))
                return "Cannot check the sign now. Try again";
            if (status.Contains("box looks wrong")) return "Could not measure the sign. Move closer and try again";
            return status;
        }
    }
}
