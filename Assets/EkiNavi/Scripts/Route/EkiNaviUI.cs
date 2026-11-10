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
    /// 利用者向けの画面（デザインの試作）。
    /// Google マップの経路のリンク（共有・貼り付け、IncomingLink）を読んだときは、駅を選ぶ画面の上に
    /// 出発・乗り換え・到着の駅を並べる。乗り換えの駅を押すと、路線を選ぶ画面は飛ばして
    /// 「新宿駅：JR改札内 → 小田急線の乗り換え改札」で読み取りへ進む。
    /// 画面の流れ:
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
        enum Screen { Language, Stations, ConfirmRoute, Line, Account, Credits, Licenses, LicenseText, Scan, Guide, Arrived }

        // チームのカラーパレット（白、ライトグレー、グレー、チャコール、青）
        // Success と Warning はパレットにない、到着と経路外れの専用の色
        static readonly Color Blue = Hex("#2F80ED");
        static readonly Color Ink = Hex("#1E1E1E");
        static readonly Color Line = Hex("#8C8C8C");
        static readonly Color Paper = Hex("#F2F2F2");
        static readonly Color Success = Hex("#1F9D55");
        static readonly Color Warning = Hex("#E8590C");

        [SerializeField] SignVisionAligner vision;
        [SerializeField] SignRouteAligner aligner;
        [SerializeField] RouteArrow arrow;
        [SerializeField] IncomingLink incomingLink;

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
        RectTransform progressFill, progressKnob;
        Button scanButton;
        float toastUntil;
        RectTransform toastRect;
        Vector2 toastHome;
        float toastLift = ToastHiddenLift;  // いつもの位置から上へずらした量
        bool toastDragging;
        string alignedSignName = "";
        bool directionWarning;
        bool hasGuide;  // 一度でも位置が合って案内を始めたか（読み取りの画面から案内へ戻れる）

        // Google マップの経路のリンクから読んだもの
        string routeOriginName = "", routeDestinationName = "";  // 確認の画面に出す、経路の両端の駅名
        string fromLineId = "jr";      // 案内を始める改札の中（どの会社の改札内か）
        bool linesSkipped;             // 確認の画面から読み取りに来たか（戻る先が駅の画面になる）
        string linkMessage = "";       // リンクを読んだ結果の知らせ（駅を選ぶ画面に出す）
        bool linkMessageIsError;

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
        const float ToastSeconds = 10f;
        const float ToastHiddenLift = 150f;  // ここまで上げると案内のカードの裏に隠れる
        const float ToastDismissLift = 50f;  // 上へこれ以上スワイプしたら消す

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

        // 文言（日本語と英語。中国語と韓国語は EkiNaviUI.Language.cs）
        static readonly Dictionary<string, (string ja, string en)> Words = new Dictionary<string, (string, string)>
        {
            ["app"] = ("ARrow", "ARrow"),
            ["destination"] = ("乗り換える路線", "Select a line to transfer to"),
            ["soon"] = ("準備中", "Coming soon"),
            ["start"] = ("案内を始める", "Start"),
            ["scanSub"] = ("カメラを向けて「読み取る」を押してください", "Then tap Scan"),
            ["scanTitle"] = ("近くの案内板を映してください", "Scan a nearby sign"),
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
            ["photoCredit"] = ("写真と地図の出典", "Photo and map credits"),
            ["mapCreditTitle"] = ("地図データ", "Map data"),
            ["mapCreditBy"] = ("国土交通省「新宿駅周辺屋内地図データ」を加工して作成", "Processed from Shinjuku indoor map data (MLIT, Japan)"),
            ["cropped"] = ("正方形に切り抜き", "cropped to a square"),
            ["version"] = ("バージョン", "Version"),
            ["backToGuide"] = ("← 案内に戻る", "← Back to navigate"),
            ["scanHold"] = ("そのまま動かさないでください", "Hold still..."),
            ["scanChecking"] = ("案内板を確かめています…", "Checking the sign..."),
            ["scanNoSign"] = ("登録された案内板が見つかりません。\n案内板にカメラを向けて、もう一度押してください", "Sign not recognized\nPoint at a sign and try again"),
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
            ["routeStart"] = ("スタート", "Start"),
            ["routeGoal"] = ("ゴール", "Goal"),
            ["offRoute"] = ("経路から外れています", "You’re off route."),
            ["upright"] = ("スマホを縦に\n戻してください", "Hold your phone upright"),
            ["uprightSub"] = ("縦に戻すと矢印が出ます", "The arrow appears when the phone is upright"),
            ["offRouteSub"] = ("矢印の向きに戻ってください（{0} m）", "Follow the arrow back ({0} m)"),
            ["platform"] = ("のりば", ""),
            ["platformDistance"] = ("のりばまで {0} m", "{0} m"),
            ["goal"] = ("{0}の乗り換え改札", "the {0} transfer gates"),
            ["fromGates"] = ("{0}改札内", "Inside {0} gates"),
            ["guideRoute"] = ("{0}：{1} → {2}", "{0}: {1} → {2}"),
            ["backStations"] = ("← 駅を選ぶ", "← Select station"),
            ["fromMaps"] = ("Google マップの経路を貼り付ける", "Paste a Google Maps route"),
            ["fromMapsHint"] = ("Google マップで経路を出し、「共有」からARrow を選んでも読み込めます", "You can also share a route from Google Maps to ARrow"),
            ["linkChecking"] = ("経路を確かめています…", "Checking the route..."),
            ["linkNotMaps"] = ("Google マップの経路のリンクが見つかりません。リンクをコピーしてから押してください", "No Google Maps route link found. Copy the link first"),
            ["linkNoStation"] = ("この経路では、案内できる駅で乗り換えません", "This route has no transfer at a supported station"),
            ["linkOffline"] = ("経路を確かめられません。ネットにつないで、もう一度試してください", "Could not check the route. Connect to the internet and try again"),
            ["confirmTitle"] = ("経路の確認", "Confirm route"),
            ["confirmRecognized"] = ("{0}駅で、{1}への乗り換えとして認識しました", "Recognized as a transfer to {1} at {0} Station"),
            ["confirmAsk"] = ("これでいいですか？", "Is this correct?"),
            ["confirmOk"] = ("OK", "OK"),
            ["confirmCancel"] = ("やり直す", "Cancel"),
            ["rescan"] = ("読み取り直す", "Rescan"),
            ["lost"] = ("位置を見失いました", "Lost your position"),
            ["lostSub"] = ("案内板を読み取り直してください", "Scan a sign again"),
            ["arrived"] = ("{0}に\n着きました", "You have arrived at\n{0}"),
            ["arrivedSub"] = ("良い旅を", "Have a nice trip!"),
            ["restart"] = ("最初に戻る", "Back to start"),
        };

        string LineName(int index) => LocalName(Destinations[index].ja, Destinations[index].en);
        /// <summary>行き先。「小田急線の乗り換え改札」</summary>
        string Goal => string.Format(T("goal"), LineName(destinationIndex));
        string FromName
        {
            get
            {
                int i = Array.FindIndex(Destinations, d => LineId(d.code) == fromLineId);
                return i >= 0 ? LineName(i) : "JR";
            }
        }
        /// <summary>「新宿駅：JR改札内 → 小田急線の乗り換え改札」</summary>
        string GuideRoute => stationIndex < 0 ? "" : string.Format(T("guideRoute"),
            string.Format(T("stationTitle"), LocalName(Stations[stationIndex].ja, Stations[stationIndex].en)),
            string.Format(T("fromGates"), FromName), Goal);

        // 路線の記号 → MapsRouteLink の路線の id
        static string LineId(string code) => code switch { "OH" => "odakyu", "KO" => "keio", "S" => "toei_shinjuku", "M" => "marunouchi", _ => null };

        void Awake()
        {
            if (vision == null) vision = FindFirstObjectByType<SignVisionAligner>();
            if (aligner == null) aligner = FindFirstObjectByType<SignRouteAligner>();
            if (arrow == null) arrow = FindFirstObjectByType<RouteArrow>();
            if (incomingLink == null && !TryGetComponent(out incomingLink)) incomingLink = gameObject.AddComponent<IncomingLink>();
            font = LoadFont();
            rounded = RoundedSprite(48);
            circle = RoundedSprite(128);
            gear = GearSprite();
            barCorner = RoundedSprite(12);  // バーの高さ 24 の半分。端が半円になる
            // 言語の保存がなければ初回とみなし、端末の言語を選んだ状態で言語の画面を出す
            bool firstRun = !PlayerPrefs.HasKey(LanguageKey);
            string savedLanguage = firstRun ? DeviceLanguage() : PlayerPrefs.GetString(LanguageKey, "ja");
            if (Languages.Contains(savedLanguage)) language = savedLanguage;
            EnsureEventSystem();
            Build();
            Show(firstRun ? Screen.Language : Screen.Stations);
        }

        void OnEnable()
        {
            if (vision != null) vision.Aligned += OnAligned;
            if (incomingLink != null) incomingLink.Handled += OnLink;
        }

        void OnDisable()
        {
            if (vision != null) vision.Aligned -= OnAligned;
            if (incomingLink != null) incomingLink.Handled -= OnLink;
        }

        /// <summary>Google マップの経路のリンクを読み終えたとき。駅と路線が決まれば確認の画面を出し、決まらなければ駅を選ぶ画面に戻す。</summary>
        void OnLink(IncomingLink.Outcome outcome, MapsRouteLink.Result result)
        {
            linkMessageIsError = outcome != IncomingLink.Outcome.Resolved;
            switch (outcome)
            {
                case IncomingLink.Outcome.Resolved:
                case IncomingLink.Outcome.NoStation:
                    routeOriginName = result.OriginName ?? "";
                    routeDestinationName = result.DestinationName ?? "";
                    int line = Array.FindIndex(Destinations, d => LineId(d.code) == result.LineId);
                    if (line >= 0) destinationIndex = line;
                    fromLineId = result.FromLineId ?? "jr";
                    linkMessage = outcome == IncomingLink.Outcome.NoStation ? T("linkNoStation") : "";
                    if (outcome == IncomingLink.Outcome.Resolved && line < 0)
                    {
                        linkMessage = T("linkNoStation");
                        linkMessageIsError = true;
                    }
                    if (outcome == IncomingLink.Outcome.Resolved && line >= 0)
                    {
                        stationIndex = Array.FindIndex(Stations, st => st.ready);
                        linesSkipped = true;
                        Show(Screen.ConfirmRoute);
                        return;
                    }
                    break;
                case IncomingLink.Outcome.Offline: linkMessage = T("linkOffline"); break;
                default: linkMessage = T("linkNotMaps"); break;
            }
            // 路線の画面で貼ったときは、選ばれた路線を見せるためにその画面のままにする
            if (screen == Screen.Line) Refresh();
            else Show(Screen.Stations);
        }

        /// <summary>確認の画面でやり直すとき。読んだ経路を消して駅を選ぶ画面に戻す。</summary>
        void CancelRoute()
        {
            routeOriginName = routeDestinationName = "";
            fromLineId = "jr";
            linesSkipped = false;
            Show(Screen.Stations);
        }

        /// <summary>「新大久保駅」「Hatsudai Station」→「新大久保」「Hatsudai」。</summary>
        static string StopName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            string s = name.Trim();
            if (s.EndsWith(" Station", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 8);
            if (s.EndsWith("駅")) s = s.Substring(0, s.Length - 1);
            return s;
        }

        void OnAligned(SignEntry sign)
        {
            alignedSignName = sign != null ? sign.name : "";
            if (arrow != null) arrow.ResetArrival();
            hasGuide = true;
            directionWarning = vision != null && vision.DirectionUncertain;
            toastUntil = Time.unscaledTime + ToastSeconds;
            toastLift = ToastHiddenLift;
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
                case Screen.Stations:
                case Screen.Line:
                    if (incomingLink != null && incomingLink.Busy && linkMessage != T("linkChecking"))
                    {
                        linkMessage = T("linkChecking");
                        linkMessageIsError = false;
                        Refresh();
                    }
                    break;
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
            UpdateToast(!overlay && Time.unscaledTime < toastUntil);
            if (overlay)
            {
                overlayTitle.text = T(directionWarning ? "directionTitle" : "lost");
                overlaySub.text = T(directionWarning ? "directionSub" : "lostSub");
                return;
            }

            // 残りの距離は下の帯に出す（位置が確かなときだけ）
            guideDestination.text = guide.Guiding
                ? string.Format(T("platformDistance"), Mathf.RoundToInt(guide.RemainingMeters))
                : T("platform");
            guideSub.gameObject.SetActive(true);

            if (guide.WaitingForPortrait)
            {
                guideCardImage.color = Blue;
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
            float progress = Mathf.Clamp01(1f - guide.RemainingMeters / total);
            progressFill.anchorMax = new Vector2(progress, 1f);
            progressKnob.anchorMin = progressKnob.anchorMax = new Vector2(progress, 0.5f);
            guideCardImage.color = Blue;
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
            guideSub.gameObject.SetActive(false);  // 大きい文字だけをカードの中央に置く
        }

        // 案内のカードの裏から下りてきて、時間が来たら戻る。指で動かしている間は指に合わせる
        void UpdateToast(bool wanted)
        {
            if (!toastDragging)
            {
                float target = wanted ? 0f : ToastHiddenLift;
                toastLift = Mathf.Lerp(toastLift, target, 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime));
                if (Mathf.Abs(toastLift - target) < 0.5f) toastLift = target;
            }
            toast.SetActive(wanted || toastDragging || toastLift < ToastHiddenLift);
            toastRect.anchoredPosition = toastHome + Vector2.up * toastLift;
        }

        void AddToastSwipe()
        {
            toast.GetComponent<Image>().raycastTarget = true;
            var trigger = toast.AddComponent<EventTrigger>();
            void On(EventTriggerType type, Action<PointerEventData> action)
            {
                var entry = new EventTrigger.Entry { eventID = type };
                entry.callback.AddListener(data => action((PointerEventData)data));
                trigger.triggers.Add(entry);
            }
            On(EventTriggerType.BeginDrag, _ => toastDragging = true);
            On(EventTriggerType.Drag, data =>
            {
                float scale = root.GetComponent<Canvas>().scaleFactor;
                toastLift = Mathf.Clamp(toastLift + data.delta.y / scale, 0f, ToastHiddenLift);
            });
            On(EventTriggerType.EndDrag, _ =>
            {
                toastDragging = false;
                if (toastLift > ToastDismissLift) toastUntil = 0f;
            });
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
            if (next == Screen.Line) linkMessage = "";
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

            screens[Screen.Language] = BuildFirstRunLanguage();
            screens[Screen.Stations] = BuildStations();
            screens[Screen.ConfirmRoute] = BuildConfirmRoute();
            screens[Screen.Line] = BuildLine();
            screens[Screen.Account] = BuildAccount();
            screens[Screen.Credits] = BuildCredits();
            screens[Screen.Licenses] = BuildLicenses();
            screens[Screen.LicenseText] = BuildLicenseText();
            screens[Screen.Scan] = BuildScan();
            screens[Screen.Guide] = BuildGuide();
            screens[Screen.Arrived] = BuildArrived();
        }

        // 画面の上の帯。onBack があれば左に戻るボタンを出す
        void PageHeader(Transform page, Func<string> title, Action onBack)
        {
            var header = Panel(page, "Header", Blue, null);
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
                var gridGo = StationGrid(content.transform);
                foreach (var _ in region.stations) StationTile(gridGo.transform, stationNumber++);
            }

            // Google マップから共有されたリンクを読んだ結果の知らせ（貼り付けは路線の画面で行う）
            var hint = Label(content.transform, "", 30, Ink, FontStyle.Bold, TextAnchor.UpperLeft);
            refreshers.Add(() =>
            {
                hint.text = linkMessage;
                hint.color = linkMessageIsError ? Warning : Ink;
            });

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

        /// <summary>駅の四角を 4 列に並べる枠（幅は FitStationGrids で画面に合わせる）。</summary>
        GameObject StationGrid(Transform parent)
        {
            var gridGo = new GameObject("Grid", typeof(RectTransform), typeof(GridLayoutGroup), typeof(LayoutElement));
            gridGo.transform.SetParent(parent, false);
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
            return gridGo;
        }

        GameObject RegionTitle(Transform parent, string ja, string en)
        {
            var go = new GameObject("Region", typeof(RectTransform), typeof(LayoutElement));
            go.transform.SetParent(parent, false);
            go.GetComponent<LayoutElement>().preferredHeight = 90;
            // 日本語以外では英語の名前だけを出す
            var left = Label(go.transform, ja, 46, Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            Stretch(left.gameObject);
            var right = Label(go.transform, en, 32, Ink, FontStyle.Bold, TextAnchor.MiddleRight);
            Stretch(right.gameObject);
            refreshers.Add(() =>
            {
                left.text = language == "ja" ? ja : en;
                right.text = language == "ja" ? en : "";
            });
            var line = Panel(go.transform, "Line", Line, null);
            Anchor(line, Vector2.zero, new Vector2(1, 0), Vector2.zero, new Vector2(0, 3));
            return go;
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
                fromLineId = "jr";
                Show(Screen.Line);
            });

            var photo = Panel(go.transform, "Photo", station.ready ? Blue : Paper, rounded);
            Anchor(photo, Vector2.zero, Vector2.one, new Vector2(0, StationLabelHeight), Vector2.zero);
            var picture = LoadIcon("station_" + station.en.ToLowerInvariant());
            if (picture != null)
            {
                // 写真の角をタイルの丸みに合わせて切り取る
                photo.AddComponent<Mask>().showMaskGraphic = true;
                var image = Panel(photo.transform, "Picture", Color.white, picture);
                image.GetComponent<Image>().type = Image.Type.Simple;
                Stretch(image);
            }
            if (!station.ready)
            {
                // 準備中の駅は、写真を暗くして文字を重ねる
                if (picture != null) Stretch(Panel(photo.transform, "Dim", new Color(0, 0, 0, 0.5f), null));
                var soon = Label(photo.transform, "", 32, picture != null ? Color.white : Ink, FontStyle.Bold, TextAnchor.MiddleCenter);
                Stretch(soon.gameObject);
                refreshers.Add(() => soon.text = T("soon"));
            }
            else if (picture == null)
            {
                var initial = Label(photo.transform, station.ja.Substring(0, 1), 96, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
                Stretch(initial.gameObject);
            }

            var ja = Label(go.transform, station.ja, 36, Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            Anchor(ja.gameObject, Vector2.zero, new Vector2(1, 0), new Vector2(6, 50), new Vector2(-6, StationLabelHeight - 4));
            var en = Label(go.transform, station.en, 28, Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            Anchor(en.gameObject, Vector2.zero, new Vector2(1, 0), new Vector2(6, 8), new Vector2(-6, 50));
            refreshers.Add(() =>
            {
                ja.text = language == "ja" ? station.ja : station.en;
                en.text = language == "ja" ? station.en : "";
            });
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

        /// <summary>Google マップの経路を、どの駅でどの路線に乗り換えると読んだかを見せて、確かめてもらう画面。</summary>
        GameObject BuildConfirmRoute()
        {
            var page = Panel(root, "ConfirmRoute", Color.white, null);
            Stretch(page);
            PageHeader(page.transform, () => T("confirmTitle"), CancelRoute);

            var body = new GameObject("Body", typeof(RectTransform), typeof(VerticalLayoutGroup));
            body.transform.SetParent(page.transform, false);
            Anchor(body, Vector2.zero, Vector2.one, new Vector2(56, 420), new Vector2(-56, -HeaderHeight - 60));
            var layout = body.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 26;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;

            // 駅を選ぶ画面と同じ写真を、同じ丸い角で出す
            var picture = LoadIcon("station_" + Stations.First(st => st.ready).en.ToLowerInvariant());
            if (picture != null)
            {
                var holder = new GameObject("PhotoRow", typeof(RectTransform));
                holder.transform.SetParent(body.transform, false);
                ((RectTransform)holder.transform).sizeDelta = new Vector2(0, 420);
                var photo = Panel(holder.transform, "Photo", Blue, rounded);
                var center = new Vector2(0.5f, 0.5f);
                Anchor(photo, center, center, new Vector2(-210, -210), new Vector2(210, 210));
                photo.AddComponent<Mask>().showMaskGraphic = true;
                var image = Panel(photo.transform, "Picture", Color.white, picture);
                image.GetComponent<Image>().type = Image.Type.Simple;
                Stretch(image);
            }

            Text AddLine(int size, Color color, float height)
            {
                var label = Label(body.transform, "", size, color, FontStyle.Bold, TextAnchor.MiddleLeft);
                label.rectTransform.sizeDelta = new Vector2(0, height);
                return label;
            }
            var station = AddLine(80, Ink, 120);
            var transfer = AddLine(46, Blue, 80);
            var ends = AddLine(34, Ink, 60);
            var recognized = AddLine(42, Ink, 160);
            var ask = AddLine(46, Ink, 90);

            var ok = PrimaryButton(page.transform, () => Show(Screen.Scan));
            Anchor(ok.gameObject, Vector2.zero, new Vector2(1, 0), new Vector2(56, 80), new Vector2(-56, 230));
            var cancel = OutlineButton(page.transform, CancelRoute);
            Anchor(cancel.gameObject, Vector2.zero, new Vector2(1, 0), new Vector2(56, 260), new Vector2(-56, 390));

            refreshers.Add(() =>
            {
                ok.GetComponentInChildren<Text>().text = T("confirmOk");
                cancel.GetComponentInChildren<Text>().text = T("confirmCancel");
                ask.text = T("confirmAsk");
                ends.text = string.IsNullOrEmpty(routeOriginName)
                    ? StopName(routeDestinationName)
                    : $"{StopName(routeOriginName)} → {StopName(routeDestinationName)}";
                if (stationIndex < 0) return;
                string stationName = LocalName(Stations[stationIndex].ja, Stations[stationIndex].en);
                station.text = string.Format(T("stationTitle"), stationName);
                transfer.text = $"{FromName} → {LineName(destinationIndex)}";
                recognized.text = string.Format(T("confirmRecognized"), stationName, LineName(destinationIndex));
            });
            return page;
        }

        GameObject BuildLine()
        {
            var page = Panel(root, "Line", Color.white, null);
            Stretch(page);
            PageHeader(page.transform, () => stationIndex >= 0
                ? string.Format(T("stationTitle"), LocalName(Stations[stationIndex].ja, Stations[stationIndex].en))
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
                var badge = LineBadge(item.transform, () => index, 36, 72);
                Anchor(badge, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(30, -36), new Vector2(-30, 36));
                refreshers.Add(() => Highlight(item, destinationIndex == index));
            }

            // Google マップの経路のリンクで、行き先の路線を選ぶ
            var paste = OutlineButton(body.transform, () => { if (incomingLink != null) incomingLink.PasteFromClipboard(); });
            ((RectTransform)paste.transform).sizeDelta = new Vector2(0, 110);
            var hint = Label(body.transform, "", 30, Ink, FontStyle.Bold, TextAnchor.UpperLeft);
            ((RectTransform)hint.transform).sizeDelta = new Vector2(0, 120);
            refreshers.Add(() =>
            {
                paste.GetComponentInChildren<Text>().text = T("fromMaps");
                bool hasMessage = !string.IsNullOrEmpty(linkMessage);
                hint.text = hasMessage ? linkMessage : T("fromMapsHint");
                hint.color = hasMessage && linkMessageIsError ? Warning : Ink;
            });

            var start = PrimaryButton(page.transform, () => { linesSkipped = false; Show(Screen.Scan); });
            Anchor(start.gameObject, Vector2.zero, new Vector2(1, 0), new Vector2(56, 80), new Vector2(-56, 230));
            refreshers.Add(() => start.GetComponentInChildren<Text>().text = T("start"));
            return page;
        }

        GameObject BuildAccount()
        {
            var page = Panel(root, "Account", Color.white, null);
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
            foreach (var key in new[] { "privacy", "terms", "contact" })
                InfoRow(body.transform, key, () => T("soon"));
            MenuRow(body.transform, "licenses", () => Show(Screen.Licenses));
            InfoRow(body.transform, "version", () => Application.version);
            MenuRow(body.transform, "photoCredit", () => Show(Screen.Credits));
            return page;
        }

        // 駅の写真の出典（Wikimedia Commons）。CC BY の写真は、撮影者、ライセンス、元のページの表示が条件
        static readonly (string ja, string en, string author, string license, string url)[] PhotoCredits =
        {
            ("新宿", "Shinjuku", "Real Estate Japan", "CC BY 2.0", "https://commons.wikimedia.org/wiki/File:Shinjuku_Station_evening_2020-10-15.jpg"),
            ("東京", "Tokyo", "Daderot", "CC0", "https://commons.wikimedia.org/wiki/File:Exterior_-_Tokyo_Station_Marunouchi_Building_-_DSC09896.JPG"),
            ("池袋", "Ikebukuro", "Asanagi", "CC0", "https://commons.wikimedia.org/wiki/File:East_entrance_of_Ikebukuro_Station_2026-10-08.jpg"),
            ("渋谷", "Shibuya", "Syced", "CC0", "https://commons.wikimedia.org/wiki/File:Shibuya_Scramble_Square_2.jpg"),
            ("大阪", "Osaka", "MIKI Yoshihito", "CC BY 2.0", "https://commons.wikimedia.org/wiki/File:Osaka_Station_south_gate_building_2017-04-01.jpg"),
            ("京都", "Kyoto", "Corpse Reviver", "CC BY 3.0", "https://commons.wikimedia.org/wiki/File:Kyoto_Station_J09.jpg"),
        };

        const string MapDataUrl = "https://www.geospatial.jp/ckan/dataset/mlit-indoor-shinjuku-r2";

        void MenuRow(Transform parent, string key, Action onClick)
        {
            var row = Button(parent, Paper, rounded, onClick);
            ((RectTransform)row.transform).sizeDelta = new Vector2(0, 108);
            var left = Label(row.transform, "", 38, Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            Anchor(left.gameObject, Vector2.zero, Vector2.one, new Vector2(40, 0), new Vector2(-100, 0));
            refreshers.Add(() => left.text = T(key));
            var more = Label(row.transform, "›", 48, Ink, FontStyle.Bold, TextAnchor.MiddleRight);
            Anchor(more.gameObject, new Vector2(1, 0), Vector2.one, new Vector2(-100, 0), new Vector2(-40, 0));
        }

        GameObject BuildCredits()
        {
            var page = Panel(root, "Credits", Color.white, null);
            Stretch(page);
            PageHeader(page.transform, () => T("photoCredit"), () => Show(Screen.Account));
            var body = new GameObject("Body", typeof(RectTransform), typeof(VerticalLayoutGroup));
            body.transform.SetParent(page.transform, false);
            Anchor(body, Vector2.zero, Vector2.one, new Vector2(56, 40), new Vector2(-56, -HeaderHeight - 40));
            var layout = body.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 14;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            foreach (var credit in PhotoCredits)
            {
                var c = credit;
                CreditRow(body.transform, () => string.Format(T("stationTitle"), LocalName(c.ja, c.en)),
                    () => $"{c.author} / {c.license} / {T("cropped")}", c.url);
            }
            // 経路に使う国土交通省の地図データは、出典の表示が利用の条件
            CreditRow(body.transform, () => T("mapCreditTitle"), () => T("mapCreditBy"), MapDataUrl);
            return page;
        }

        // 押すと元のページを開く
        void CreditRow(Transform parent, Func<string> titleText, Func<string> byText, string url)
        {
            var row = Button(parent, Paper, rounded, () => Application.OpenURL(url));
            ((RectTransform)row.transform).sizeDelta = new Vector2(0, 128);
            var title = Label(row.transform, "", 34, Ink, FontStyle.Bold, TextAnchor.LowerLeft);
            Anchor(title.gameObject, new Vector2(0, 0.5f), Vector2.one, new Vector2(40, 4), new Vector2(-40, 0));
            var by = Label(row.transform, "", 28, Ink, FontStyle.Bold, TextAnchor.UpperLeft);
            Anchor(by.gameObject, Vector2.zero, new Vector2(1, 0.5f), new Vector2(40, 0), new Vector2(-40, -6));
            refreshers.Add(() =>
            {
                title.text = titleText();
                by.text = byText();
            });
        }

        void InfoRow(Transform parent, string key, Func<string> value)
        {
            var row = Panel(parent, "Row", Paper, rounded);
            ((RectTransform)row.transform).sizeDelta = new Vector2(0, 108);
            var left = Label(row.transform, "", 38, Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            Anchor(left.gameObject, Vector2.zero, Vector2.one, new Vector2(40, 0), new Vector2(-220, 0));
            refreshers.Add(() => left.text = T(key));
            if (value == null) return;
            var right = Label(row.transform, "", 32, Ink, FontStyle.Bold, TextAnchor.MiddleRight);
            Anchor(right.gameObject, new Vector2(1, 0), Vector2.one, new Vector2(-220, 0), new Vector2(-40, 0));
            refreshers.Add(() => right.text = value());
        }

        /// <summary>
        /// 文字の実際の形の上下の中心を、枠の中心に合わせる。
        /// フォントの行の高さで中央に置くと、文字によって少し下にずれて見えるため。
        /// </summary>
        static void CenterGlyphs(Text label)
        {
            if (string.IsNullOrEmpty(label.text)) return;
            var generator = new TextGenerator();
            generator.Populate(label.text, label.GetGenerationSettings(new Vector2(4000f, 400f)));
            var verts = generator.verts;
            if (verts.Count == 0) return;
            float min = float.MaxValue, max = float.MinValue;
            foreach (var v in verts)
            {
                min = Mathf.Min(min, v.position.y);
                max = Mathf.Max(max, v.position.y);
            }
            label.rectTransform.anchoredPosition = new Vector2(0f, -(min + max) * 0.5f / label.pixelsPerUnit);
        }

        /// <summary>路線の記号を丸で囲んだものと、路線の色で囲んだ路線名を横に並べる。</summary>
        GameObject LineBadge(Transform parent, Func<int> index, int fontSize, float height)
        {
            var color = Hex(Destinations[index()].color);
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
            var ringImage = ring.GetComponent<Image>();
            var ringSize = ring.AddComponent<LayoutElement>();
            ringSize.preferredWidth = height;
            ringSize.preferredHeight = height;
            var inner = Panel(ring.transform, "Inner", Color.white, circle);
            Stretch(inner, height * 0.1f);
            var code = Label(inner.transform, "", Mathf.RoundToInt(height * 0.4f), color, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(code.gameObject);

            var pill = Panel(row.transform, "Name", color, barCorner);
            var pillImage = pill.GetComponent<Image>();
            var pillSize = pill.AddComponent<LayoutElement>();
            pillSize.preferredHeight = height;
            var name = Label(pill.transform, "", fontSize, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
            name.horizontalOverflow = HorizontalWrapMode.Overflow;
            Stretch(name.gameObject);
            refreshers.Add(() =>
            {
                var line = Destinations[index()];
                var lineColor = Hex(line.color);
                ringImage.color = lineColor;
                code.text = line.code;
                code.color = lineColor;
                pillImage.color = lineColor;
                name.text = LocalName(line.ja, line.en);
                pillSize.preferredWidth = name.preferredWidth + 44f;
                CenterGlyphs(code);
                CenterGlyphs(name);
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
            Anchor(card, new Vector2(0, 1), new Vector2(1, 1), new Vector2(40, -400), new Vector2(-40, -110));
            var scanTexts = TextStack(card.transform, 56);
            var title = Label(scanTexts, "", 54, Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            var sub = Label(scanTexts, "", 34, Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            refreshers.Add(() => { title.text = T("scanTitle"); sub.text = linesSkipped ? GuideRoute : T("scanSub"); });

            var back = TextButton(page.transform, () => Show(linesSkipped ? Screen.Stations : Screen.Line));
            Anchor(back.gameObject, new Vector2(0, 1), new Vector2(0, 1), new Vector2(40, -110), new Vector2(400, -20));
            refreshers.Add(() => back.GetComponentInChildren<Text>().text = T(linesSkipped ? "backStations" : "back"));

            // 読み取りに失敗しても、案内の途中なら案内へ戻れるようにする
            var backToGuide = TextButton(page.transform, () => Show(Screen.Guide));
            Anchor(backToGuide.gameObject, new Vector2(1, 1), new Vector2(1, 1), new Vector2(-480, -110), new Vector2(-40, -20));
            backToGuide.GetComponentInChildren<Text>().alignment = TextAnchor.MiddleRight;
            refreshers.Add(() =>
            {
                backToGuide.gameObject.SetActive(hasGuide);
                backToGuide.GetComponentInChildren<Text>().text = T("backToGuide");
            });

            // 結果の知らせ
            scanStatusPill = Panel(page.transform, "Status", new Color(0.118f, 0.118f, 0.118f, 0.82f), rounded);
            Anchor(scanStatusPill, new Vector2(0, 0), new Vector2(1, 0), new Vector2(60, 400), new Vector2(-60, 520));
            scanStatus = Label(scanStatusPill.transform, "", 32, Color.white, FontStyle.Normal, TextAnchor.MiddleCenter);
            Stretch(scanStatus.gameObject, 24);

            // 読み取るボタン（丸）
            var ring = Panel(page.transform, "Ring", Color.white, circle);
            Anchor(ring, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(-130, 110), new Vector2(130, 370));
            scanButton = Button(ring.transform, Blue, circle, () => { if (vision != null) vision.Scan(); else PreviewScan(); });
            Stretch(scanButton.gameObject, 14);
            var scanLabel = Label(scanButton.transform, "", 40, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(scanLabel.gameObject);
            return page;
        }

        /// <summary>カードの中で、大きい文字と小さい文字のかたまりを上下の中央に置く入れ物。</summary>
        Transform TextStack(Transform card, float left)
        {
            var go = new GameObject("Texts", typeof(RectTransform), typeof(VerticalLayoutGroup));
            go.transform.SetParent(card, false);
            Anchor(go, Vector2.zero, Vector2.one, new Vector2(left, 0), new Vector2(-40, 0));
            var layout = go.GetComponent<VerticalLayoutGroup>();
            layout.childAlignment = TextAnchor.MiddleLeft;
            layout.spacing = 30;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return go.transform;
        }

        GameObject BuildGuide()
        {
            var page = new GameObject("Guide", typeof(RectTransform));
            page.transform.SetParent(root, false);
            Stretch(page);

            guideCard = Panel(page.transform, "Instruction", Blue, rounded);
            guideCardImage = guideCard.GetComponent<Image>();
            Anchor(guideCard, new Vector2(0, 1), new Vector2(1, 1), new Vector2(40, -400), new Vector2(-40, -110));
            guideIcon = Label(guideCard.transform, "↑", 130, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
            Anchor(guideIcon.gameObject, new Vector2(0, 0), new Vector2(0, 1), new Vector2(20, 0), new Vector2(220, 0));
            var guideTexts = TextStack(guideCard.transform, 230);
            guideMain = Label(guideTexts, "", 54, Color.white, FontStyle.Bold, TextAnchor.MiddleLeft);
            guideSub = Label(guideTexts, "", 34, Color.white, FontStyle.Normal, TextAnchor.MiddleLeft);

            toast = Panel(page.transform, "Located", Success, rounded);
            Anchor(toast, new Vector2(0, 1), new Vector2(1, 1), new Vector2(80, -560), new Vector2(-80, -430));
            toastText = Label(toast.transform, "", 34, Color.white, FontStyle.Bold, TextAnchor.MiddleCenter);
            Stretch(toastText.gameObject, 20);
            toastText.lineSpacing = 1.2f;
            toast.transform.SetAsFirstSibling();  // 案内のカードの裏から出す
            toastRect = (RectTransform)toast.transform;
            toastHome = toastRect.anchoredPosition;
            AddToastSwipe();
            refreshers.Add(() => toastText.text = T("located") + (string.IsNullOrEmpty(alignedSignName) ? "" : $"\n<size=28>{alignedSignName}</size>"));

            // 下の帯: 行き先、進み具合、読み取り直す
            var bar = Panel(page.transform, "Bottom", new Color(1, 1, 1, 0.95f), rounded);
            guideBar = bar;
            Anchor(bar, new Vector2(0, 0), new Vector2(1, 0), new Vector2(40, 60), new Vector2(-40, 300));
            // 選んだ路線の札（経路そのものは、まだシーンの経路のまま）
            var destination = LineBadge(bar.transform, () => destinationIndex, 30, 58);
            Anchor(destination, new Vector2(0, 0.5f), new Vector2(0.68f, 1), new Vector2(44, 0), new Vector2(0, -20));
            guideDestination = Label(destination.transform, "", 30, Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            // ゴールの路線だと分かるよう、記号の左に旗を出す（Material Icons の flag）
            var flagSprite = LoadIcon("icon_flag");
            if (flagSprite != null)
            {
                var flag = Panel(destination.transform, "GoalFlag", Hex("#D93025"), flagSprite);
                var image = flag.GetComponent<Image>();
                image.type = Image.Type.Simple;
                image.preserveAspect = true;
                var size = flag.AddComponent<LayoutElement>();
                size.preferredWidth = 50;
                size.preferredHeight = 58;
                flag.transform.SetAsFirstSibling();
            }
            var track = Panel(bar.transform, "Track", Paper, barCorner);
            Anchor(track, new Vector2(0, 0), new Vector2(1, 0), new Vector2(44, 72), new Vector2(-44, 96));
            var fill = Panel(track.transform, "Fill", Blue, barCorner);
            Stretch(fill);
            // 角の丸みを保つため、塗りの幅（右端の位置）で進み具合を出す
            progressFill = (RectTransform)fill.transform;
            progressFill.anchorMax = new Vector2(0f, 1f);

            // 音楽アプリの再生バーのように、現在地にバーと同じ色の丸を出す
            var knob = Panel(track.transform, "Knob", Blue, circle);
            Anchor(knob, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(-24, -24), new Vector2(24, 24));
            progressKnob = (RectTransform)knob.transform;

            // バーの両端の文字
            var startLabel = Label(bar.transform, "", 28, Ink, FontStyle.Bold, TextAnchor.MiddleLeft);
            Anchor(startLabel.gameObject, Vector2.zero, new Vector2(0.5f, 0), new Vector2(44, 14), new Vector2(0, 56));
            var goalLabel = Label(bar.transform, "", 28, Ink, FontStyle.Bold, TextAnchor.MiddleRight);
            Anchor(goalLabel.gameObject, new Vector2(0.5f, 0), new Vector2(1, 0), new Vector2(0, 14), new Vector2(-44, 56));
            refreshers.Add(() => { startLabel.text = T("routeStart"); goalLabel.text = T("routeGoal"); });
            var rescan = OutlineButton(bar.transform, () => Show(Screen.Scan));
            Anchor(rescan.gameObject, new Vector2(0.68f, 0.5f), new Vector2(1, 1), new Vector2(10, 4), new Vector2(-44, -24));
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
            overlaySub = Label(overlay.transform, "", 36, Color.white, FontStyle.Normal, TextAnchor.UpperCenter);
            Anchor(overlaySub.gameObject, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(50, -50), new Vector2(-50, 40));
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
            var sub = Label(page.transform, "", 38, Color.white, FontStyle.Normal, TextAnchor.UpperCenter);
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
            var label = Label(parent, "", 34, Ink, FontStyle.Bold, TextAnchor.LowerLeft);
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
            button.GetComponent<Image>().color = on ? Paper : Color.white;
            var outline = button.GetComponent<Outline>();
            if (outline != null) outline.effectColor = on ? Blue : Line;
            var label = button.GetComponentInChildren<Text>();
            if (label != null) label.color = on ? Blue : Ink;
        }

        Button PrimaryButton(Transform parent, Action onClick)
        {
            var button = Button(parent, Blue, rounded, onClick);
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

        // 旗とチェックは Material Icons（Apache 2.0、Assets/EkiNavi/Resources/EkiNavi/material-icons-LICENSE.txt。全文はライセンスの画面で出す）
        static Sprite LoadIcon(string name)
        {
            var texture = Resources.Load<Texture2D>("EkiNavi/" + name);
            if (texture == null)
            {
                Debug.LogWarning($"[EkiNavi] アイコンがありません: {name}");
                return null;
            }
            return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
        }

        // チェックは Material Icons の check（Apache 2.0、Assets/EkiNavi/Resources/EkiNavi/material-icons-LICENSE.txt）
        void Check(Transform badge)
        {
            var sprite = LoadIcon("icon_check");
            if (sprite == null) return;
            var check = Panel(badge, "Check", Success, sprite);
            check.GetComponent<Image>().type = Image.Type.Simple;
            Stretch(check, 30);
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
                // 中国語と韓国語の文字も出せるよう、それぞれのフォントも候補に入れる
                string[] preferred = { "Noto Sans CJK JP", "Noto Sans JP", "Hiragino Sans", "Yu Gothic UI", "Meiryo UI", "Meiryo", "PingFang SC", "Hiragino Sans GB", "Apple SD Gothic Neo", "Malgun Gothic" };
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
