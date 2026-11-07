using System.IO;
using System.Linq;
using EkiNavi.Route;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace EkiNavi.EditorTools
{
    /// <summary>
    /// 経路案内の試作シーン（Assets/EkiNavi/Scenes/RouteDemo.unity）を作り直す。
    /// メニュー「EkiNavi > 経路デモのシーンを作る」から実行する。何度実行しても同じものができる。
    /// </summary>
    public static class RouteDemoSceneBuilder
    {
        const string ScenePath = "Assets/EkiNavi/Scenes/RouteDemo.unity";
        const string RoutePath = "Assets/EkiNavi/Routes/jr_south_to_new_south.json";
        const string MaterialFolder = "Assets/EkiNavi/Materials";

        [MenuItem("EkiNavi/経路デモのシーンを作る")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var lineMat = GetOrCreateMaterial("RouteLine", new Color(0f, 0.38f, 1f));
            var arrowMat = GetOrCreateMaterial("RouteArrow", Color.white);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // AR Foundation の標準メニューで、AR Session と XR Origin を置く
            EditorApplication.ExecuteMenuItem("GameObject/XR/AR Session");
            Selection.activeObject = null;
            EditorApplication.ExecuteMenuItem("GameObject/XR/XR Origin (Mobile AR)");

            var origin = Object.FindFirstObjectByType<XROrigin>();
            // メニューで置くとシーンビューの注視点に置かれることがあるので、原点に戻す
            origin.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var planeManager = origin.GetComponent<ARPlaneManager>() ?? origin.gameObject.AddComponent<ARPlaneManager>();

            var routeGo = new GameObject("Route");
            var renderer = routeGo.AddComponent<RouteRenderer>();
            var aligner = routeGo.AddComponent<ManualRouteAligner>();

            var so = new SerializedObject(renderer);
            so.FindProperty("routeJson").objectReferenceValue = AssetDatabase.LoadAssetAtPath<TextAsset>(RoutePath);
            so.FindProperty("lineMaterial").objectReferenceValue = lineMat;
            so.FindProperty("arrowMaterial").objectReferenceValue = arrowMat;
            so.ApplyModifiedPropertiesWithoutUndo();

            so = new SerializedObject(aligner);
            so.FindProperty("cameraTransform").objectReferenceValue = origin.Camera.transform;
            so.FindProperty("planeManager").objectReferenceValue = planeManager;
            so.ApplyModifiedPropertiesWithoutUndo();

            // 床の線の代わりに浮かぶ矢印で案内し、人混みでの位置のずれを加速度センサーで打ち消す
            var guard = routeGo.AddComponent<RouteMotionGuard>();
            var arrow = routeGo.AddComponent<RouteArrow>();
            so = new SerializedObject(renderer);
            so.FindProperty("drawLine").boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
            foreach (var component in new Component[] { guard, arrow })
            {
                so = new SerializedObject(component);
                so.FindProperty("cameraTransform").objectReferenceValue = origin.Camera.transform;
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);

            // ビルドしたアプリがこのシーンから始まるようにする
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();

            Debug.Log($"[EkiNavi] {ScenePath} を作りました");
        }

        static Material GetOrCreateMaterial(string name, Color color)
        {
            var path = $"{MaterialFolder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                Directory.CreateDirectory(MaterialFolder);
                mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }
    }
}
