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
    /// （ProjectSettings/ARCoreExtensionsProjectSettings.json に保存される。このファイルは .gitignore に入れてある）。
    ///
    /// コマンドラインからは、環境変数 ARCORE_API_KEY にキーを入れて BuildApk を呼ぶと、シーン作成から APK のビルドまで行う:
    ///   Unity -batchmode -quit -projectPath . -buildTarget Android \
    ///     -executeMethod EkiNavi.EditorTools.GeospatialProbeSceneBuilder.BuildApk -logFile -
    /// できた APK は Builds/eki-navi-geo.apk。
    /// </summary>
    public static class GeospatialProbeSceneBuilder
    {
        const string ScenePath = "Assets/EkiNavi/Scenes/GeospatialProbe.unity";
        const string ConfigPath = "Assets/EkiNavi/Geospatial/GeospatialConfig.asset";
        const string ApkPath = "Builds/eki-navi-geo.apk";

        [MenuItem("EkiNavi/Geospatial の確認用シーンを作る")]
        public static void Build()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

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
            if (!Application.isBatchMode) SettingsService.OpenProjectSettings("Project/XR Plug-in Management/ARCore Extensions");
        }

        /// <summary>コマンドライン用。シーンを作り、環境変数 ARCORE_API_KEY のキーを設定して、APK をビルドする。</summary>
        public static void BuildApk()
        {
            var key = System.Environment.GetEnvironmentVariable("ARCORE_API_KEY");
            if (string.IsNullOrEmpty(key))
            {
                Debug.LogError("[EkiNavi] 環境変数 ARCORE_API_KEY が空です");
                EditorApplication.Exit(1);
                return;
            }

            Build();

            // 試験に使う端末が Android 10 なので、ProjectSettings の値によらずここで合わせる
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
            PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android, "com.ekinavi.geoprobe");
            PlayerSettings.productName = "eki-navi-geo";

            var settings = ARCoreExtensionsProjectSettings.Instance;
            settings.AndroidAuthenticationStrategySetting = AndroidAuthenticationStrategy.ApiKey;
            settings.AndroidCloudServicesApiKey = key;
            settings.Save();

            Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });

            if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                Debug.LogError($"[EkiNavi] ビルドに失敗しました: {report.summary.result}");
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log($"[EkiNavi] {ApkPath} をビルドしました");
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
