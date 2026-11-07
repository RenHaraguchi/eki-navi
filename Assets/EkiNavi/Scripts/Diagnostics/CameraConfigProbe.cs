using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace EkiNavi.Diagnostics
{
    /// <summary>
    /// 確認用。端末のカメラで選べる解像度と、いま使っている解像度を画面に出す。
    /// 案内板の文字を Gemini に読ませる案で、遠くの文字が読める大きさの画像を取り出せるかを確かめるために使う。
    ///
    /// 使い方: XR Origin の下の Main Camera（AR Camera Manager が付いている物）に付けて、端末で動かす。
    /// 画面の右上に一覧が出る。一覧のボタンを押すと、その解像度に切り替える（追跡が続くかも見る）。
    /// 同じ内容を Logcat にも出す（タグ [CameraConfigProbe]）。
    ///
    /// エディターでは AR のカメラがないので、何も出ない。確認が済んだら外してよい。
    /// </summary>
    public class CameraConfigProbe : MonoBehaviour
    {
        const string LogTag = "[CameraConfigProbe]";

        [SerializeField] ARCameraManager cameraManager;

        [Tooltip("一覧を取り直す間隔（秒）")]
        [SerializeField] float refreshSeconds = 1f;

        [Tooltip("画面の右上に出す")]
        [SerializeField] bool showOnScreen = true;

        readonly List<XRCameraConfiguration> configurations = new List<XRCameraConfiguration>();
        string status = "Camera probe: waiting for AR camera";
        string lastLogged;
        float nextRefresh;
        GUIStyle labelStyle, buttonStyle;
        Vector2 scroll;

        void Awake()
        {
            if (cameraManager == null) cameraManager = GetComponent<ARCameraManager>();
            if (cameraManager == null) cameraManager = FindFirstObjectByType<ARCameraManager>();
        }

        void Update()
        {
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + refreshSeconds;
            Refresh();
        }

        void Refresh()
        {
            if (cameraManager == null || !cameraManager.enabled || cameraManager.subsystem == null || !cameraManager.subsystem.running)
            {
                status = "Camera probe: no running AR camera";
                return;
            }

            var text = new StringBuilder();
            text.AppendLine($"AR session: {ARSession.state}");

            // 選べる解像度の一覧
            configurations.Clear();
            try
            {
                using (NativeArray<XRCameraConfiguration> list = cameraManager.GetConfigurations(Allocator.Temp))
                {
                    foreach (var c in list) configurations.Add(c);
                }
                text.AppendLine($"Configurations: {configurations.Count}");
            }
            catch (Exception e)
            {
                text.AppendLine($"Configurations: not available ({e.GetType().Name})");
            }

            // いま使っている設定
            XRCameraConfiguration? current = null;
            try { current = cameraManager.currentConfiguration; }
            catch (Exception e) { text.AppendLine($"Current: not available ({e.GetType().Name})"); }
            text.AppendLine(current.HasValue ? $"Current: {Describe(current.Value)}" : "Current: (none)");

            // 実際に取り出せる画像（Gemini に送るならこれ）
            if (cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
            {
                using (image)
                {
                    text.AppendLine($"CPU image: {image.width}x{image.height} {image.format}");
                }
            }
            else
            {
                text.AppendLine("CPU image: not available yet");
            }

            // 映像（GPU のテクスチャ）の大きさ。ふつうは CPU の画像より大きい
            if (cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics))
                text.AppendLine($"Intrinsics: {intrinsics.resolution.x}x{intrinsics.resolution.y}");

            text.AppendLine($"Screen: {Screen.width}x{Screen.height}");

            status = text.ToString().TrimEnd();

            // 変わったときだけ Logcat に出す
            var log = status + "\n" + string.Join("\n", configurations.ConvertAll(Describe));
            if (log != lastLogged)
            {
                lastLogged = log;
                Debug.Log($"{LogTag}\n{log}");
            }
        }

        static string Describe(XRCameraConfiguration c) =>
            $"{c.width}x{c.height} {(c.framerate.HasValue ? c.framerate.Value + "fps" : "fps ?")} depth:{c.depthSensorSupported}";

        void Select(XRCameraConfiguration c)
        {
            try
            {
                cameraManager.currentConfiguration = c;
                Debug.Log($"{LogTag} switched to {Describe(c)}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{LogTag} could not switch to {Describe(c)}: {e.Message}");
            }
            nextRefresh = 0f;
        }

        // 画面の文字は英数字だけにしている（端末によって日本語のフォントがないため）
        void OnGUI()
        {
            if (!showOnScreen) return;
            if (labelStyle == null)
            {
                int size = Mathf.RoundToInt(Screen.height * 0.016f);
                labelStyle = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = true };
                labelStyle.normal.textColor = Color.white;
                buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = size };
            }

            float width = Screen.width * 0.55f;
            float height = Screen.height * 0.45f;
            var area = new Rect(Screen.width - width - 10, 10, width, height);
            GUI.Box(area, GUIContent.none);
            GUILayout.BeginArea(new Rect(area.x + 8, area.y + 8, area.width - 16, area.height - 16));
            scroll = GUILayout.BeginScrollView(scroll);
            GUILayout.Label(status, labelStyle);
            foreach (var c in configurations)
            {
                if (GUILayout.Button("Use " + Describe(c), buttonStyle, GUILayout.Height(Screen.height * 0.035f)))
                    Select(c);
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }
    }
}
