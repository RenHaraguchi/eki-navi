using System.IO;
using EkiNavi.Route;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace EkiNavi.EditorTools
{
    /// <summary>
    /// 歩数計測の確認用シーン（Assets/EkiNavi/Scenes/StepProbe.unity）を作り直す。
    /// メニュー「EkiNavi > 歩数計測の確認シーンを作る」から実行する。ビルドの設定（最初のシーン）は変えない。
    /// </summary>
    public static class StepProbeSceneBuilder
    {
        public const string ScenePath = "Assets/EkiNavi/Scenes/StepProbe.unity";

        [MenuItem("EkiNavi/歩数計測の確認シーンを作る")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            EditorApplication.ExecuteMenuItem("GameObject/XR/AR Session");
            Selection.activeObject = null;
            EditorApplication.ExecuteMenuItem("GameObject/XR/XR Origin (Mobile AR)");
            var origin = Object.FindFirstObjectByType<XROrigin>();
            origin.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            var probe = new GameObject("Step Probe").AddComponent<StepProbe>();
            var so = new SerializedObject(probe);
            so.FindProperty("cameraTransform").objectReferenceValue = origin.Camera.transform;
            so.ApplyModifiedPropertiesWithoutUndo();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[EkiNavi] {ScenePath} を作りました");
        }
    }
}
