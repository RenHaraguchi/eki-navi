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
    ///
    /// 経路は1本だけ持つ。どの案内板を読み取っても、その板の位置と向きから経路を置くので、
    /// 経路と同じ階の板を読み取れば、経路は正しい場所に出る。
    /// 経路は環境変数 EKINAVI_ROUTE（Assets/EkiNavi/Routes/ の中のファイル名。拡張子なし）で選べる。
    /// なければ2階の南改札 → 新南口改札（jr_south_to_new_south）
    /// </summary>
    public static class SignRouteDemoSceneBuilder
    {
        const string ScenePath = "Assets/EkiNavi/Scenes/SignRouteDemo.unity";
        const string RoutesDir = "Assets/EkiNavi/Routes";
        const string DefaultRoute = "jr_south_to_new_south";
        // 地下1階の 9・10番線のエスカレーター（西）→ 小田急線のりば。改札の中の道筋を手でなぞった線
        const string B1OdakyuRoute = "b1_9_10_to_odakyu";
        const string SignsPath = "Assets/EkiNavi/Data/signs.json";
        const string LibraryPath = "Assets/EkiNavi/ImageTracking/SignLibrary.asset";
        const string LineMaterialPath = "Assets/EkiNavi/Materials/RouteLine.mat";
        const string ArrowMaterialPath = "Assets/EkiNavi/Materials/RouteArrow.mat";

        [MenuItem("EkiNavi/案内板の経路デモのシーンを作る")]
        public static void BuildMenu() => Build();

        [MenuItem("EkiNavi/案内板の経路デモのシーンを作る（地下1階 9・10番線 → 小田急線）")]
        public static void BuildB1OdakyuMenu() => Build(B1OdakyuRoute);

        /// <summary>経路のファイル名（拡張子なし）。省略したら環境変数 EKINAVI_ROUTE、それもなければ2階のデモ</summary>
        public static void Build(string routeName = null)
        {
            routeName ??= System.Environment.GetEnvironmentVariable("EKINAVI_ROUTE");
            if (string.IsNullOrWhiteSpace(routeName)) routeName = DefaultRoute;
            var routePath = $"{RoutesDir}/{routeName.Trim()}.json";
            if (!File.Exists(routePath)) throw new FileNotFoundException($"経路のファイルがありません: {routePath}");

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            // NewScene は使われていないアセットを解放するので、ライブラリはシーンを作ってから読む
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            // 案内板の写真はリポジトリに入れないので、ふだんは参照画像ライブラリがない
            var library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(LibraryPath);

            // AR Foundation の標準メニューで、AR Session と XR Origin を置く
            EditorApplication.ExecuteMenuItem("GameObject/XR/AR Session");
            Selection.activeObject = null;
            EditorApplication.ExecuteMenuItem("GameObject/XR/XR Origin (Mobile AR)");

            var origin = Object.FindFirstObjectByType<XROrigin>();
            // メニューで置くとシーンビューの注視点に置かれることがあるので、原点に戻す
            origin.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            if (!origin.TryGetComponent<ARPlaneManager>(out var planeManager))
                planeManager = origin.gameObject.AddComponent<ARPlaneManager>();
            if (!origin.TryGetComponent<ARTrackedImageManager>(out var imageManager))
                imageManager = origin.gameObject.AddComponent<ARTrackedImageManager>();

            SerializedObject so;
            if (library != null)
            {
                so = new SerializedObject(imageManager);
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
            }
            // 参照画像がなければ ARCore の画像認識は使わない（読み取りは Gemini で行う）
            imageManager.enabled = library != null && library.count > 0;

            var routeGo = new GameObject("Route");
            var renderer = routeGo.AddComponent<RouteRenderer>();
            var aligner = routeGo.AddComponent<SignRouteAligner>();

            so = new SerializedObject(renderer);
            // NewScene のあとに読む（NewScene が使われていないアセットを解放するため）
            so.FindProperty("routeJson").objectReferenceValue = AssetDatabase.LoadAssetAtPath<TextAsset>(routePath);
            so.FindProperty("lineMaterial").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Material>(LineMaterialPath);
            so.FindProperty("arrowMaterial").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Material>(ArrowMaterialPath);
            so.ApplyModifiedPropertiesWithoutUndo();

            so = new SerializedObject(aligner);
            so.FindProperty("imageManager").objectReferenceValue = imageManager;
            so.FindProperty("planeManager").objectReferenceValue = planeManager;
            so.FindProperty("cameraTransform").objectReferenceValue = origin.Camera.transform;
            so.FindProperty("signsJson").objectReferenceValue = AssetDatabase.LoadAssetAtPath<TextAsset>(SignsPath);
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

            // ARCore の画像認識で見つけにくい板のために、Gemini で見分ける「Scan sign」を付ける。
            // サーバーの URL は、環境変数 EKINAVI_SERVER_URL があれば入れる（Cloud Run に出したあとのビルド用）。
            // なければ空のままなので、使うときに Inspector で入れる
            var vision = routeGo.AddComponent<SignVisionAligner>();
            so = new SerializedObject(vision);
            so.FindProperty("arCamera").objectReferenceValue = origin.Camera;
            so.FindProperty("cameraBackground").objectReferenceValue = origin.Camera.GetComponent<ARCameraBackground>();
            so.FindProperty("planeManager").objectReferenceValue = planeManager;
            var serverUrl = System.Environment.GetEnvironmentVariable("EKINAVI_SERVER_URL");
            if (!string.IsNullOrWhiteSpace(serverUrl)) so.FindProperty("serverUrl").stringValue = serverUrl.Trim();
            so.ApplyModifiedPropertiesWithoutUndo();

            // 歩いている間の位置は、ジャイロと歩数で進める（ARCore が地下で位置を見失いやすいため）
            var tracker = routeGo.AddComponent<DeadReckoningTracker>();
            so = new SerializedObject(tracker);
            so.FindProperty("cameraTransform").objectReferenceValue = origin.Camera.transform;
            so.ApplyModifiedPropertiesWithoutUndo();
            so = new SerializedObject(aligner);
            so.FindProperty("keepRouteWhenTrackingLost").boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();

            // 確認用: 歩数の数え方と、ジャイロで取る向きを端末で確かめる画面（確認が済んだら外してよい）
            var diagnostics = routeGo.AddComponent<SensorDiagnostics>();
            so = new SerializedObject(diagnostics);
            so.FindProperty("cameraTransform").objectReferenceValue = origin.Camera.transform;
            so.ApplyModifiedPropertiesWithoutUndo();

            // 開発用の表示は、画面の左上を素早く 3 回たたくと出る（デモでは隠しておく）
            routeGo.AddComponent<DevOverlayToggle>();

            // 利用者向けの画面（設定 → 読み取り → 案内 → 到着）
            routeGo.AddComponent<EkiNaviUI>();

            // ARCore は既定で 640×480 を選ぶことがあるので、起動したら大きい解像度に切り替える
            if (!origin.Camera.TryGetComponent<CameraResolutionSelector>(out _))
                origin.Camera.gameObject.AddComponent<CameraResolutionSelector>();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);

            // ビルドしたアプリがこのシーンから始まるようにする
            var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();

            int imageCount = library != null ? library.count : 0;
            Debug.Log($"[EkiNavi] {ScenePath} を経路 {routeName} で作りました（参照画像 {imageCount} 枚）");
            Selection.activeObject = library;
        }

    }
}
