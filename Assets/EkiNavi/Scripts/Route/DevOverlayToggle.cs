using UnityEngine;
using UnityEngine.InputSystem;

namespace EkiNavi.Route
{
    /// <summary>
    /// 画面の左上を素早く 3 回たたくと、開発用の表示を出し入れする（エディターでは D キーでも）。
    /// エディターと Development Build だけで動く。通常のビルドでは出さない。
    /// 2 本指のタップ（SignRouteAligner の合わせ直し）とぶつからないよう、1 本指の連打にしている。
    /// </summary>
    public class DevOverlayToggle : MonoBehaviour
    {
        [Tooltip("起動したときに開発用の表示を出すか")]
        [SerializeField] bool visibleOnStart;
        [Tooltip("連打とみなす間隔（秒）")]
        [SerializeField] float tapWindowSeconds = 1.5f;

        int taps;
        float firstTapTime;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        void Awake() => DevOverlay.Visible = visibleOnStart;
#else
        void Awake() => DevOverlay.Visible = false;
#endif

        void Update()
        {
#if !(UNITY_EDITOR || DEVELOPMENT_BUILD)
            // 通常のビルド（デモで配る APK）では、開発用の表示を出さない
            return;
#else
            var kb = Keyboard.current;
            if (kb != null && kb.dKey.wasPressedThisFrame) Toggle();

            var ts = Touchscreen.current;
            if (ts == null || !ts.primaryTouch.press.wasPressedThisFrame) return;
            // Input System の画面の座標は左下が原点
            var p = ts.primaryTouch.position.ReadValue();
            bool topLeft = p.x < Screen.width * 0.2f && p.y > Screen.height * 0.88f;
            if (!topLeft) return;

            if (taps == 0 || Time.unscaledTime - firstTapTime > tapWindowSeconds)
            {
                taps = 0;
                firstTapTime = Time.unscaledTime;
            }
            if (++taps >= 3)
            {
                taps = 0;
                Toggle();
            }
#endif
        }

        static void Toggle()
        {
            DevOverlay.Visible = !DevOverlay.Visible;
            Debug.Log($"[EkiNavi] 開発用の表示: {(DevOverlay.Visible ? "出す" : "隠す")}");
        }
    }
}
