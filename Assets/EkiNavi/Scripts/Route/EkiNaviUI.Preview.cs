#if UNITY_EDITOR
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EkiNavi.Route
{
    /// <summary>
    /// エディターで AR なしに全部の画面を見るためのプレビュー（メニュー「EkiNavi > 画面のプレビューを開く」）。
    /// シーンに AR の部品がないときだけ動き、数字キーで画面と状態を切り替える。APK には入らない。
    /// </summary>
    public partial class EkiNaviUI
    {
        const float PreviewTotalMeters = 120f;

        bool IsPreview => vision == null && arrow == null;

        static readonly Key[] PreviewKeys =
        {
            Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5, Key.Digit6, Key.Digit7, Key.Digit8, Key.Digit9, Key.Digit0, Key.Q, Key.W, Key.E, Key.R, Key.T,
        };

        partial void UpdatePreview()
        {
            if (!IsPreview || Keyboard.current == null) return;
            for (int i = 0; i < PreviewKeys.Length; i++)
                if (Keyboard.current[PreviewKeys[i]].wasPressedThisFrame) ShowPreviewState(i + 1);
        }

        /// <summary>プレビューの画面に切り替える（1 駅を選ぶ、2 読み取り、3 読み取りの結果、4 案内、5 曲がり角、6 経路外れ、7 見失った、8 到着、9 向きの注意、0 矢印と逆を向いている、Q 路線、W アカウント設定、E 縦に戻す案内、R 写真の出典、T オープンソースライセンス）。</summary>
        public void ShowPreviewState(int number)
        {
            switch (number)
            {
                case 1: ShowSetupPreview(); break;
                case 2: ShowScanPreview(null); break;
                case 3: ShowScanPreview(ScanMessage("No known sign in view")); break;
                case 4: directionWarning = false; ShowGuidePreview(StraightPreview(), true); break;
                case 11: stationIndex = Array.FindIndex(Stations, s => s.ready); Show(Screen.Line); break;
                case 12: Show(Screen.Account); break;
                case 14: Show(Screen.Credits); break;
                case 15: Show(Screen.Licenses); break;
                case 13: directionWarning = false; ShowGuidePreview(new GuideInfo { Guiding = false, WaitingForPortrait = true }, false); break;
                case 10: directionWarning = false; ShowGuidePreview(new GuideInfo { Guiding = true, FacingAway = true, RemainingMeters = 90f, TotalMeters = PreviewTotalMeters }, false); break;
                case 9: directionWarning = true; ShowGuidePreview(StraightPreview(), false); break;
                case 5: directionWarning = false; ShowGuidePreview(TurnPreview(), false); break;
                case 6: directionWarning = false; ShowGuidePreview(new GuideInfo { Guiding = true, OffRoute = true, OffRouteMeters = 12f, TotalMeters = PreviewTotalMeters }, false); break;
                case 7: directionWarning = false; ShowGuidePreview(new GuideInfo { Guiding = false }, false); break;
                case 8: directionWarning = false; ShowGuidePreview(new GuideInfo { Arrived = true }, false); break;
            }
        }

        // 「読み取る」は、少し待ってから成功した扱いで案内の画面へ進む
        partial void PreviewScan()
        {
            if (IsPreview && !scanBusyOverride) StartCoroutine(PreviewScanRoutine());
        }

        IEnumerator PreviewScanRoutine()
        {
            scanBusyOverride = true;
            scanStatusOverride = null;
            yield return new WaitForSecondsRealtime(1f);
            scanBusyOverride = false;
            directionWarning = false;
            if (screen == Screen.Scan) ShowGuidePreview(StraightPreview(), true);
        }

        void ShowSetupPreview()
        {
            guideOverride = null;
            scanStatusOverride = null;
            hasGuide = false;
            Show(Screen.Stations);
        }

        void ShowScanPreview(string status)
        {
            scanStatusOverride = status;
            Show(Screen.Scan);
        }

        void ShowGuidePreview(GuideInfo guide, bool withToast)
        {
            guideOverride = guide;
            hasGuide = true;
            scanStatusOverride = null;
            alignedSignName = "中央西改札前の案内板";
            toastUntil = withToast ? Time.unscaledTime + ToastSeconds : 0f;
            toastLift = ToastHiddenLift;
            Show(Screen.Guide);
        }

        static GuideInfo StraightPreview() => new GuideInfo { Guiding = true, RemainingMeters = 90f, TotalMeters = PreviewTotalMeters };

        static GuideInfo TurnPreview() => new GuideInfo
        {
            Guiding = true,
            HasNextTurn = true,
            NextTurnMeters = 20f,
            RemainingMeters = 60f,
            TotalMeters = PreviewTotalMeters,
        };

    }
}
#endif
