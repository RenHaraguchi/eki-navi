using System;
using System.Collections;
using EkiNavi.Alignment;
using UnityEngine;
using UnityEngine.Networking;

namespace EkiNavi.Route
{
    /// <summary>
    /// Google マップから届いた経路のリンクを受け取り、駅と路線を決めて知らせる。
    ///
    /// 受け取り方は 2 つ:
    /// - Android の「共有」: Google マップで経路を出し、共有 → 駅ナビAR。届いた文章からリンクを取り出す
    ///   （受け取れるようにする設定は Editor/AndroidShareIntentPostprocessor.cs が書き足す）
    /// - 貼り付け: 駅を選ぶ画面のボタンから、コピーしてあるリンクを読む（PasteFromClipboard）
    ///
    /// 短いリンク（maps.app.goo.gl）は、転送先をネットで調べて長いリンクにしてから読む。
    /// </summary>
    public class IncomingLink : MonoBehaviour
    {
        /// <summary>読み取りの結果。</summary>
        public enum Outcome { Resolved, NotMapsLink, NoStation, Offline }

        /// <summary>リンクを読み終えたとき（駅が決まったか、決まらなかったか）。</summary>
        public event Action<Outcome, MapsRouteLink.Result> Handled;

        /// <summary>短いリンクの転送先を調べている最中か。</summary>
        public bool Busy { get; private set; }

        void Start() => CheckAndroidIntent();

        void OnApplicationFocus(bool focus)
        {
            // アプリが開いたまま、別のアプリから共有されたときも、戻ってきたところで読む
            if (focus) CheckAndroidIntent();
        }

        /// <summary>コピーしてあるリンク（またはリンクを含む文章）を読む。</summary>
        public void PasteFromClipboard() => Handle(GUIUtility.systemCopyBuffer);

        /// <summary>リンクを含む文章を読む。</summary>
        public void Handle(string text)
        {
            if (Busy) return;
            string url = MapsRouteLink.ExtractMapsUrl(text);
            if (url == null)
            {
                Handled?.Invoke(Outcome.NotMapsLink, default);
                return;
            }
            StartCoroutine(Resolve(url));
        }

        IEnumerator Resolve(string url)
        {
            Busy = true;
            try
            {
                // 短いリンクは、転送先（Location）を順にたどる。転送は自分でたどる（Unity は最後の URL を教えてくれないため）
                for (int hop = 0; hop < 5 && MapsRouteLink.IsShortUrl(url); hop++)
                {
                    using var request = UnityWebRequest.Head(url);
                    request.redirectLimit = 0;
                    request.timeout = 10;
                    yield return request.SendWebRequest();
                    string next = request.GetResponseHeader("Location");
                    if (string.IsNullOrEmpty(next))
                    {
                        Debug.LogWarning($"[EkiNavi] リンクの転送先が分かりません: {url} ({request.responseCode} {request.error})");
                        Handled?.Invoke(Outcome.Offline, default);
                        yield break;
                    }
                    url = new Uri(new Uri(url), next).AbsoluteUri;
                }

                if (!MapsRouteLink.TryResolve(url, out var result))
                {
                    Handled?.Invoke(Outcome.NotMapsLink, default);
                    yield break;
                }
                Debug.Log($"[EkiNavi] 経路のリンク: {result.OriginName} → {result.DestinationName} / 駅 {result.StationId ?? "-"} / 路線 {result.LineId ?? "-"}");
                Handled?.Invoke(result.StationId != null ? Outcome.Resolved : Outcome.NoStation, result);
            }
            finally
            {
                Busy = false;
            }
        }

        void CheckAndroidIntent()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                if (intent == null) return;
                string action = intent.Call<string>("getAction");
                string text = action switch
                {
                    "android.intent.action.SEND" => intent.Call<string>("getStringExtra", "android.intent.extra.TEXT"),
                    "android.intent.action.VIEW" => intent.Call<string>("getDataString"),
                    _ => null,
                };
                if (string.IsNullOrEmpty(text)) return;
                // 同じ共有を二度読まないよう、受け取った知らせを空にしておく
                using (var empty = new AndroidJavaObject("android.content.Intent")) activity.Call("setIntent", empty);
                Handle(text);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[EkiNavi] 共有されたリンクを読めません: {e.Message}");
            }
#endif
        }
    }
}
