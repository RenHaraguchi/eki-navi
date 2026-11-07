using System.IO;
using System.Linq;
using EkiNavi.Geospatial;
using Google.XR.ARCoreExtensions;
using Google.XR.ARCoreExtensions.Internal;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace EkiNavi.EditorTools
{
    /// <summary>
    /// 駅の中で Geospatial API（VPS）が使えるかを確かめる試験用シーン（Assets/EkiNavi/Scenes/GeospatialProbe.unity）を作り直す。
    /// メニュー「EkiNavi > Geospatial の確認用シーンを作る」から実行する。何度実行しても同じものができる。
    ///
    /// あわせて ARCore Extensions のプロジェクト設定で Geospatial をオンにし、認証を API キーにする。
    /// API キーそのものは Project Settings > XR Plug-in Management > ARCore Extensions に手で入れる
    /// （ProjectSettings/ARCoreExtensionsProjectSettings.json に保存されるので、キーを入れたらこのファイルはコミットしない）。
    /// </summary>
    public static class GeospatialProbeSceneBuilder
    {
        const string ScenePath = "Assets/EkiNavi/Scenes/GeospatialProbe.unity";
        const string ConfigPath = "Assets/EkiNavi/Geospatial/GeospatialConfig.asset";

        [MenuItem("EkiNavi/Geospatial の確認用シーンを作る")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            EnableGeospatialInProjectSettings();

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var config = GetOrCreateConfig();

            // AR Foundation の標準メニューで、AR Session と XR Origin を置く
            EditorApplication.ExecuteMenuItem("GameObject/XR/AR Session");
            Selection.activeObject = null;
            EditorApplication.ExecuteMenuItem("GameObject/XR/XR Origin (Mobile AR)");

            var session = Object.FindFirstObjectByType<ARSession>();
            var origin = Object.FindFirstObjectByType<XROrigin>();
            var cameraManager = origin.Camera.GetComponent<ARCameraManager>();

            if (!origin.TryGetComponent<AREarthManager>(out var earthManager))
                earthManager = origin.gameObject.AddComponent<AREarthManager>();

            var extGo = new GameObject("ARCore Extensions");
            var extensions = extGo.AddComponent<ARCoreExtensions>();
            extensions.Session = session;
            extensions.Origin = origin;
            extensions.CameraManager = cameraManager;
            extensions.ARCoreExtensionsConfig = config;

            var probeGo = new GameObject("Geospatial Probe");
            var probe = probeGo.AddComponent<GeospatialProbe>();
            var so = new SerializedObject(probe);
            so.FindProperty("arCoreExtensions").objectReferenceValue = extensions;
            so.FindProperty("earthManager").objectReferenceValue = earthManager;
            so.ApplyModifiedPropertiesWithoutUndo();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);

            // ビルドしたアプリがこのシーンから始まるようにする（元に戻すときは、使いたいシーンのメニューを実行し直す）
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();

            Debug.Log($"[EkiNavi] {ScenePath} を作りました。Project Settings > XR Plug-in Management > ARCore Extensions に API キーを入れてからビルドしてください");
            SettingsService.OpenProjectSettings("Project/XR Plug-in Management/ARCore Extensions");
        }

        static void EnableGeospatialInProjectSettings()
        {
            var settings = ARCoreExtensionsProjectSettings.Instance;
            settings.GeospatialEnabled = true;
            if (settings.AndroidAuthenticationStrategySetting == AndroidAuthenticationStrategy.DoNotUse)
                settings.AndroidAuthenticationStrategySetting = AndroidAuthenticationStrategy.ApiKey;
            settings.Save();
        }

        static ARCoreExtensionsConfig GetOrCreateConfig()
        {
            var config = AssetDatabase.LoadAssetAtPath<ARCoreExtensionsConfig>(ConfigPath);
            if (config == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
                config = ScriptableObject.CreateInstance<ARCoreExtensionsConfig>();
                AssetDatabase.CreateAsset(config, ConfigPath);
            }
            config.GeospatialMode = GeospatialMode.Enabled;
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssets();
            return config;
        }
    }
}
