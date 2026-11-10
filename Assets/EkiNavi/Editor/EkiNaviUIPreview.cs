using EkiNavi.Route;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EkiNavi.EditorTools
{
    /// <summary>
    /// 保存しない空のシーンに画面だけを置いて再生する。AR もサーバーも使わないので、
    /// SignRouteDemo.unity を作り直さずに画面を確かめられる。
    /// 再生中に数字キーで切り替える（1 駅を選ぶ、2 読み取り、3 読み取りの結果、4 案内、5 曲がり角、6 経路外れ、7 見失った、8 到着、9 向きの注意、0 矢印と逆を向いている、Q 路線、W アカウント設定、E 縦に戻す案内、R 写真の出典、T オープンソースライセンス）。
    /// </summary>
    public static class EkiNaviUIPreview
    {
        [MenuItem("EkiNavi/画面のプレビューを開く")]
        public static void Open()
        {
            if (EditorApplication.isPlaying || !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // カメラの映像の代わりの背景
            var camera = new GameObject("Preview Camera").AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.32f, 0.36f, 0.42f);

            new GameObject("EkiNavi UI Preview").AddComponent<EkiNaviUI>();
            EditorApplication.EnterPlaymode();
        }
    }
}
