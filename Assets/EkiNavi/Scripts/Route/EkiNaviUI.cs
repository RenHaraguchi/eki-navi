using System;
using System.Collections.Generic;
using System.Linq;
using EkiNavi.Alignment;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace EkiNavi.Route
{
    /// <summary>
    /// 利用者向けの画面（デザインの試作）。画面の流れ:
    /// 1. 設定: 言語・駅・行き先の路線を選ぶ
    /// 2. 読み取り: 近くの案内板にカメラを向けて「読み取る」を押す（SignVisionAligner.Scan）
    /// 3. 現在地: 合わせられたら、どの案内板の前にいるかを出す
    /// 4. 案内: 次の曲がり角までの距離と、ゴールまでの残りを出す（RouteArrow の状態を読む）
    /// 5. 到着: ゴールに着いたら、着いたことを出す
    ///
    /// 画面はコードで組み立てる（シーンのファイルを手で編集しなくてよいように）。
    /// 選んだ言語は表示の文言にだけ使い、駅と行き先は今は見た目だけ（経路はシーンの経路のまま）。
    /// 開発用の表示（DevOverlay）を出している間は、これまでの英語の表示も出す。
    /// </summary>
    public partial class EkiNaviUI : MonoBehaviour
    {
        enum Screen { Stations, Line, Account, Scan, Guide, Arrived }

        // アイコン（#15）に合わせた色
        static readonly Color Navy = Hex("#0017C1");
        static readonly Color Blue = Hex("#3460FB");
        static readonly Color Ink = Hex("#1A1F36");
        static readonly Color SubInk = Hex("#5B6178");
        static readonly Color Line = Hex("#D9DEEA");
        static readonly Color Paper = Hex("#F4F6FB");
        static readonly Color Success = Hex("#1F9D55");
        static readonly Color Warning = Hex("#E8590C");

        [SerializeField] SignVisionAligner vision;
        [SerializeField] SignRouteAligner aligner;
        [SerializeField] RouteArrow arrow;
        [Tooltip("ゴールの名前（到着の画面に出す）")]
        [SerializeField] string goalNameJa = "小田急線のりば";
        [SerializeField] string goalNameEn = "Odakyu Line";

        Screen screen = Screen.Stations;
        string language = "ja";
        int stationIndex = -1;  // 駅を選ぶまでは -1
        int destinationIndex;
        Font font;
        Sprite rounded, circle, gear, barCorner;
        Transform root;
        readonly Dictionary<Screen, GameObject> screens = new Dictionary<Screen, GameObject>();
        readonly List<Action> refreshers = new List<Action>();
        readonly List<GridLayoutGroup> stationGrids = new List<GridLayoutGroup>();

        // 読み取りと案内の画面で毎フレーム書き換える部品
        Text scanStatus, toastText, guideIcon, guideMain, guideSub, guideDestination;
        GameObject scanStatusPill, toast, guideCard, guideBar, guideOverlay;
        Text overlayTitle, overlaySub;
        Image guideCardImage;
        RectTransform progressFill;
        Button scanButton;
        float toastUntil;
        string alignedSignName = "";
        bool directionWarning;
        bool hasGuide;  // 一度でも位置が合って案内を始めたか（読み取りの画面から案内へ戻れる）

        /// <summary>案内の画面に出す内容。ふだんは RouteArrow から読む。</summary>
        struct GuideInfo
        {
            public bool Guiding, Arrived, HasNextTurn, NextTurnIsRight, OffRoute, FacingAway, WaitingForPortrait;
            public float RemainingMeters, TotalMeters, NextTurnMeters, OffRouteMeters;
        }

        // プレビュー（EkiNaviUI.Preview.cs）で表示の内容を差し替えるときに使う。ふだんは空のまま
        GuideInfo? guideOverride;
        string scanStatusOverride;
        bool scanBusyOverride;

        partial void UpdatePreview();
        partial void PreviewScan();

        const string LanguageKey = "ekinavi.language";
        const float StationLabelHeight = 104f;
        const float HeaderHeight = 220f;

        static readonly string[] Languages = { "ja", "en", "zh", "ko" };
        static readonly string[] LanguageLabels = { "日本語", "English", "中文", "한국어" };
        // 駅の一覧。地方ごとに並べる。ready が false の駅は「準備中」と出して押せないようにする
        static readonly (string ja, string en, (string ja, string en, bool ready)[] stations)[] Regions =
        {
            ("関東", "Kanto", new[]
            {
                ("東京", "Tokyo", false),
                ("新宿", "Shinjuku", true),
                ("池袋", "Ikebukuro", false),
                ("渋谷", "Shibuya", false),
            }),
            ("関西", "Kansai", new[]
            {
                ("大阪", "Osaka", false),
                ("京都", "Kyoto", false),
            }),
        };
        static readonly (string ja, string en, bool ready)[] Stations = Regions.SelectMany(r => r.stations).ToArray();
        // 路線の色は仮の値。小田急と京王は公式の資料の記号から読み取った色、都営と東京メトロは未確認
        static readonly (string ja, string en, string color, string code)[] Destinations =
        {
            ("小田急線", "Odakyu Line", "#008CD0", "OH"),
            ("京王線", "Keio Line", "#C60076", "KO"),
            ("都営新宿線", "Toei Shinjuku Line", "#6CBB5A", "S"),
            ("東京メトロ丸ノ内線", "Marunouchi Line", "#F62E36", "M"),
        };

        // 文言（言語は日本語と英語。ほかの言語は英語で出す）
        static readonly Dictionary<string, (string ja, string en)> Words = new Dictionary<string, (string, string)>
        {
            ["app"] = ("駅ナビAR", "Eki Navi AR"),
            ["destination"] = ("乗り換える路線", "Line to transfer to"),
            ["soon"] = ("準備中", "Coming soon"),
            ["start"] = ("案内を始める", "Start"),
            ["scanStep"] = ("現在地を確かめます", "Find where you are"),
            ["scanHint"] = ("近くの案内板にカメラを向けて\n「読み取る」を押してください", "Point the camera at a nearby sign\nand tap Scan"),
            ["scan"] = ("読み取る", "Scan"),
            ["scanning"] = ("読み取り中…", "Scanning..."),
            ["back"] = ("← 路線を選ぶ", "← Select line"),
            ["pickStation"] = ("駅を選ぶ", "Select a station"),
            ["stationTitle"] = ("{0}駅", "{0} Station"),
            ["account"] = ("アカウント設定", "Account settings"),
            ["accountSection"] = ("アカウント", "Account"),
            ["accountName"] = ("ゲスト（仮のアカウント）", "Guest (temporary)"),
            ["languageTitle"] = ("言語", "Language"),
            ["aboutSection"] = ("このアプリについて", "About this app"),
            ["privacy"] = ("プライバシーポリシー", "Privacy policy"),
            ["terms"] = ("利用規約", "Terms of use"),
            ["licenses"] = ("オープンソースライセンス", "Open source licenses"),
            ["contact"] = ("お問い合わせ", "Contact"),
            ["version"] = ("バージョン", "Version"),
            ["backToGuide"] = ("← 案内に戻る", "← Back to guide"),
            ["scanHold"] = ("そのまま動かさないでください", "Hold still..."),
            ["scanChecking"] = ("案内板を確かめています…", "Checking the sign..."),
            ["scanNoSign"] = ("登録された案内板が見つかりません。\n案内板にカメラを向けて、もう一度押してください", "No registered sign found.\nPoint at a sign and try again"),
            ["scanUnavailable"] = ("今は案内板を確かめられません。もう一度試してください", "Cannot check the sign now. Try again"),
            ["scanTooFar"] = ("案内板をうまく測れません。近づいてもう一度試してください", "Could not measure the sign. Move closer and try again"),
            ["scanFailed"] = ("読み取れませんでした。もう一度試してください", "Could not scan. Try again"),
            ["directionTitle"] = ("向きがずれているかもしれません", "The direction may be off"),
            ["directionSub"] = ("3 m 以内でもう一度読み取ってください", "Scan again within 3 m"),
            ["located"] = ("現在地が分かりました", "Found your location"),
            ["turnIn"] = ("{0} m 先を{1}へ", "Turn {1} in {0} m"),
            ["left"] = ("左", "left"),
            ["right"] = ("右", "right"),
            ["faceArrow"] = ("矢印の方向を\n向いてください", "Turn to face the arrow"),
            ["straight"] = ("このまま まっすぐ", "Go straight"),
            ["toGoal"] = ("{0}まで あと {1} m", "{1} m to {0}"),
            ["offRoute"] = ("経路から外れています", "You are off the route"),
            ["upright"] = ("スマホを縦に\n戻してください", "Hold your phone upright"),
            ["uprightSub"] = ("縦に戻すと矢印が出ます", "The arrow appears when the phone is upright"),
            ["offRouteSub"] = ("矢印の向きに戻ってください（{0} m）", "Follow the arrow back ({0} m)"),
            ["platform"] = ("のりば", ""),
            ["rescan"] = ("読み取り直す", "Rescan"),
            ["lost"] = ("位置を見失いました", "Lost your position"),
            ["lostSub"] = ("案内板を読み取り直してください", "Scan a sign again"),
            ["arrived"] = ("{0}に\n着きました", "You have arrived at\n{0}"),
            ["arrivedSub"] = ("おつかれさまでした", "Have a nice trip"),
            ["restart"] = ("最初に戻る", "Back to start"),
        };

        string T(string key) => language == "ja" ? Words[key].ja : Words[key].en;
        string Goal => language == "ja" ? goalNameJa : goalNameEn;

        void Awake()
        {
            if (vision == null) vision = FindFirstObjectByType<SignVisionAligner>();
            if (aligner == null) aligner = FindFirstObjectByType<SignRouteAligner>();
            if (arrow == null) arrow = FindFirstObjectByType<RouteArrow>();
            font = LoadFont();
            rounded = RoundedSprite(48);
            circle = RoundedSprite(128);
            gear = GearSprite();
            barCorner = RoundedSprite(12);  // バーの高さ 24 の半分。端が半円になる
            string savedLanguage = PlayerPrefs.GetString(LanguageKey, "ja");
            if (Languages.Contains(savedLanguage)) language = savedLanguage;
            EnsureEventSystem();
            Build();
            Show(Screen.Stations);
        }

        void OnEnable()
        {
            if (vision != null) vision.Aligned += OnAligned;
        }

        void OnDisable()
        {
            if (vision != null) vision.Aligned -= OnAligned;
        }

        void OnAligned(SignEntry sign)
        {
            alignedSignName = sign != null ? sign.name : "";
            if (arrow != null) arrow.ResetArrival();
            hasGuide = true;
            directionWarning = vision != null && vision.DirectionUncertain;
            toastUntil = Time.unscaledTime + 3f;
            Show(Screen.Guide);
        }

        void Update()
        {
            // これまでの英語の表示は、開発用の表示を出しているときだけ
            bool dev = DevOverlay.Visible;
            if (vision != null) vision.ShowButton = dev;
            if (aligner != null) aligner.ShowStatus = dev;
            if (arrow != null) arrow.ShowText = dev;

            UpdatePreview();
            FitStationGrids();

            switch (screen)
            {
                case Screen.Scan:
                    UpdateScan();
                    break;
                case Screen.Guide:
                    UpdateGuide();
                    break;
            }
        }

        void UpdateScan()
        {
            bool busy = scanBusyOverride || (vision != null && vision.Busy);
            scanButton.interactable = !busy;
            scanButton.GetComponentInChildren<Text>().text = busy ? T("scanning") : T("scan");
            string status = scanStatusOverride ?? (vision != null ? ScanMessage(vision.Status) : "");
            scanStatusPill.SetActive(!string.IsNullOrEmpty(status));
            scanStatus.text = status;
        }

        /// <summary>読み取りの結果（開発用の英語の文）を、利用者向けの文にする。開発用の文はそのまま出さない。</summary>
        string ScanMessage(string status)
        {
            if (string.IsNullOrEmpty(status) || status.StartsWith("Aligned at")) return "";
            if (status.StartsWith("Hold still")) return T("scanHold");
            if (status.StartsWith("Sending")) return T("scanChecking");
            if (status.StartsWith("No known sign")) return T("scanNoSign");
            if (status.StartsWith("Server error") || status.StartsWith("Sign check unavailable")) return T("scanUnavailable");
            if (status.Contains("box looks wrong")) return T("scanTooFar");
            return T("scanFailed");
        }

        void UpdateGuide()
        {
            if (guideOverride == null && arrow == null) return;
            var guide = guideOverride ?? ReadArrow();
            if (guide.Arrived)
            {
                Show(Screen.Arrived);
                return;
            }

            // 読み取り直してほしいときは、グレーの全面表示にする（案内のカードと下の帯は隠す）
            bool overlay = directionWarning || (!guide.Guiding && !guide.WaitingForPortrait);
            guideOverlay.SetActive(overlay);
            guideCard.SetActive(!overlay);
            guideBar.SetActive(!overlay);
            toast.SetActive(!overlay && Time.unscaledTime < toastUntil);
            if (overlay)
            {
                overlayTitle.text = T(directionWarning ? "directionTitle" : "lost");
                overlaySub.text = T(directionWarning ? "directionSub" : "lostSub");
                return;
            }

            guideDestination.text = T("platform");

            if (guide.WaitingForPortrait)
            {
                guideCardImage.color = Navy;
                guideIcon.text = "↻";
                guideMain.text = T("upright");
                guideSub.text = T("uprightSub");
                return;
            }
            if (guide.OffRoute)
            {
                guideCardImage.color = Warning;
                guideIcon.text = "!";
                guideMain.text = T("offRoute");
                guideSub.text = string.Format(T("offRouteSub"), Mathf.RoundToInt(guide.OffRouteMeters));
                return;
            }
            // 進み具合は、案内できている間だけ更新する（見失っている間は直前のまま）
            float total = Mathf.Max(1f, guide.TotalMeters);
            progressFill.anchorMax = new Vector2(Mathf.Clamp01(1f - guide.RemainingMeters / total), 1f);
            guideCardImage.color = Navy;
            if (guide.FacingAway)
            {
                guideIcon.text = "!";
                guideMain.text = T("faceArrow");
            }
            else if (guide.HasNextTurn && guide.NextTurnMeters < 60f)
            {
                guideIcon.text = guide.NextTurnIsRight ? "→" : "←";
                guideMain.text = string.Format(T("turnIn"), Mathf.RoundToInt(guide.NextTurnMeters), T(guide.NextTurnIsRight ? "right" : "left"));
            }
            else
            {
                guideIcon.text = "↑";
                guideMain.text = T("straight");
            }
            guideSub.text = string.Format(T("toGoal"), Goal, Mathf.RoundToInt(guide.RemainingMeters));
        }

        GuideInfo ReadArrow() => new GuideInfo
        {
            Guiding = arrow.IsGuiding,
            Arrived = arrow.IsArrived,
            HasNextTurn = arrow.HasNextTurn,
            NextTurnIsRight = arrow.NextTurnIsRight,
            OffRoute = arrow.IsOffRoute,
            FacingAway = arrow.IsFacingAway,
            WaitingForPortrait = arrow.WaitingForPortrait,
            RemainingMeters = arrow.RemainingMeters,
            TotalMeters = arrow.TotalMeters,
            NextTurnMeters = arrow.NextTurnMeters,
            OffRouteMeters = arrow.OffRouteMeters,
        };

        void Show(Screen next)
        {
            screen = next;
            // 読み取りの画面を開き直したときは、前の結果を残さない
            if (next == Screen.Scan && vision != null && !vision.Busy) vision.ClearStatus();
            if (next == Screen.Scan) directionWarning = false;
            foreach (var pair in screens) pair.Value.SetActive(pair.Key == next);
            Refresh();
        }

        // ---- 画面を組み立てる ----

        void Build()
        {
            var canvasGo = new GameObject("EkiNavi UI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;
            root = canvasGo.transform;

            screens[Screen.Stations] = BuildStations();
            screens[Screen.Line] = BuildLine();
            screens[Screen.Account] = BuildAccount();
            screens[Screen.Scan] = BuildScan();
            screens[Screen.Guide] = BuildGuide();
            screens[Screen.Arrived] = BuildArrived();
        }

        // 画面の上の帯。onBack があれば左に戻るボタンを出す
        void PageHeader(Transform page, Func<string> title, Action onBack)
        {
            var header = Panel(page, "Header", Navy, null);
            Anchor(header, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -HeaderHeight), Vector2.zero);
            var label = Label(header.transform, "", 56, Color.white, FontStyle.Bold, TextAnchor.LowerLeft);
            Anchor(label.gameObject, Vector2.zero, Vector2.one, new Vector2(onBack == null ? 56 : 140, 44), new Vector2(-56, -80));
            refreshers.Add(() => label.text = title());
            if (onBack == null) return;
            var back = TextButton(header.transform, onBack);
            Anchor(back.gameObject, Vector2.zero, Vector2.zero, new Vector2(30, 26), new Vector2(130, 126));
            var arrowLabel = back.GetComponentInChildren<Text>();
            arrowLabel.text = "←";
            arrowLabel.fontSize = 60;
            arrowLabel.alignment = TextAnchor.MiddleCenter;
        }

        GameObject BuildStations()
        {
            var page = Panel(root, "Stations", Color.white, null);
            Stretch(page);
            PageHeader(page.transform, () => T("pickStation"), null);

            // 駅が増えても入るよう、縦にスクロールできるようにする
            var scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(RectMask2D), typeof(Image), typeof(ScrollRect));
            scrollGo.transform.SetParent(page.transform, false);
            Anchor(scrollGo, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0, -HeaderHeight));
            scrollGo.GetComponent<Image>().color = new Color(0, 0, 0, 0);

            var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            content.transform.SetParent(scrollGo.transform, false);
            var contentRect = (RectTransform)content.transform;
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;
            var layout = content.GetComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(40, 40, 30, 220);
            layout.spacing = 20;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.content = contentRect;
            scroll.viewport = (RectTransform)scrollGo.transform;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            int stationNumber = 0;
            foreach (var region in Regions)
            {
                RegionTitle(content.transform, region.ja, region.en);
                var gridGo = new GameObject("Grid", typeof(RectTransform), typeof(GridLayoutGroup), typeof(LayoutElement));
                gridGo.transform.SetParent(content.transform, false);
                // 四角の大きさから幅が決まって広がり続けないよう、幅は親に合わせる
                var gridElement = gridGo.GetComponent<LayoutElement>();
                gridElement.minWidth = 0f;
                gridElement.preferredWidth = 0f;
                gridElement.flexibleWidth = 1f;
                var grid = gridGo.GetComponent<GridLayoutGroup>();
                grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                grid.constraintCount = 4;
                grid.spacing = new Vector2(20, 30);
                grid.cellSize = new Vector2(200, 200 + StationLabelHeight);
                stationGrids.Add(grid);
                foreach (var _ in region.stations) StationTile(gridGo.transform, stationNumber++);
            }

            var settings = Button(page.transform, Color.white, circle, () => Show(Screen.Account));
            Anchor(settings.gameObject, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-180, 60), new Vector2(-40, 200));
            var shadow = settings.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0, 0, 0, 0.25f);
            shadow.effectDistance = new Vector2(0, -4);
            var gearImage = Panel(settings.transform, "Gear", Ink, gear);
            gearImage.GetComponent<Image>().type = Image.Type.Simple;
            Stretch(gearImage, 28);
            return page;
        }

        void RegionTitle(Transform parent, string ja, string en)
        {
            var go = new GameObject("Region", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<LayoutElement>().preferredHeight = 90;
            var left = Label(go.transform, ja, 46, Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            Stretch(left.gameObject);
            var right = Label(go.transform, en, 32, SubInk, FontStyle.Normal, TextAnchor.MiddleRight);
            Stretch(right.gameObject);
            var line = Panel(go.transform, "Line", Line, null);
            Anchor(line, Vector2.zero, new Vector2(1, 0), Vector2.zero, new Vector2(0, 3));
        }

        // 写真の代わりに、色の四角と駅名の 1 文字目を出す（写真はあとで差し替える）
        void StationTile(Transform parent, int index)
        {
            var station = Stations[index];
            var go = new GameObject("Station", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0, 0, 0, 0);
            var button = go.GetComponent<Button>();
            button.interactable = station.ready;
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() =>
            {
                stationIndex = index;
                Show(Screen.Line);
            });

            var photo = Panel(go.transform, "Photo", station.ready ? Blue : Hex("#C9CEDA"), rounded);
            Anchor(photo, Vector2.zero, Vector2.one, new Vector2(0, StationLabelHeight), Vector2.zero);
            if (station.ready)
            {
                var initial = Label(photo.transform, station.ja.Substring(0, 1), 96, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
                Stretch(initial.gameObject);
            }
            else
            {
                var soon = Label(photo.transform, "", 32, SubInk, FontStyle.Bold, TextAnchor.MiddleCenter);
                Stretch(soon.gameObject);
                refreshers.Add(() => soon.text = T("soon"));
            }

            var ja = Label(go.transform, station.ja, 36, station.ready ? Ink : SubInk, FontStyle.Bold, TextAnchor.MiddleLeft);
            Anchor(ja.gameObject, Vector2.zero, new Vector2(1, 0), new Vector2(6, 50), new Vector2(-6, StationLabelHeight - 4));
            var en = Label(go.transform, station.en, 28, station.ready ? Ink : SubInk, FontStyle.Normal, TextAnchor.MiddleLeft);
            Anchor(en.gameObject, Vector2.zero, new Vector2(1, 0), new Vector2(6, 8), new Vector2(-6, 50));
        }

        // 画面の幅に合わせて、駅の四角が 4 列でちょうど並ぶ大きさにする
        void FitStationGrids()
        {
            if (screen != Screen.Stations) return;
            foreach (var grid in stationGrids)
            {
                float width = ((RectTransform)grid.transform).rect.width;
                if (width <= 0f) continue;
                float cell = (width - grid.spacing.x * 3f) / 4f;
                var size = new Vector2(cell, cell + StationLabelHeight);
                if (grid.cellSize != size) grid.cellSize = size;
            }
        }

        GameObject BuildLine()
        {
            var page = Panel(root, "Line", Paper, null);
            Stretch(page);
            PageHeader(page.transform, () => stationIndex >= 0
                ? string.Format(T("stationTitle"), language == "ja" ? Stations[stationIndex].ja : Stations[stationIndex].en)
                : "", () => Show(Screen.Stations));

            var body = new GameObject("Body", typeof(RectTransform), typeof(VerticalLayoutGroup));
            body.transform.SetParent(page.transform, false);
            Anchor(body, Vector2.zero, Vector2.one, new Vector2(56, 260), new Vector2(-56, -HeaderHeight - 40));
            var layout = body.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 22;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;

            SectionTitle(body.transform, "destination");
            for (int i = 0; i < Destinations.Length; i++)
            {
                int index = i;
                var item = Choice(body.transform, "", 40, () => { destinationIndex = index; Refresh(); }, 120, TextAnchor.MiddleLeft);
                var badge = LineBadge(item.transform, i, 36, 72);
                Anchor(badge, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(30, -36), new Vector2(-30, 36));
                refreshers.Add(() => Highlight(item, destinationIndex == index));
            }

            var start = PrimaryButton(page.transform, () => Show(Screen.Scan));
            Anchor(start.gameObject, Vector2.zero, new Vector2(1, 0), new Vector2(56, 80), new Vector2(-56, 230));
            refreshers.Add(() => start.GetComponentInChildren<Text>().text = T("start"));
            return page;
        }

        GameObject BuildAccount()
        {
            var page = Panel(root, "Account", Paper, null);
            Stretch(page);
            PageHeader(page.transform, () => T("account"), () => Show(Screen.Stations));

            var body = new GameObject("Body", typeof(RectTransform), typeof(VerticalLayoutGroup));
            body.transform.SetParent(page.transform, false);
            Anchor(body, Vector2.zero, Vector2.one, new Vector2(56, 40), new Vector2(-56, -HeaderHeight - 30));
            var layout = body.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 14;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;

            SectionTitle(body.transform, "accountSection");
            InfoRow(body.transform, "accountName", null);

            SectionTitle(body.transform, "languageTitle");
            var chips = Row(body.transform, 100, 16);
            for (int i = 0; i < Languages.Length; i++)
            {
                int index = i;
                var chip = Choice(chips.transform, LanguageLabels[i], 34, () =>
                {
                    language = Languages[index];
                    PlayerPrefs.SetString(LanguageKey, language);
                    PlayerPrefs.Save();
                    Refresh();
                });
                refreshers.Add(() => Highlight(chip, language == Languages[index]));
            }

            SectionTitle(body.transform, "aboutSection");
            foreach (var key in new[] { "privacy", "terms", "licenses", "contact" })
                InfoRow(body.transform, key, () => T("soon"));
            InfoRow(body.transform, "version", () => Application.version);
            return page;
        }

        void InfoRow(Transform parent, string key, Func<string> value)
        {
            var row = Panel(parent, "Row", Color.white, rounded);
            ((RectTransform)row.transform).sizeDelta = new Vector2(0, 108);
            var left = Label(row.transform, "", 38, Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            Anchor(left.gameObject, Vector2.zero, Vector2.one, new Vector2(40, 0), new Vector2(-220, 0));
            refreshers.Add(() => left.text = T(key));
            if (value == null) return;
            var right = Label(row.transform, "", 32, SubInk, FontStyle.Normal, TextAnchor.MiddleRight);
            Anchor(right.gameObject, new Vector2(1, 0), Vector2.one, new Vector2(-220, 0), new Vector2(-40, 0));
            refreshers.Add(() => right.text = value());
        }

        /// <summary>路線の記号を丸で囲んだものと、路線の色で囲んだ路線名を横に並べる。</summary>
        GameObject LineBadge(Transform parent, int index, int fontSize, float height)
        {
            var line = Destinations[index];
            var color = Hex(line.color);
            var row = new GameObject("LineBadge", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(parent, false);
            var layout = row.GetComponent<HorizontalLayoutGroup>();
            layout.spacing = 12;
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            var ring = Panel(row.transform, "Code", color, circle);
            var ringSize = ring.AddComponent<LayoutElement>();
            ringSize.preferredWidth = height;
            ringSize.preferredHeight = height;
            var inner = Panel(ring.transform, "Inner", Color.white, circle);
            Stretch(inner, height * 0.1f);
            var code = Label(inner.transform, line.code, Mathf.RoundToInt(height * 0.4f), color, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(code.gameObject);

            var pill = Panel(row.transform, "Name", color, barCorner);
            var pillSize = pill.AddComponent<LayoutElement>();
            pillSize.preferredHeight = height;
            var name = Label(pill.transform, "", fontSize, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
            name.horizontalOverflow = HorizontalWrapMode.Overflow;
            Stretch(name.gameObject);
            refreshers.Add(() =>
            {
                name.text = language == "ja" ? line.ja : line.en;
                pillSize.preferredWidth = name.preferredWidth + 44f;
            });
            return row;
        }

        GameObject BuildScan()
        {
            var page = new GameObject("Scan", typeof(RectTransform));
            page.transform.SetParent(root, false);
            Stretch(page);

            // 上の案内のカード
            var card = Panel(page.transform, "Hint", new Color(1, 1, 1, 0.94f), rounded);
            Anchor(card, new Vector2(0, 1), new Vector2(1, 1), new Vector2(40, -420), new Vector2(-40, -120));
            var step = Label(card.transform, "", 32, Blue, FontStyle.Bold, TextAnchor.UpperLeft);
            Anchor(step.gameObject, new Vector2(0, 0), new Vector2(1, 1), new Vector2(48, 30), new Vector2(-48, -36));
            var hint = Label(card.transform, "", 42, Ink, FontStyle.Bold, TextAnchor.LowerLeft);
            Anchor(hint.gameObject, new Vector2(0, 0), new Vector2(1, 1), new Vector2(48, 40), new Vector2(-48, -90));
            refreshers.Add(() => { step.text = T("scanStep"); hint.text = T("scanHint"); });

            var back = TextButton(page.transform, () => Show(Screen.Line));
            Anchor(back.gameObject, new Vector2(0, 1), new Vector2(0, 1), new Vector2(40, -110), new Vector2(360, -20));
            refreshers.Add(() => back.GetComponentInChildren<Text>().text = T("back"));

            // 読み取りに失敗しても、案内の途中なら案内へ戻れるようにする
            var backToGuide = TextButton(page.transform, () => Show(Screen.Guide));
            Anchor(backToGuide.gameObject, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-480, -110), new Vector2(-40, -20));
            backToGuide.GetComponentInChildren<Text>().alignment = TextAnchor.MiddleRight;
            refreshers.Add(() =>
            {
                backToGuide.gameObject.SetActive(hasGuide);
                backToGuide.GetComponentInChildren<Text>().text = T("backToGuide");
            });

            // 案内板を入れる枠（四隅のかぎ）
            var frame = new GameObject("Frame", typeof(RectTransform));
            frame.transform.SetParent(page.transform, false);
            Anchor(frame, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-440, -170), new Vector2(440, 330));
            foreach (var (ax, ay) in new[] { (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f) }) Corner(frame.transform, ax, ay);

            // 結果の知らせ
            scanStatusPill = Panel(page.transform, "Status", new Color(0.1f, 0.12f, 0.2f, 0.82f), rounded);
            Anchor(scanStatusPill, new Vector2(0, 0), new Vector2(1, 0), new Vector2(60, 400), new Vector2(-60, 520));
            scanStatus = Label(scanStatusPill.transform, "", 32, Color.white, FontStyle.Normal, TextAnchor.MiddleCenter);
            Stretch(scanStatus.gameObject, 24);

            // 読み取るボタン（丸）
            var ring = Panel(page.transform, "Ring", Color.white, circle);
            Anchor(ring, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(-130, 110), new Vector2(130, 370));
            scanButton = Button(ring.transform, Navy, circle, () => { if (vision != null) vision.Scan(); else PreviewScan(); });
            Stretch(scanButton.gameObject, 14);
            var scanLabel = Label(scanButton.transform, "", 40, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(scanLabel.gameObject);
            return page;
        }

        GameObject BuildGuide()
        {
            var page = new GameObject("Guide", typeof(RectTransform));
            page.transform.SetParent(root, false);
            Stretch(page);

            guideCard = Panel(page.transform, "Instruction", Navy, rounded);
            guideCardImage = guideCard.GetComponent<Image>();
            Anchor(guideCard, new Vector2(0, 1), new Vector2(1, 1), new Vector2(40, -400), new Vector2(-40, -110));
            guideIcon = Label(guideCard.transform, "↑", 130, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
            Anchor(guideIcon.gameObject, new Vector2(0, 0), new Vector2(0, 1), new Vector2(20, 0), new Vector2(220, 0));
            guideMain = Label(guideCard.transform, "", 54, Color.white, FontStyle.Bold, TextAnchor.LowerLeft);
            Anchor(guideMain.gameObject, new Vector2(0, 0.42f), new Vector2(1, 1), new Vector2(230, 0), new Vector2(-40, -30));
            guideSub = Label(guideCard.transform, "", 34, new Color(1, 1, 1, 0.85f), FontStyle.Normal, TextAnchor.UpperLeft);
            Anchor(guideSub.gameObject, new Vector2(0, 0), new Vector2(1, 0.42f), new Vector2(230, 30), new Vector2(-40, -10));

            toast = Panel(page.transform, "Located", Success, rounded);
            Anchor(toast, new Vector2(0, 1), new Vector2(1, 1), new Vector2(80, -560), new Vector2(-80, -430));
            toastText = Label(toast.transform, "", 34, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(toastText.gameObject, 20);
            refreshers.Add(() => toastText.text = T("located") + (string.IsNullOrEmpty(alignedSignName) ? "" : $"\n<size=28>{alignedSignName}</size>"));

            // 下の帯: 行き先、進み具合、読み取り直す
            var bar = Panel(page.transform, "Bottom", new Color(1, 1, 1, 0.95f), rounded);
            guideBar = bar;
            Anchor(bar, new Vector2(0, 0), new Vector2(1, 0), new Vector2(40, 60), new Vector2(-40, 300));
            // 案内先は今は小田急線に固定（路線の選択を経路につなぐときに切り替える）
            var destination = LineBadge(bar.transform, 0, 30, 58);
            Anchor(destination, new Vector2(0, 0.5f), new Vector2(0.62f, 1), new Vector2(44, 0), new Vector2(0, -20));
            guideDestination = Label(destination.transform, "", 34, Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            var track = Panel(bar.transform, "Track", Line, barCorner);
            Anchor(track, new Vector2(0, 0), new Vector2(1, 0), new Vector2(44, 50), new Vector2(-44, 74));
            var fill = Panel(track.transform, "Fill", Blue, barCorner);
            Stretch(fill);
            // 角の丸みを保つため、塗りの幅（右端の位置）で進み具合を出す
            progressFill = (RectTransform)fill.transform;
            progressFill.anchorMax = new Vector2(0f, 1f);
            var rescan = OutlineButton(bar.transform, () => Show(Screen.Scan));
            Anchor(rescan.gameObject, new Vector2(0.62f, 0.5f), new Vector2(1, 1), new Vector2(10, 4), new Vector2(-44, -24));
            refreshers.Add(() => rescan.GetComponentInChildren<Text>().text = T("rescan"));

            guideOverlay = BuildGuideOverlay(page.transform);
            return page;
        }

        // 読み取り直してほしいときの全面表示。後ろのカメラの映像が透けて見える
        GameObject BuildGuideOverlay(Transform page)
        {
            var overlay = Panel(page, "RescanOverlay", new Color(0.2f, 0.22f, 0.28f, 0.85f), null);
            overlay.GetComponent<Image>().raycastTarget = true;
            Stretch(overlay);
            overlayTitle = Label(overlay.transform, "", 56, Color.white, FontStyle.Bold, TextAnchor.LowerCenter);
            Anchor(overlayTitle.gameObject, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(50, 90), new Vector2(-50, 340));
            overlaySub = Label(overlay.transform, "", 36, new Color(1, 1, 1, 0.9f), FontStyle.Normal, TextAnchor.UpperCenter);
            Anchor(overlaySub.gameObject, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(50, -30), new Vector2(-50, 60));
            var rescan = PrimaryButton(overlay.transform, () => Show(Screen.Scan));
            Anchor(rescan.gameObject, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(110, -270), new Vector2(-110, -100));
            refreshers.Add(() => rescan.GetComponentInChildren<Text>().text = T("rescan"));
            return overlay;
        }

        GameObject BuildArrived()
        {
            var page = Panel(root, "Arrived", Success, null);
            Stretch(page);
            var badge = Panel(page.transform, "Badge", Color.white, circle);
            Anchor(badge, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(-150, 260), new Vector2(150, 560));
            Check(badge.transform);
            var title = Label(page.transform, "", 64, Color.white, FontStyle.Bold, TextAnchor.UpperCenter);
            Anchor(title.gameObject, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(60, -100), new Vector2(-60, 200));
            var sub = Label(page.transform, "", 38, new Color(1, 1, 1, 0.85f), FontStyle.Normal, TextAnchor.UpperCenter);
            Anchor(sub.gameObject, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(60, -220), new Vector2(-60, -120));
            var restart = Button(page.transform, Color.white, rounded, () =>
            {
                if (arrow != null) arrow.ResetArrival();
                hasGuide = false;
                Show(Screen.Stations);
            });
            Anchor(restart.gameObject, new Vector2(0, 0), new Vector2(1, 0), new Vector2(56, 80), new Vector2(-56, 230));
            var restartLabel = Label(restart.transform, "", 44, Success, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(restartLabel.gameObject);
            refreshers.Add(() =>
            {
                title.text = string.Format(T("arrived"), Goal);
                sub.text = T("arrivedSub");
                restartLabel.text = T("restart");
            });
            return page;
        }

        // ---- 部品 ----

        GameObject SectionTitle(Transform parent, string key)
        {
            var label = Label(parent, "", 34, SubInk, FontStyle.Bold, TextAnchor.LowerLeft);
            label.rectTransform.sizeDelta = new Vector2(0, 70);
            refreshers.Add(() => label.text = T(key));
            return label.gameObject;
        }

        void Refresh()
        {
            foreach (var refresh in refreshers) refresh();
        }

        GameObject Row(Transform parent, float height, float spacing)
        {
            var row = new GameObject("Row", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            row.transform.SetParent(parent, false);
            ((RectTransform)row.transform).sizeDelta = new Vector2(0, height);
            var h = row.GetComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.childControlWidth = true;
            h.childControlHeight = true;
            h.childForceExpandWidth = true;
            h.childForceExpandHeight = true;
            return row;
        }

        Button Choice(Transform parent, string text, int size, Action onClick, float height = 0f, TextAnchor anchor = TextAnchor.MiddleCenter)
        {
            var button = Button(parent, Color.white, rounded, onClick);
            if (height > 0f) ((RectTransform)button.transform).sizeDelta = new Vector2(0, height);
            var outline = button.gameObject.AddComponent<Outline>();
            outline.effectDistance = new Vector2(3, -3);
            outline.effectColor = Line;
            var label = Label(button.transform, text, size, Ink, FontStyle.Bold, anchor);
            Stretch(label.gameObject, 16);
            return button;
        }

        static void Highlight(Button button, bool on)
        {
            button.GetComponent<Image>().color = on ? Hex("#E8EDFF") : Color.white;
            var outline = button.GetComponent<Outline>();
            if (outline != null) outline.effectColor = on ? Blue : Line;
            var label = button.GetComponentInChildren<Text>();
            if (label != null) label.color = !button.interactable ? SubInk : on ? Navy : Ink;
        }

        Button PrimaryButton(Transform parent, Action onClick)
        {
            var button = Button(parent, Navy, rounded, onClick);
            var label = Label(button.transform, "", 46, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(label.gameObject);
            return button;
        }

        Button OutlineButton(Transform parent, Action onClick)
        {
            var button = Button(parent, Color.white, rounded, onClick);
            var outline = button.gameObject.AddComponent<Outline>();
            outline.effectDistance = new Vector2(3, -3);
            outline.effectColor = Blue;
            var label = Label(button.transform, "", 32, Blue, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(label.gameObject);
            return button;
        }

        Button TextButton(Transform parent, Action onClick)
        {
            var go = new GameObject("TextButton", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0, 0, 0, 0);
            var button = go.GetComponent<Button>();
            button.onClick.AddListener(() => onClick());
            var label = Label(go.transform, "", 34, Color.white, FontStyle.Bold, TextAnchor.MiddleLeft);
            Stretch(label.gameObject);
            var shadow = label.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0, 0, 0, 0.6f);
            return button;
        }

        Button Button(Transform parent, Color color, Sprite sprite, Action onClick)
        {
            var go = Panel(parent, "Button", color, sprite);
            // Panel は飾りとしてタップを通すので、ボタンだけはタップを受ける
            go.GetComponent<Image>().raycastTarget = true;
            var button = go.AddComponent<Button>();
            var colors = button.colors;
            colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
            colors.disabledColor = new Color(0.8f, 0.8f, 0.8f, 0.6f);
            button.colors = colors;
            button.onClick.AddListener(() => onClick());
            return button;
        }

        void Corner(Transform frame, float ax, float ay)
        {
            const float length = 90f, thickness = 12f;
            var a = new Vector2(ax, ay);
            var h = Panel(frame, "CornerH", Color.white, null);
            var v = Panel(frame, "CornerV", Color.white, null);
            float sx = ax == 0f ? 1f : -1f, sy = ay == 0f ? 1f : -1f;
            Anchor(h, a, a, new Vector2(Mathf.Min(0, sx * length), Mathf.Min(0, sy * thickness)), new Vector2(Mathf.Max(0, sx * length), Mathf.Max(0, sy * thickness)));
            Anchor(v, a, a, new Vector2(Mathf.Min(0, sx * thickness), Mathf.Min(0, sy * length)), new Vector2(Mathf.Max(0, sx * thickness), Mathf.Max(0, sy * length)));
            // 白い物に重なっても見えるよう、影を付ける
            foreach (var bar in new[] { h, v })
            {
                var shadow = bar.AddComponent<Shadow>();
                shadow.effectColor = new Color(0, 0, 0, 0.55f);
                shadow.effectDistance = new Vector2(3, -3);
            }
        }

        void Check(Transform badge)
        {
            // 文字の ✓ は端末のフォントにないことがあるので、2 本の棒で描く
            var shortBar = Panel(badge, "CheckShort", Success, rounded);
            var rt = (RectTransform)shortBar.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(110, 34);
            rt.anchoredPosition = new Vector2(-48, -10);
            rt.localRotation = Quaternion.Euler(0, 0, -45);
            var longBar = Panel(badge, "CheckLong", Success, rounded);
            rt = (RectTransform)longBar.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(190, 34);
            rt.anchoredPosition = new Vector2(26, 14);
            rt.localRotation = Quaternion.Euler(0, 0, 50);
        }

        GameObject Panel(Transform parent, string name, Color color, Sprite sprite)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.color = color;
            if (sprite != null)
            {
                image.sprite = sprite;
                image.type = sprite == circle ? Image.Type.Simple : Image.Type.Sliced;
                image.pixelsPerUnitMultiplier = 1f;
            }
            image.raycastTarget = false;
            return go;
        }

        Text Label(Transform parent, string text, int size, Color color, FontStyle style, TextAnchor anchor)
        {
            var go = new GameObject("Text", typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            var label = go.GetComponent<Text>();
            label.font = font;
            label.text = text;
            label.fontSize = size;
            label.fontStyle = style;
            label.color = color;
            label.alignment = anchor;
            label.supportRichText = true;
            label.horizontalOverflow = HorizontalWrapMode.Wrap;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = false;
            return label;
        }

        static void Stretch(GameObject go, float inset = 0f)
        {
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset);
            rt.offsetMax = new Vector2(-inset, -inset);
        }

        static void Anchor(GameObject go, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
        {
            var rt = (RectTransform)go.transform;
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
        }

        void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            go.transform.SetParent(transform, false);
        }

        /// <summary>日本語が出るフォント。端末に入っている日本語のフォントを優先し、なければ Unity の組み込み。</summary>
        static Font LoadFont()
        {
            try
            {
                string[] preferred = { "Noto Sans CJK JP", "Noto Sans JP", "Hiragino Sans", "Yu Gothic UI", "Meiryo UI", "Meiryo" };
                var installed = Font.GetOSInstalledFontNames();
                var names = preferred.Where(installed.Contains)
                    .Concat(installed.Where(n => n.Contains("CJK") || n.Contains("JP")))
                    .Distinct()
                    .ToArray();
                if (names.Length > 0) return Font.CreateDynamicFontFromOSFont(names, 40);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[EkiNavi] 端末のフォントを読めません: {e.Message}");
            }
            return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        /// <summary>角の丸い四角（9 分割で伸ばす）。radius が大きいと丸になる。</summary>
        static Sprite RoundedSprite(int radius)
        {
            int size = radius * 2 + 2;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[size * size];
            float c = size / 2f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float dx = Mathf.Max(0f, Mathf.Abs(px - c) - (c - radius));
                    float dy = Mathf.Max(0f, Mathf.Abs(py - c) - (c - radius));
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    byte alpha = (byte)(Mathf.Clamp01(radius - d + 0.5f) * 255f);
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
        }

        /// <summary>歯車の形（設定のボタンに使う）。文字の歯車は端末のフォントにないことがあるので描く。</summary>
        static Sprite GearSprite()
        {
            const int size = 128;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            float c = size / 2f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - c, dy = y + 0.5f - c;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float outer = Mathf.Cos(Mathf.Atan2(dy, dx) * 8f) > -0.1f ? 62f : 48f;
                    float alpha = Mathf.Clamp01(outer - r + 0.5f) * Mathf.Clamp01(r - 22f + 0.5f);
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        static Color Hex(string hex) => ColorUtility.TryParseHtmlString(hex, out var color) ? color : Color.magenta;
    }
}
