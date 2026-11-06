using System.IO;
using System.Linq;
using EkiNavi.Route;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace EkiNavi.EditorTools
{
    /// <summary>
    /// 案内板の画像認識で経路を置く試作シーン（Assets/EkiNavi/Scenes/SignRouteDemo.unity）を作り直す。
    /// メニュー「EkiNavi > 案内板の経路デモのシーンを作る」から実行する。何度実行しても同じものができる。
    ///
    /// 参照画像ライブラリ（Assets/EkiNavi/ImageTracking/SignLibrary.asset）がなければ空のものを作る。
    /// 画像は手で足す: 名前を signs.json の imageName（例: sign_2f_south）にし、Specify Size をオンにして
    /// 実寸の幅（メートル）を入れる。
    /// </summary>
    public static class SignRouteDemoSceneBuilder
    {
        const string ScenePath = "Assets/EkiNavi/Scenes/SignRouteDemo.unity";
        const string RoutePath = "Assets/EkiNavi/Routes/jr_south_to_new_south.json";
        const string SignsPath = "Assets/EkiNavi/Data/signs.json";
        const string LibraryPath = "Assets/EkiNavi/ImageTracking/SignLibrary.asset";
        const string LineMaterialPath = "Assets/EkiNavi/Materials/RouteLine.mat";
        const string ArrowMaterialPath = "Assets/EkiNavi/Materials/RouteArrow.mat";

        [MenuItem("EkiNavi/案内板の経路デモのシーンを作る")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            // NewScene は使われていないアセットを解放するので、ライブラリはシーンを作ってから読む
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var library = GetOrCreateLibrary();

            // AR Foundation の標準メニューで、AR Session と XR Origin を置く
            EditorApplication.ExecuteMenuItem("GameObject/XR/AR Session");
            Selection.activeObject = null;
            EditorApplication.ExecuteMenuItem("GameObject/XR/XR Origin (Mobile AR)");

            var origin = Object.FindFirstObjectByType<XROrigin>();
            if (!origin.TryGetComponent<ARPlaneManager>(out var planeManager))
                planeManager = origin.gameObject.AddComponent<ARPlaneManager>();
            if (!origin.TryGetComponent<ARTrackedImageManager>(out var imageManager))
                imageManager = origin.gameObject.AddComponent<ARTrackedImageManager>();

            var so = new SerializedObject(imageManager);
            var libraryProperty = so.FindProperty("m_SerializedLibrary");
            if (libraryProperty != null)
            {
                libraryProperty.objectReferenceValue = library;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                imageManager.referenceLibrary = library;
            }

            var routeGo = new GameObject("Route");
            var renderer = routeGo.AddComponent<RouteRenderer>();
            var aligner = routeGo.AddComponent<SignRouteAligner>();

            so = new SerializedObject(renderer);
            so.FindProperty("routeJson").objectReferenceValue = AssetDatabase.LoadAssetAtPath<TextAsset>(RoutePath);
            so.FindProperty("lineMaterial").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Material>(LineMaterialPath);
            so.FindProperty("arrowMaterial").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Material>(ArrowMaterialPath);
            so.ApplyModifiedPropertiesWithoutUndo();

            so = new SerializedObject(aligner);
            so.FindProperty("imageManager").objectReferenceValue = imageManager;
            so.FindProperty("planeManager").objectReferenceValue = planeManager;
            so.FindProperty("cameraTransform").objectReferenceValue = origin.Camera.transform;
            so.FindProperty("signsJson").objectReferenceValue = AssetDatabase.LoadAssetAtPath<TextAsset>(SignsPath);
            so.ApplyModifiedPropertiesWithoutUndo();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);

            // ビルドしたアプリがこのシーンから始まるようにする
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();

            Debug.Log($"[EkiNavi] {ScenePath} を作りました。参照画像は {LibraryPath} に足してください（{library.count} 枚登録済み）");
            Selection.activeObject = library;
        }

        static XRReferenceImageLibrary GetOrCreateLibrary()
        {
            var library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(LibraryPath);
            if (library != null) return library;

            Directory.CreateDirectory(Path.GetDirectoryName(LibraryPath));
            library = ScriptableObject.CreateInstance<XRReferenceImageLibrary>();
            AssetDatabase.CreateAsset(library, LibraryPath);
            AssetDatabase.SaveAssets();
            return library;
        }
    }
}
