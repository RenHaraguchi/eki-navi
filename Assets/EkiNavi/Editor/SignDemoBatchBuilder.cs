using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.ARSubsystems;
using UnityEngine;
using UnityEngine.XR.ARSubsystems;

namespace EkiNavi.EditorTools
{
    /// <summary>
    /// 案内板の経路デモをコマンドラインから APK まで作る。
    /// 1. SignRouteDemoSceneBuilder.Build() でシーンと参照画像ライブラリを作る
    /// 2. signs.json の imageName と同名の画像が Images/ にあれば、ライブラリに実寸つきで登録する
    /// 3. SignRouteDemo.unity だけを入れた APK を Builds/eki-navi.apk に作る
    ///
    /// 実行例:
    ///   Unity -batchmode -quit -buildTarget Android -projectPath . \
    ///     -executeMethod EkiNavi.EditorTools.SignDemoBatchBuilder.BuildApk -logFile -
    ///
    /// 経路は環境変数 EKINAVI_ROUTE で選べる（例: EKINAVI_ROUTE=b1_9_10_to_odakyu で地下1階の 9・10番線 → 小田急線）
    /// </summary>
    public static class SignDemoBatchBuilder
    {
        const string ScenePath = "Assets/EkiNavi/Scenes/SignRouteDemo.unity";
        const string SignsPath = "Assets/EkiNavi/Data/signs.json";
        const string LibraryPath = "Assets/EkiNavi/ImageTracking/SignLibrary.asset";
        const string ImagesDir = "Assets/EkiNavi/ImageTracking/Images";
        const string ApkPath = "Builds/eki-navi.apk";

        // ARCore の画像データベースに入れられない画像（arcoreimg で特徴点不足と判定されたもの）。
        // 1枚でも失敗するとライブラリ全体が作れないので外す
        static readonly string[] SkipImages = { "sign_2f_r01_a" };

        // 切り分け用の試験画像（名前, パス, 実寸の幅 m）。普段は空。
        // 画像認識そのものが動くかを確かめたいときに、模様の細かい写真（arcoreimg で 75 点以上）を足す。
        // signs.json に無い名前なので、認識すると画面に「Unknown image: 名前」と出る
        static readonly (string name, string path, float width)[] TestImages =
        {
        };

        // signs.json の読み取りに使う最小限の型（使う項目だけ）
        [Serializable] class SignEntry { public string id; public string imageName; public float widthMeters; }
        [Serializable] class SignList { public SignEntry[] signs; }

        [MenuItem("EkiNavi/参照画像を signs.json から登録する")]
        public static void RegisterImagesMenu() => RegisterImages();

        /// <summary>コマンドラインから呼ぶ入口。失敗したら終了コード 1 で終わる</summary>
        public static void BuildApk()
        {
            try
            {
                SignRouteDemoSceneBuilder.Build();
                // 案内板の写真はリポジトリに入れないので、登録が 0 枚でも続ける（読み取りは Gemini で行う）
                RegisterImages();
                Build();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[EkiNavi] ビルド失敗: {e}");
                EditorApplication.Exit(1);
            }
        }

        /// <summary>signs.json を読み、画像があるものをライブラリに登録する。登録した枚数を返す</summary>
        public static int RegisterImages()
        {
            var library = AssetDatabase.LoadAssetAtPath<XRReferenceImageLibrary>(LibraryPath);
            if (library == null)
            {
                Debug.Log("[EkiNavi] 参照画像ライブラリがないので、画像の登録は飛ばします");
                return 0;
            }
            var json = AssetDatabase.LoadAssetAtPath<TextAsset>(SignsPath)
                ?? throw new Exception($"{SignsPath} がありません");
            var list = JsonUtility.FromJson<SignList>(json.text);

            // 毎回空にしてから登録し直す（除外した画像が残らないようにする）
            while (library.count > 0) library.RemoveAt(library.count - 1);

            var registered = 0;
            foreach (var sign in list.signs ?? Array.Empty<SignEntry>())
            {
                if (string.IsNullOrEmpty(sign.imageName)) continue;
                if (Array.IndexOf(SkipImages, sign.imageName) >= 0)
                {
                    Debug.Log($"[EkiNavi] 除外: {sign.imageName}（ARCore の判定で特徴点不足）");
                    continue;
                }
                // 上下に白い余白を足した版（{imageName}_padded.png）があればそちらを使う。
                // 横長すぎる画像は arcoreimg が判定できないため。幅は同じなので widthMeters はそのまま使える
                var texturePath = $"{ImagesDir}/{sign.imageName}_padded.png";
                if (AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath) == null)
                    texturePath = $"{ImagesDir}/{sign.imageName}.png";
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
                if (texture == null) continue; // 画像のない仮データは飛ばす
                if (sign.widthMeters <= 0) throw new Exception($"{sign.id} の widthMeters が不正です");

                AddImage(library, sign.imageName, texturePath, sign.widthMeters);
                registered++;
            }

            // 切り分け用の試験画像（signs.json には載せない。認識すると画面に「Unknown image: 名前」と出る）
            foreach (var (name, path, width) in TestImages)
            {
                if (AssetDatabase.LoadAssetAtPath<Texture2D>(path) == null) continue;
                AddImage(library, name, path, width);
            }

            EditorUtility.SetDirty(library);
            AssetDatabase.SaveAssets();
            Debug.Log($"[EkiNavi] ライブラリの登録数: {library.count}（今回 {registered} 枚）");
            return registered;
        }

        /// <summary>画像を1枚ライブラリに足す。名前・実寸（幅は指定、高さは元画像の比から）を設定する</summary>
        static void AddImage(XRReferenceImageLibrary library, string name, string texturePath, float widthMeters)
        {
            // 高さは元画像の縦横比から出す（取り込み後のテクスチャは2の累乗に縮められて比が変わるため使わない）
            var importer = (TextureImporter)AssetImporter.GetAtPath(texturePath);
            importer.GetSourceTextureWidthAndHeight(out var srcWidth, out var srcHeight);
            if (importer.npotScale != TextureImporterNPOTScale.None)
            {
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.SaveAndReimport();
            }
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            var height = widthMeters * srcHeight / srcWidth;

            library.Add();
            var index = library.count - 1;
            library.SetName(index, name);
            library.SetTexture(index, texture, false);
            library.SetSpecifySize(index, true);
            library.SetSize(index, new Vector2(widthMeters, height));
            Debug.Log($"[EkiNavi] 参照画像を登録: {name} (元 {srcWidth}x{srcHeight}px) → {widthMeters:F2}m x {height:F3}m");
        }

        static void Build()
        {
            EditorUserBuildSettings.buildAppBundle = false;
            Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));
            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log($"[EkiNavi] ビルド結果: {summary.result} / {summary.totalTime} / {summary.totalSize} bytes / エラー {summary.totalErrors}");
            if (summary.result != BuildResult.Succeeded)
                throw new Exception($"ビルドが {summary.result} で終わりました");
        }
    }
}
