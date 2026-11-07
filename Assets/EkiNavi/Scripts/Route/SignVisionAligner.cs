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
    /// 3. 候補（経路の出発の階にある板）と画像を送り、板の ID と画像の中の枠を受け取る
    /// 4. 枠の大きさと板の縦の長さから位置を、正面から撮ったとみなして向きを求める（SignVisionGeometry）。
    ///    光線が縦の面（壁）と交われば、その面の位置と向きを使う
    /// 5. SignRouteAligner.AlignFromVision で経路を置く
    /// </summary>
    [RequireComponent(typeof(SignRouteAligner))]
    public class SignVisionAligner : MonoBehaviour
    {
        [Tooltip("サーバーの URL（例: https://xxxx.a.run.app）。末尾の / はなくてよい")]
        [SerializeField] string serverUrl = "";
        [SerializeField] ARCameraBackground cameraBackground;
        [SerializeField] Camera arCamera;
        [Tooltip("あれば、壁の面で位置と向きを確かめる")]
        [SerializeField] ARPlaneManager planeManager;

        [Header("画像（数値は仮）")]
        [Tooltip("送る画像の長辺（px）。大きいほど遠くの文字が読めるが、送るのに時間がかかる")]
        [SerializeField] int maxLongSide = 1280;
        [Range(30, 95)]
        [SerializeField] int jpegQuality = 80;
        [Tooltip("ぶれの少ない1枚を選ぶために写すフレーム数")]
        [SerializeField] int burstFrames = 5;
        [SerializeField] float timeoutSeconds = 10f;

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
        class IdentifyResponse
        {
            public string status;
            public string signId;
            public ImageBox box;
            public string engine;
            public int latencyMs;
        }

        SignRouteAligner aligner;
        RouteRenderer route;
        RenderTexture best;
        Texture2D readback;
        bool busy;
        string status = "";
        GUIStyle buttonStyle, labelStyle;

        public bool Busy => busy;

        void Awake()
        {
            aligner = GetComponent<SignRouteAligner>();
            route = GetComponent<RouteRenderer>();
            if (arCamera == null) arCamera = Camera.main;
            if (cameraBackground == null && arCamera != null) cameraBackground = arCamera.GetComponent<ARCameraBackground>();
            if (planeManager == null) planeManager = FindFirstObjectByType<ARPlaneManager>();
        }

        void OnDestroy()
        {
            if (best != null) best.Release();
            if (readback != null) Destroy(readback);
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

                var candidates = CandidateIds();
                if (candidates.Length == 0)
                {
                    status = "No signs on this floor in signs.json";
                    yield break;
                }

                byte[] jpeg = Encode(best);
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
                    Debug.Log($"[EkiNavi] 案内板の判別: {response.status} {response.signId}（Gemini {response.latencyMs} ms、往復 {roundTrip} ms、{jpeg.Length / 1024} KB）");
                    Apply(response, view, roundTrip);
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

        byte[] Encode(RenderTexture source)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = source;
            readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            RenderTexture.active = previous;
            return readback.EncodeToJPG(jpegQuality);
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

        void Apply(IdentifyResponse response, CaptureView view, int roundTrip)
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
            var box = response.box;
            if (!SignVisionGeometry.TryLocate(view, box.xMin, box.yMin, box.xMax, box.yMax, sign.heightMeters,
                    out var position, out var normal))
            {
                status = $"{sign.id}: box looks wrong. Try again closer";
                return;
            }

            string source = "size";
            var ray = SignVisionGeometry.RayThrough(view, (box.xMin + box.xMax) * 0.5f, (box.yMin + box.yMax) * 0.5f);
            float estimated = Vector3.Distance(view.Position, position);
            if (TryWall(ray, estimated, out var hit, out var wallNormal))
            {
                position = hit;
                normal = wallNormal;
                source = "wall";
            }

            aligner.AlignFromVision(sign, position, normal);
            status = $"Aligned at {sign.id} ({source}, {Vector3.Distance(view.Position, position):0.0} m, {roundTrip} ms)";
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
            if (!string.IsNullOrEmpty(status))
                GUI.Label(new Rect(20, rect.y - h * 1.2f, Screen.width - 40, h * 1.2f), status, labelStyle);
        }
    }
}
