using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace EkiNavi.Route
{
    /// <summary>
    /// アカウント設定の「オープンソースライセンス」の画面。アプリに入れたものの出典とライセンスを出す。
    /// - 地図データ: 政府標準利用規約で、出典の記載が条件
    /// - ARCore: 追加利用規約で、Google Play Services for AR を使っていることの表示を求められている
    /// - Material Icons: Apache 2.0 で、ライセンスの文を一緒に配ることが条件（全文を別の画面で出す）
    /// - 駅の写真: 写真の出典の画面へ
    /// 一覧はチームの「著作物利用シート」と合わせる。サーバーだけで動くもの（FastAPI など）は配らないので載せない。
    /// </summary>
    public partial class EkiNaviUI
    {
        const string ApacheLicensePath = "EkiNavi/material-icons-LICENSE";

        static readonly Dictionary<string, (string ja, string en)> LicenseWords = new Dictionary<string, (string, string)>
        {
            ["mapTitle"] = ("地図データ", "Map data"),
            ["mapBody"] = (
                "「新宿駅周辺屋内地図データ」（国土交通省）を\n加工して、新宿駅の経路を作成しています。\n政府標準利用規約（第2.0版）",
                "Routes in Shinjuku Station are based on \"Shinjuku Station Indoor Map Data\" (Ministry of Land, Infrastructure, Transport and Tourism, Japan), modified by us.\nGovernment of Japan Standard Terms of Use (Version 2.0)"),
            ["arcoreTitle"] = ("ARCore", "ARCore"),
            ["arcoreBody"] = (
                "このアプリは Google Play Services for AR\n（ARCore）を使っています。\nARCore は Google が提供し、Google の\nプライバシーポリシーが適用されます。",
                "This application runs on Google Play Services for AR (ARCore), which is provided by Google and governed by the Google Privacy Policy."),
            ["iconsTitle"] = ("Material Icons", "Material Icons"),
            ["iconsBody"] = ("Google LLC / Apache License 2.0\n押すとライセンスの全文を表示します", "Google LLC / Apache License 2.0\nTap to read the full license"),
            ["photosTitle"] = ("駅の写真", "Station photos"),
            ["photosBody"] = ("Wikimedia Commons の写真です。\n撮影者とライセンスは「写真の出典」に載せています", "Photos from Wikimedia Commons. See Photo credits for authors and licenses"),
            ["apacheTitle"] = ("Apache License 2.0", "Apache License 2.0"),
        };

        /// <summary>
        /// この画面の文言（日本語のほかは英語で出す。T と同じ決め方）。
        /// uGUI は空白で折り返すので、日本語の文の空白は折り返さない空白にして、改行は文言の \n で決める。
        /// </summary>
        string L(string key) => language == "ja" ? LicenseWords[key].ja.Replace(' ', '\u00A0') : LicenseWords[key].en;

        GameObject BuildLicenses()
        {
            var page = Panel(root, "Licenses", Color.white, null);
            Stretch(page);
            PageHeader(page.transform, () => T("licenses"), () => Show(Screen.Account));
            var content = ScrollContent(page.transform, 20);

            LicenseRow(content, "mapTitle", "mapBody", () => Application.OpenURL("https://www.geospatial.jp/ckan/dataset/mlit-indoor-shinjuku-r2"));
            LicenseRow(content, "arcoreTitle", "arcoreBody", () => Application.OpenURL("https://policies.google.com/privacy"));
            LicenseRow(content, "iconsTitle", "iconsBody", () => Show(Screen.LicenseText));
            LicenseRow(content, "photosTitle", "photosBody", () => Show(Screen.Credits));
            return page;
        }

        /// <summary>Apache License 2.0 の全文（Material Icons に付いてきたもの）。</summary>
        GameObject BuildLicenseText()
        {
            var page = Panel(root, "LicenseText", Color.white, null);
            Stretch(page);
            PageHeader(page.transform, () => L("apacheTitle"), () => Show(Screen.Licenses));
            var content = ScrollContent(page.transform, 0);
            var asset = Resources.Load<TextAsset>(ApacheLicensePath);
            if (asset == null) Debug.LogWarning($"[EkiNavi] ライセンスの文が見つかりません: Resources/{ApacheLicensePath}");
            // 英語の原文のまま出す（翻訳しない）。等幅でないので、字下げは少し崩れる
            var text = Label(content, asset != null ? asset.text.Trim() : "https://www.apache.org/licenses/LICENSE-2.0", 24, Ink, FontStyle.Normal, TextAnchor.UpperLeft);
            text.supportRichText = false;
            return page;
        }

        /// <summary>見出しの下に縦にスクロールする中身を作り、中身を並べる親を返す。</summary>
        Transform ScrollContent(Transform page, float spacing)
        {
            var scrollGo = new GameObject("Scroll", typeof(RectTransform), typeof(RectMask2D), typeof(Image), typeof(ScrollRect));
            scrollGo.transform.SetParent(page, false);
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
            layout.padding = new RectOffset(56, 56, 40, 120);
            layout.spacing = spacing;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            content.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = scrollGo.GetComponent<ScrollRect>();
            scroll.content = contentRect;
            scroll.viewport = (RectTransform)scrollGo.transform;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            return content.transform;
        }

        /// <summary>見出しと説明の行。高さは文の長さに合わせる。押すと onClick。</summary>
        void LicenseRow(Transform parent, string titleKey, string bodyKey, Action onClick)
        {
            var row = Button(parent, Paper, rounded, onClick).gameObject;
            var layout = row.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(40, 80, 28, 30);
            layout.spacing = 10;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandHeight = false;
            var title = Label(row.transform, "", 34, Ink, FontStyle.Bold, TextAnchor.UpperLeft);
            var body = Label(row.transform, "", 28, Ink, FontStyle.Normal, TextAnchor.UpperLeft);
            refreshers.Add(() =>
            {
                title.text = L(titleKey);
                body.text = L(bodyKey);
            });
            // 右の「›」は並びから外して、行の右端に置く
            var more = Label(row.transform, "›", 48, Ink, FontStyle.Bold, TextAnchor.MiddleRight);
            more.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            Anchor(more.gameObject, new Vector2(1, 0), Vector2.one, new Vector2(-80, 0), new Vector2(-30, 0));
        }
    }
}
