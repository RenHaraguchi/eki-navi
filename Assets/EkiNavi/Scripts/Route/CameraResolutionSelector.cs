using System;
using System.Collections.Generic;
using EkiNavi.Alignment;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace EkiNavi.Route
{
    /// <summary>
    /// AR のカメラが動き出したら、長辺 maxLongSide 以下でいちばん大きい解像度に切り替える。
    /// ARCore は既定で 640×480 を選ぶことがあり、そのままだと画面の映像も 640×480 になって、
    /// 「Scan sign」で送る画像から遠くの案内板の文字が読めない
    /// （KYV46 で、起動直後 640×480、切り替えると 1920×1080 になり、追跡も続くことを確認）。
    ///
    /// AR Camera Manager の付いたカメラ（XR Origin の下の Main Camera）に付ける。
    /// </summary>
    public class CameraResolutionSelector : MonoBehaviour
    {
        [SerializeField] ARCameraManager cameraManager;
        [Tooltip("長辺の上限（px）。大きいほど遠くの文字が読めるが、端末が熱くなりやすい")]
        [SerializeField] int maxLongSide = 1920;
        [Tooltip("切り替えを試す回数の上限（カメラが動き出す前は失敗するため、数回試す）")]
        [SerializeField] int maxAttempts = 10;

        int attempts;
        bool done;

        /// <summary>切り替えた解像度。まだなら空。</summary>
        public string Selected { get; private set; } = "";

        void Awake()
        {
            if (cameraManager == null) cameraManager = GetComponent<ARCameraManager>();
            if (cameraManager == null) cameraManager = FindFirstObjectByType<ARCameraManager>();
        }

        void OnEnable()
        {
            if (cameraManager != null) cameraManager.frameReceived += OnFrame;
        }

        void OnDisable()
        {
            if (cameraManager != null) cameraManager.frameReceived -= OnFrame;
        }

        void OnFrame(ARCameraFrameEventArgs args)
        {
            if (done) return;
            if (++attempts > maxAttempts)
            {
                done = true;
                Debug.LogWarning("[EkiNavi] カメラの解像度を切り替えられませんでした。既定のまま使います");
                return;
            }
            done = TrySelect();
        }

        bool TrySelect()
        {
            var configs = new List<XRCameraConfiguration>();
            try
            {
                using (NativeArray<XRCameraConfiguration> list = cameraManager.GetConfigurations(Allocator.Temp))
                {
                    foreach (var c in list) configs.Add(c);
                }
            }
            catch (Exception e)
            {
                // この端末やエディターの模擬環境では設定を選べない
                Debug.Log($"[EkiNavi] カメラの解像度を選べません: {e.GetType().Name}");
                return true;
            }
            if (configs.Count == 0) return false;

            var sizes = configs.ConvertAll(c => new Vector2Int(c.width, c.height));
            int index = CameraConfigChooser.Choose(sizes, maxLongSide);
            var chosen = configs[index];
            var current = cameraManager.currentConfiguration;
            if (current.HasValue && current.Value.width == chosen.width && current.Value.height == chosen.height)
            {
                Selected = $"{chosen.width}x{chosen.height}";
                return true;
            }
            try
            {
                cameraManager.currentConfiguration = chosen;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[EkiNavi] カメラの解像度を {chosen.width}x{chosen.height} にできませんでした: {e.Message}");
                return false;
            }
            Selected = $"{chosen.width}x{chosen.height}";
            Debug.Log($"[EkiNavi] カメラの解像度を {Selected} にしました（{configs.Count} 個から選択）");
            return true;
        }
    }
}
