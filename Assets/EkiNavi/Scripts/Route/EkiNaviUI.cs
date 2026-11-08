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
    public class EkiNaviUI : MonoBehaviour
    {
        enum Screen { Setup, Scan, Guide, Arrived }

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

        Screen screen = Screen.Setup;
        string language = "ja";
        int stationIndex = -1;  // 入力した駅名が見つかるまでは -1
        int destinationIndex;
        Font font;
        Sprite rounded, circle;
        Transform root;
        readonly Dictionary<Screen, GameObject> screens = new Dictionary<Screen, GameObject>();
        readonly List<Action> refreshers = new List<Action>();

        // 読み取りと案内の画面で毎フレーム書き換える部品
        Text scanStatus, toastText, guideIcon, guideMain, guideSub, guideDestination;
        GameObject scanStatusPill, toast, guideCard;
        Image guideCardImage;
        RectTransform progressFill;
        Button scanButton;
        float toastUntil;
        string alignedSignName = "";

        static readonly string[] Languages = { "ja", "en", "zh", "ko" };
        static readonly string[] LanguageLabels = { "日本語", "English", "中文", "한국어" };
        // 駅の候補。読み（ひらがな・カタカナ）と英語でも見つかるようにする
        static readonly (string ja, string en, bool ready, string[] aliases)[] Stations =
        {
            ("新宿", "Shinjuku", true, new[] { "新宿", "しんじゅく", "シンジュク", "shinjuku" }),
            ("東京", "Tokyo", false, new[] { "東京", "とうきょう", "トウキョウ", "tokyo" }),
        };
        static readonly (string ja, string en, string color)[] Destinations =
        {
            ("小田急線", "Odakyu Line", "#2288CC"),
            ("京王線", "Keio Line", "#DD0077"),
            ("都営新宿線", "Toei Shinjuku Line", "#6CBB5A"),
            ("東京メトロ丸ノ内線", "Marunouchi Line", "#F62E36"),
        };

        // 文言（言語は日本語と英語。ほかの言語は英語で出す）
        static readonly Dictionary<string, (string ja, string en)> Words = new Dictionary<string, (string, string)>
        {
            ["app"] = ("駅ナビAR", "Eki Navi AR"),
            ["tagline"] = ("駅の中の乗り換えを、カメラで案内します", "Camera guidance for transfers inside the station"),
            ["language"] = ("1. 言語", "1. Language"),
            ["station"] = ("2. 駅", "2. Station"),
            ["stationPlaceholder"] = ("駅名を入力（例: 新宿）", "Enter a station (e.g. Shinjuku)"),
            ["stationNotFound"] = ("この駅はまだ案内できません", "This station is not supported yet"),
            ["stationSelected"] = ("{0}駅を選びました", "Selected {0} Station"),
            ["destination"] = ("3. 乗り換える路線", "3. Line to transfer to"),
            ["soon"] = ("準備中", "Coming soon"),
            ["start"] = ("案内を始める", "Start"),
            ["scanStep"] = ("現在地を確かめます", "Find where you are"),
            ["scanHint"] = ("近くの案内板にカメラを向けて\n「読み取る」を押してください", "Point the camera at a nearby sign\nand tap Scan"),
            ["scan"] = ("読み取る", "Scan"),
            ["scanning"] = ("読み取り中…", "Scanning..."),
            ["back"] = ("← 設定", "← Settings"),
            ["located"] = ("現在地が分かりました", "Found your location"),
            ["turnIn"] = ("{0} m 先を{1}へ", "Turn {1} in {0} m"),
            ["left"] = ("左", "left"),
            ["right"] = ("右", "right"),
            ["straight"] = ("このまま まっすぐ", "Go straight"),
            ["toGoal"] = ("{0}まで あと {1} m", "{1} m to {0}"),
            ["offRoute"] = ("経路から外れています", "You are off the route"),
            ["offRouteSub"] = ("矢印の向きに戻ってください（{0} m）", "Follow the arrow back ({0} m)"),
            ["destinationLabel"] = ("行き先: {0}", "To: {0}"),
            ["rescan"] = ("読み取り直す", "Rescan"),
            ["lost"] = ("位置を見失いました。案内板を読み取り直してください", "Lost your position. Scan a sign again"),
            ["arrived"] = ("{0}に\n着きました", "You have arrived at\n{0}"),
            ["arrivedSub"] = ("おつかれさまでした", "Have a nice trip"),
            ["restart"] = ("最初に戻る", "Back to start"),
        };

        string T(string key) => language == "ja" ? Words[key].ja : Words[key].en;
        string Goal => language == "ja" ? goalNameJa : goalNameEn;

        /// <summary>
        /// 案内板の経路デモ（SignVisionAligner と RouteArrow があるシーン）が始まったら、この画面を付ける。
        /// シーンに保存すると、Android のビルドで起動時に「level0 is corrupted」で落ちたため（2026-11-09、原因は未特定）、
        /// シーンには置かず、起動したときに付ける。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Install()
        {
            if (FindFirstObjectByType<EkiNaviUI>() != null) return;
            var routeArrow = FindFirstObjectByType<RouteArrow>();
            if (routeArrow == null || FindFirstObjectByType<SignVisionAligner>() == null) return;
            routeArrow.gameObject.AddComponent<EkiNaviUI>();
        }

        void Awake()
        {
            if (vision == null) vision = FindFirstObjectByType<SignVisionAligner>();
            if (aligner == null) aligner = FindFirstObjectByType<SignRouteAligner>();
            if (arrow == null) arrow = FindFirstObjectByType<RouteArrow>();
            font = LoadFont();
            rounded = RoundedSprite(48);
            circle = RoundedSprite(128);
            EnsureEventSystem();
            Build();
            Show(Screen.Setup);
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
            bool busy = vision != null && vision.Busy;
            scanButton.interactable = !busy;
            scanButton.GetComponentInChildren<Text>().text = busy ? T("scanning") : T("scan");
            string status = vision != null ? vision.StatusForUser : "";
            scanStatusPill.SetActive(!string.IsNullOrEmpty(status));
            scanStatus.text = status;
        }

        void UpdateGuide()
        {
            toast.SetActive(Time.unscaledTime < toastUntil);
            if (arrow == null) return;
            if (arrow.IsArrived)
            {
                Show(Screen.Arrived);
                return;
            }

            guideDestination.text = string.Format(T("destinationLabel"), Goal);

            if (!arrow.IsGuiding)
            {
                guideCardImage.color = SubInk;
                guideIcon.text = "!";
                guideMain.text = T("lost");
                guideSub.text = "";
                return;
            }
            if (arrow.IsOffRoute)
            {
                guideCardImage.color = Warning;
                guideIcon.text = "!";
                guideMain.text = T("offRoute");
                guideSub.text = string.Format(T("offRouteSub"), Mathf.RoundToInt(arrow.OffRouteMeters));
                return;
            }
            // 進み具合は、案内できている間だけ更新する（見失っている間は直前のまま）
            float total = Mathf.Max(1f, arrow.TotalMeters);
            progressFill.anchorMax = new Vector2(Mathf.Clamp01(1f - arrow.RemainingMeters / total), 1f);
            guideCardImage.color = Navy;
            if (arrow.HasNextTurn && arrow.NextTurnMeters < 60f)
            {
                guideIcon.text = arrow.NextTurnIsRight ? "→" : "←";
                guideMain.text = string.Format(T("turnIn"), Mathf.RoundToInt(arrow.NextTurnMeters), T(arrow.NextTurnIsRight ? "right" : "left"));
            }
            else
            {
                guideIcon.text = "↑";
                guideMain.text = T("straight");
            }
            guideSub.text = string.Format(T("toGoal"), Goal, Mathf.RoundToInt(arrow.RemainingMeters));
        }

        void Show(Screen next)
        {
            screen = next;
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

            screens[Screen.Setup] = BuildSetup();
            screens[Screen.Scan] = BuildScan();
            screens[Screen.Guide] = BuildGuide();
            screens[Screen.Arrived] = BuildArrived();
        }

        GameObject BuildSetup()
        {
            var page = Panel(root, "Setup", Paper, null);
            Stretch(page);

            // 上の帯
            var header = Panel(page.transform, "Header", Navy, null);
            Anchor(header, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -420), new Vector2(0, 0));
            var title = Label(header.transform, "", 84, Color.white, FontStyle.Bold, TextAnchor.LowerLeft);
            Anchor(title.gameObject, new Vector2(0, 0), new Vector2(1, 1), new Vector2(64, 150), new Vector2(-64, -80));
            var tagline = Label(header.transform, "", 36, new Color(1, 1, 1, 0.8f), FontStyle.Normal, TextAnchor.UpperLeft);
            Anchor(tagline.gameObject, new Vector2(0, 0), new Vector2(1, 0), new Vector2(64, 50), new Vector2(-64, 140));
            refreshers.Add(() => { title.text = T("app"); tagline.text = T("tagline"); });

            // 中身
            var body = new GameObject("Body", typeof(RectTransform), typeof(VerticalLayoutGroup));
            body.transform.SetParent(page.transform, false);
            Anchor(body, new Vector2(0, 0), new Vector2(1, 1), new Vector2(56, 260), new Vector2(-56, -470));
            var layout = body.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 22;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;

            SectionTitle(body.transform, "language");
            var chips = Row(body.transform, 100, 16);
            for (int i = 0; i < Languages.Length; i++)
            {
                int index = i;
                var chip = Choice(chips.transform, LanguageLabels[i], 34, () =>
                {
                    language = Languages[index];
                    Refresh();
                });
                refreshers.Add(() => Highlight(chip, language == Languages[index]));
            }

            SectionTitle(body.transform, "station");
            var field = StationField(body.transform);
            var placeholder = (Text)field.placeholder;

            // 入力の途中（「しん」「shin」など）で出す候補。押すとその駅に決まる
            var suggestions = Row(body.transform, 110, 16);
            var suggestionButtons = new List<Button>();
            for (int i = 0; i < Stations.Length; i++)
            {
                int index = i;
                var card = Choice(suggestions.transform, "", 36, () =>
                {
                    if (!Stations[index].ready) return;
                    field.SetTextWithoutNotify(language == "ja" ? Stations[index].ja : Stations[index].en);
                    stationIndex = index;
                    Refresh();
                });
                suggestionButtons.Add(card);
            }
            var stationNote = Label(body.transform, "", 30, SubInk, FontStyle.Normal, TextAnchor.MiddleLeft);
            stationNote.rectTransform.sizeDelta = new Vector2(0, 50);

            field.onValueChanged.AddListener(text =>
            {
                // 読みや英語がぴったり合えば、その場で駅を決める（準備中の駅は決めない）
                int exact = FindStation(text, exactOnly: true);
                stationIndex = exact >= 0 && Stations[exact].ready ? exact : -1;
                Refresh();
            });

            // 乗り換える路線は、駅が決まってから出す
            var destinationParts = new List<GameObject> { SectionTitle(body.transform, "destination") };
            for (int i = 0; i < Destinations.Length; i++)
            {
                int index = i;
                var item = Choice(body.transform, "", 40, () => { destinationIndex = index; Refresh(); }, 120, TextAnchor.MiddleLeft);
                var label = item.GetComponentInChildren<Text>();
                label.rectTransform.offsetMin = new Vector2(110, 16);
                var badge = Panel(item.transform, "LineColor", Hex(Destinations[i].color), circle);
                Anchor(badge, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(36, -26), new Vector2(88, 26));
                destinationParts.Add(item.gameObject);
                refreshers.Add(() =>
                {
                    label.text = language == "ja" ? Destinations[index].ja : Destinations[index].en;
                    Highlight(item, destinationIndex == index);
                });
            }

            refreshers.Add(() =>
            {
                placeholder.text = T("stationPlaceholder");
                string text = field.text;
                bool chosen = stationIndex >= 0;
                bool any = false;
                for (int i = 0; i < Stations.Length; i++)
                {
                    var st = Stations[i];
                    // 決まっていないときだけ、入力の途中に合う駅を候補として出す
                    bool show = !chosen && !string.IsNullOrWhiteSpace(text) && MatchesStation(i, text, exactOnly: false);
                    suggestionButtons[i].gameObject.SetActive(show);
                    any |= show;
                    suggestionButtons[i].interactable = st.ready;
                    suggestionButtons[i].GetComponentInChildren<Text>().text =
                        (language == "ja" ? st.ja : st.en) + (st.ready ? "" : $"<size=26>（{T("soon")}）</size>");
                    Highlight(suggestionButtons[i], false);
                }
                suggestions.SetActive(any);
                if (chosen)
                    stationNote.text = string.Format(T("stationSelected"), language == "ja" ? Stations[stationIndex].ja : Stations[stationIndex].en);
                else if (!string.IsNullOrWhiteSpace(text) && !any)
                    stationNote.text = T("stationNotFound");
                else
                    stationNote.text = "";
                stationNote.color = chosen ? Success : SubInk;
                foreach (var part in destinationParts) part.SetActive(chosen);
            });

            var start = PrimaryButton(page.transform, () => { if (stationIndex >= 0) Show(Screen.Scan); });
            Anchor(start.gameObject, new Vector2(0, 0), new Vector2(1, 0), new Vector2(56, 80), new Vector2(-56, 230));
            refreshers.Add(() =>
            {
                start.GetComponentInChildren<Text>().text = T("start");
                start.interactable = stationIndex >= 0;  // 駅が決まるまでは押せない
            });
            return page;
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

            var back = TextButton(page.transform, () => Show(Screen.Setup));
            Anchor(back.gameObject, new Vector2(0, 1), new Vector2(0, 1), new Vector2(40, -110), new Vector2(360, -20));
            refreshers.Add(() => back.GetComponentInChildren<Text>().text = T("back"));

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
            scanButton = Button(ring.transform, Navy, circle, () => { if (vision != null) vision.Scan(); });
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
            Anchor(bar, new Vector2(0, 0), new Vector2(1, 0), new Vector2(40, 60), new Vector2(-40, 300));
            guideDestination = Label(bar.transform, "", 36, Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            Anchor(guideDestination.gameObject, new Vector2(0, 0.5f), new Vector2(0.62f, 1), new Vector2(44, 0), new Vector2(0, -20));
            var track = Panel(bar.transform, "Track", Line, rounded);
            Anchor(track, new Vector2(0, 0), new Vector2(1, 0), new Vector2(44, 50), new Vector2(-44, 74));
            var fill = Panel(track.transform, "Fill", Blue, rounded);
            Stretch(fill);
            // 角の丸みを保つため、塗りの幅（右端の位置）で進み具合を出す
            progressFill = (RectTransform)fill.transform;
            progressFill.anchorMax = new Vector2(0f, 1f);
            var rescan = OutlineButton(bar.transform, () => Show(Screen.Scan));
            Anchor(rescan.gameObject, new Vector2(0.62f, 0.5f), new Vector2(1, 1), new Vector2(10, 4), new Vector2(-44, -24));
            refreshers.Add(() => rescan.GetComponentInChildren<Text>().text = T("rescan"));
            return page;
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
                Show(Screen.Setup);
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

        /// <summary>駅名を打つ欄。端末のキーボード（日本語の入力も）で打てる。</summary>
        InputField StationField(Transform parent)
        {
            var go = Panel(parent, "StationField", Color.white, rounded);
            go.GetComponent<Image>().raycastTarget = true;  // たたいてキーボードを出す
            ((RectTransform)go.transform).sizeDelta = new Vector2(0, 120);
            var outline = go.AddComponent<Outline>();
            outline.effectDistance = new Vector2(3, -3);
            outline.effectColor = Line;
            var text = Label(go.transform, "", 42, Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            text.supportRichText = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            Stretch(text.gameObject);
            text.rectTransform.offsetMin = new Vector2(40, 10);
            text.rectTransform.offsetMax = new Vector2(-40, -10);
            var placeholder = Label(go.transform, "", 38, SubInk, FontStyle.Normal, TextAnchor.MiddleLeft);
            Stretch(placeholder.gameObject);
            placeholder.rectTransform.offsetMin = new Vector2(40, 10);
            placeholder.rectTransform.offsetMax = new Vector2(-40, -10);
            var field = go.AddComponent<InputField>();
            field.textComponent = text;
            field.placeholder = placeholder;
            field.lineType = InputField.LineType.SingleLine;
            field.characterLimit = 20;
            field.caretColor = Navy;
            field.caretWidth = 3;
            field.selectionColor = new Color(0.2f, 0.38f, 0.98f, 0.3f);
            return field;
        }

        /// <summary>入力した駅名に合う駅の番号。exactOnly が false なら、打ちかけ（先頭が合う）でもよい。</summary>
        static int FindStation(string text, bool exactOnly)
        {
            for (int i = 0; i < Stations.Length; i++)
                if (MatchesStation(i, text, exactOnly)) return i;
            return -1;
        }

        static bool MatchesStation(int index, string text, bool exactOnly)
        {
            string query = NormalizeStation(text);
            if (query.Length == 0) return false;
            foreach (var alias in Stations[index].aliases)
            {
                string a = NormalizeStation(alias);
                if (a == query || (!exactOnly && a.StartsWith(query, StringComparison.Ordinal))) return true;
            }
            return false;
        }

        /// <summary>空白と「駅」「station」を除き、英字は小文字にそろえる。</summary>
        static string NormalizeStation(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = text.Trim().ToLowerInvariant().Replace(" ", "").Replace("\u3000", "");
            if (s.EndsWith("station", StringComparison.Ordinal)) s = s.Substring(0, s.Length - "station".Length);
            if (s.EndsWith("駅", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
            return s;
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

        static Color Hex(string hex) => ColorUtility.TryParseHtmlString(hex, out var color) ? color : Color.magenta;
    }
}
