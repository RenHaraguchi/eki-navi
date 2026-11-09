using UnityEngine;
using UnityEngine.InputSystem;

namespace EkiNavi.Route
{
    /// <summary>
    /// 開発用の表示（確認用の画面、歩数と推測航法の状態、Scan sign の詳しい結果）を出すかどうか。
    /// デモでは隠し、利用者向けの案内（かざしてください、矢印、曲がり角までの距離）だけを出す。
    /// </summary>
    public static class DevOverlay
    {
        /// <summary>開発用の表示を出すか。</summary>
        public static bool Visible { get; set; }
    }
}
