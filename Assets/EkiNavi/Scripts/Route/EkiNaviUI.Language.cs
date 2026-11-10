using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace EkiNavi.Route
{
    /// <summary>
    /// 初回の言語選択の画面と、中国語（簡体字）と韓国語の文言。
    /// 言語の保存がない端末を初回とみなす（アカウントは使わない）。選んだ言語は端末に残し、次からは聞かない。
    /// </summary>
    public partial class EkiNaviUI
    {
        /// <summary>初回の画面の文言。</summary>
        static readonly Dictionary<string, (string ja, string en, string zh, string ko)> FirstRunWords =
            new Dictionary<string, (string, string, string, string)>
            {
                ["title"] = ("言語を選んでください", "Choose your language", "请选择语言", "언어를 선택하세요"),
                ["hint"] = ("あとから設定で変えられます", "You can change this later in settings", "之后可在设置中更改", "나중에 설정에서 변경할 수 있습니다"),
                ["continue"] = ("はじめる", "Continue", "继续", "계속"),
            };

        // Words と同じキーで、中国語（簡体字）と韓国語を持つ。ここにないキーは英語で出す
        static readonly Dictionary<string, (string zh, string ko)> Translations = new Dictionary<string, (string, string)>
        {
            ["app"] = ("ARrow", "ARrow"),
            ["destination"] = ("换乘线路", "환승할 노선"),
            ["soon"] = ("即将推出", "준비 중"),
            ["start"] = ("开始导航", "안내 시작"),
            ["scan"] = ("扫描", "스캔"),
            ["scanning"] = ("扫描中…", "스캔 중…"),
            ["back"] = ("← 选择线路", "← 노선 선택"),
            ["pickStation"] = ("选择车站", "역 선택"),
            ["stationTitle"] = ("{0}站", "{0}역"),
            ["account"] = ("账户设置", "계정 설정"),
            ["accountSection"] = ("账户", "계정"),
            ["accountName"] = ("访客（临时账户）", "게스트(임시 계정)"),
            ["languageTitle"] = ("语言", "언어"),
            ["aboutSection"] = ("关于本应用", "이 앱에 대하여"),
            ["privacy"] = ("隐私政策", "개인정보 처리방침"),
            ["terms"] = ("使用条款", "이용약관"),
            ["licenses"] = ("开源许可", "오픈소스 라이선스"),
            ["contact"] = ("联系我们", "문의하기"),
            ["version"] = ("版本", "버전"),
            ["backToGuide"] = ("← 返回导航", "← 안내로 돌아가기"),
            ["scanHold"] = ("请保持手机不动", "움직이지 말고 그대로 계세요"),
            ["scanChecking"] = ("正在确认指示牌…", "안내판을 확인하고 있습니다…"),
            ["scanNoSign"] = ("未找到已登记的指示牌。\n请将相机对准指示牌后再试一次", "등록된 안내판을 찾을 수 없습니다.\n안내판에 카메라를 비추고 다시 눌러 주세요"),
            ["scanUnavailable"] = ("暂时无法确认指示牌，请重试", "지금은 안내판을 확인할 수 없습니다. 다시 시도해 주세요"),
            ["scanTooFar"] = ("无法测量指示牌，请靠近后重试", "안내판을 측정할 수 없습니다. 가까이 가서 다시 시도해 주세요"),
            ["scanFailed"] = ("未能读取，请重试", "읽지 못했습니다. 다시 시도해 주세요"),
            ["directionTitle"] = ("方向可能有偏差", "방향이 어긋났을 수 있습니다"),
            ["directionSub"] = ("请在 3 米以内重新扫描", "3m 이내에서 다시 스캔해 주세요"),
            ["located"] = ("已确定当前位置", "현재 위치를 찾았습니다"),
            ["turnIn"] = ("前方 {0} 米向{1}转", "{0}m 앞에서 {1}으로"),
            ["left"] = ("左", "왼쪽"),
            ["right"] = ("右", "오른쪽"),
            ["faceArrow"] = ("请转向\n箭头所指方向", "화살표 방향을\n바라봐 주세요"),
            ["straight"] = ("继续直行", "계속 직진"),
            ["offRoute"] = ("您已偏离路线", "경로에서 벗어났습니다"),
            ["upright"] = ("请将手机\n竖直拿好", "휴대폰을 세로로\n들어 주세요"),
            ["uprightSub"] = ("竖起手机后会显示箭头", "세로로 들면 화살표가 나타납니다"),
            ["offRouteSub"] = ("请按箭头方向返回（{0} 米）", "화살표 방향으로 돌아가세요 ({0}m)"),
            ["platform"] = ("站台", "승강장"),
            ["confirmTitle"] = ("确认路线", "경로 확인"),
            ["confirmRecognized"] = ("已识别为在{0}站换乘{1}", "{0}역에서 {1}(으)로 환승하는 경로로 인식했습니다"),
            ["confirmAsk"] = ("这样可以吗？", "이대로 진행할까요?"),
            ["confirmOk"] = ("确定", "확인"),
            ["confirmCancel"] = ("取消", "취소"),
            ["rescan"] = ("重新扫描", "다시 스캔"),
            ["lost"] = ("定位已丢失", "위치를 확인할 수 없습니다"),
            ["lostSub"] = ("请重新扫描指示牌", "안내판을 다시 스캔해 주세요"),
            ["arrived"] = ("已到达\n{0}", "{0}에\n도착했습니다"),
            ["arrivedSub"] = ("祝您旅途愉快", "즐거운 여행 되세요"),
            ["restart"] = ("返回首页", "처음으로"),

            ["goal"] = ("{0}换乘闸机", "{0} 환승 개찰구"),
            ["fromGates"] = ("{0}闸机内", "{0} 개찰구 안"),
            ["guideRoute"] = ("{0}：{1} → {2}", "{0}: {1} → {2}"),
            ["backStations"] = ("← 选择车站", "← 역 선택"),
            ["fromMaps"] = ("粘贴谷歌地图路线", "구글 지도 경로 붙여넣기"),
            ["fromMapsHint"] = ("也可以在谷歌地图中显示路线后，通过“分享”选择 ARrow 导入", "구글 지도에서 경로를 띄운 뒤 '공유'에서 ARrow을 선택해도 불러올 수 있습니다"),
            ["linkChecking"] = ("正在确认路线…", "경로를 확인하고 있습니다…"),
            ["linkNotMaps"] = ("未找到谷歌地图路线链接。请先复制链接", "구글 지도 경로 링크를 찾을 수 없습니다. 링크를 먼저 복사해 주세요"),
            ["linkNoStation"] = ("此路线不在支持的车站换乘", "이 경로에는 안내 가능한 역에서의 환승이 없습니다"),
            ["linkOffline"] = ("无法确认路线。请连接网络后重试", "경로를 확인할 수 없습니다. 인터넷에 연결한 뒤 다시 시도해 주세요"),
            ["scanTitle"] = ("请对准附近的指示牌", "근처 안내판을 비춰 주세요"),
            ["scanSub"] = ("对准后点击“扫描”", "카메라를 비추고 '스캔'을 누르세요"),
            ["photoCredit"] = ("照片和地图来源", "사진 및 지도 출처"),
            ["mapCreditTitle"] = ("地图数据", "지도 데이터"),
            ["mapCreditBy"] = ("根据日本国土交通省“新宿站周边室内地图数据”加工制作", "일본 국토교통성 '신주쿠역 주변 실내 지도 데이터'를 가공하여 제작"),
            ["cropped"] = ("裁剪为正方形", "정사각형으로 자름"),
            ["routeStart"] = ("起点", "출발"),
            ["routeGoal"] = ("终点", "도착"),
            ["platformDistance"] = ("距站台 {0} 米", "승강장까지 {0}m"),
        };

        // 地方、駅、路線、ゴールの名前。日本語の名前をキーにする
        static readonly Dictionary<string, (string zh, string ko)> Names = new Dictionary<string, (string, string)>
        {
            ["東京"] = ("东京", "도쿄"),
            ["新宿"] = ("新宿", "신주쿠"),
            ["池袋"] = ("池袋", "이케부쿠로"),
            ["渋谷"] = ("涩谷", "시부야"),
            ["大阪"] = ("大阪", "오사카"),
            ["京都"] = ("京都", "교토"),
            ["小田急線"] = ("小田急线", "오다큐선"),
            ["京王線"] = ("京王线", "게이오선"),
            ["都営新宿線"] = ("都营新宿线", "도에이 신주쿠선"),
            ["東京メトロ丸ノ内線"] = ("东京地铁丸之内线", "도쿄메트로 마루노우치선"),
        };

        bool UsesTranslation => language == "zh" || language == "ko";

        string Pick((string zh, string ko) text) => language == "zh" ? text.zh : text.ko;

        string T(string key)
        {
            if (language == "ja") return Words[key].ja;
            return UsesTranslation && Translations.TryGetValue(key, out var text) ? Pick(text) : Words[key].en;
        }

        /// <summary>選んでいる言語での名前。訳がなければ英語。</summary>
        string LocalName(string ja, string en)
        {
            if (language == "ja") return ja;
            return UsesTranslation && Names.TryGetValue(ja, out var name) ? Pick(name) : en;
        }

        string FirstRunText(string key)
        {
            var text = FirstRunWords[key];
            switch (language)
            {
                case "ja": return text.ja;
                case "zh": return text.zh;
                case "ko": return text.ko;
                default: return text.en;
            }
        }

        /// <summary>端末の言語に合う選択肢。対応していない言語は英語にする。</summary>
        static string DeviceLanguage()
        {
            switch (Application.systemLanguage)
            {
                case SystemLanguage.Japanese: return "ja";
                case SystemLanguage.Chinese:
                case SystemLanguage.ChineseSimplified:
                case SystemLanguage.ChineseTraditional: return "zh";
                case SystemLanguage.Korean: return "ko";
                default: return "en";
            }
        }

        // 「はじめる」を押すまでは保存しない（途中でアプリを閉じたら、次もこの画面を出す）
        GameObject BuildFirstRunLanguage()
        {
            var page = Panel(root, "Language", Paper, null);
            Stretch(page);

            var title = Label(page.transform, "", 60, Ink, FontStyle.Bold, TextAnchor.LowerCenter);
            Anchor(title.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(56, -470), new Vector2(-56, -270));
            refreshers.Add(() => title.text = FirstRunText("title"));
            var hint = Label(page.transform, "", 36, Ink, FontStyle.Normal, TextAnchor.UpperCenter);
            Anchor(hint.gameObject, new Vector2(0, 1), new Vector2(1, 1), new Vector2(56, -590), new Vector2(-56, -490));
            refreshers.Add(() => hint.text = FirstRunText("hint"));

            var body = new GameObject("Choices", typeof(RectTransform), typeof(VerticalLayoutGroup));
            body.transform.SetParent(page.transform, false);
            Anchor(body, Vector2.zero, Vector2.one, new Vector2(80, 300), new Vector2(-80, -620));
            var layout = body.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 24;
            layout.childControlHeight = false;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;

            for (int i = 0; i < Languages.Length; i++)
            {
                int index = i;
                var choice = Choice(body.transform, LanguageLabels[i], 46, () =>
                {
                    language = Languages[index];
                    Refresh();
                }, 150f);
                refreshers.Add(() => Highlight(choice, language == Languages[index]));
            }

            var confirm = PrimaryButton(page.transform, () =>
            {
                PlayerPrefs.SetString(LanguageKey, language);
                PlayerPrefs.Save();
                Show(Screen.Stations);
            });
            Anchor(confirm.gameObject, Vector2.zero, new Vector2(1, 0), new Vector2(80, 90), new Vector2(-80, 250));
            var confirmLabel = confirm.GetComponentInChildren<Text>();
            refreshers.Add(() => confirmLabel.text = FirstRunText("continue"));
            return page;
        }
    }
}
