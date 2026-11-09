using System.IO;
using System.Linq;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

namespace EkiNavi.EditorTools
{
    /// <summary>
    /// Android で、Google マップの「共有」から駅ナビAR を選べるようにする（IncomingLink が受け取る）。
    /// ビルドのたびに作られる AndroidManifest.xml の、起動する画面（MAIN / LAUNCHER）に次を書き足す:
    /// - 文章の共有（ACTION_SEND, text/plain）
    /// - Google マップのリンクを開く（ACTION_VIEW, maps.app.goo.gl と google.com/maps）
    /// 手で書いた AndroidManifest.xml を置かずに済むよう、生成されたものを直す形にしている。
    /// </summary>
    public class AndroidShareIntentPostprocessor : IPostGenerateGradleAndroidProject
    {
        const string AndroidNs = "http://schemas.android.com/apk/res/android";
        const string Marker = "ekinavi-share";

        public int callbackOrder => 10;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning($"[EkiNavi] AndroidManifest.xml がありません: {manifestPath}");
                return;
            }

            var doc = new XmlDocument();
            doc.Load(manifestPath);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("android", AndroidNs);

            var activity = doc.SelectNodes("/manifest/application/activity", ns).Cast<XmlElement>()
                .FirstOrDefault(a => a.SelectSingleNode("intent-filter/action[@android:name='android.intent.action.MAIN']", ns) != null);
            if (activity == null)
            {
                Debug.LogWarning("[EkiNavi] 起動する画面が AndroidManifest.xml に見つかりません。共有の受け取りは付けません");
                return;
            }
            if (activity.SelectSingleNode($"comment()[contains(., '{Marker}')]") != null) return;

            activity.AppendChild(doc.CreateComment($" {Marker}: Google マップの経路を受け取る（IncomingLink） "));
            activity.AppendChild(Filter(doc, "android.intent.action.SEND", new[] { "android.intent.category.DEFAULT" }, mimeType: "text/plain"));
            var view = Filter(doc, "android.intent.action.VIEW",
                new[] { "android.intent.category.DEFAULT", "android.intent.category.BROWSABLE" });
            view.AppendChild(Data(doc, "https", "maps.app.goo.gl", null));
            view.AppendChild(Data(doc, "https", "www.google.com", "/maps"));
            view.AppendChild(Data(doc, "https", "maps.google.com", null));
            activity.AppendChild(view);
            if (!activity.HasAttribute("exported", AndroidNs)) Set(activity, "exported", "true");

            doc.Save(manifestPath);
            Debug.Log("[EkiNavi] AndroidManifest.xml に、Google マップの共有を受け取る設定を足しました");
        }

        /// <summary>android: の付いた属性を付ける（接頭辞が自動の名前にならないよう、android を指定する）。</summary>
        static void Set(XmlElement element, string name, string value)
        {
            var attribute = element.OwnerDocument.CreateAttribute("android", name, AndroidNs);
            attribute.Value = value;
            element.SetAttributeNode(attribute);
        }

        static XmlElement Filter(XmlDocument doc, string action, string[] categories, string mimeType = null)
        {
            var filter = doc.CreateElement("intent-filter");
            var a = doc.CreateElement("action");
            Set(a, "name", action);
            filter.AppendChild(a);
            foreach (var category in categories)
            {
                var c = doc.CreateElement("category");
                Set(c, "name", category);
                filter.AppendChild(c);
            }
            if (mimeType != null)
            {
                var d = doc.CreateElement("data");
                Set(d, "mimeType", mimeType);
                filter.AppendChild(d);
            }
            return filter;
        }

        static XmlElement Data(XmlDocument doc, string scheme, string host, string pathPrefix)
        {
            var d = doc.CreateElement("data");
            Set(d, "scheme", scheme);
            Set(d, "host", host);
            if (pathPrefix != null) Set(d, "pathPrefix", pathPrefix);
            return d;
        }
    }
}
