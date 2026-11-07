using System.Collections;
using System.Collections.Generic;
using System.Text;
using Google.XR.ARCoreExtensions;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace EkiNavi.Geospatial
{
    /// <summary>
    /// 駅の中で ARCore の Geospatial API（VPS）が使えるかを確かめるための画面。経路は描かない。
    ///
    /// 画面に出すもの:
    /// - Earth の状態と追跡状態
    /// - いまの緯度・経度・高さと、その誤差（水平・垂直・向き）
    /// - いまいる場所で VPS が使えるか（5秒ごとに問い合わせ）
    /// - 新宿駅の周りの決めた地点で VPS が使えるか（起動後に1回）
    ///
    /// 見方: 水平の誤差が数m以内、向きの誤差が数度以内まで下がれば、VPS で位置が合っている。
    /// 誤差が10m以上のまま下がらなければ、GPS だけで動いている（VPS が効いていない）。
    /// VPS の問い合わせは緯度・経度だけで答えるので、地下にいても真上の道路が対象なら Available になる。
    /// 本当に使えるかは、誤差の値で判断する。
    /// </summary>
    public class GeospatialProbe : MonoBehaviour
    {
        [SerializeField] ARCoreExtensions arCoreExtensions;
        [SerializeField] AREarthManager earthManager;
        [Tooltip("いまいる場所の VPS の問い合わせ間隔（秒）")]
        [SerializeField] float vpsCheckInterval = 5f;
        [Tooltip("記録を Logcat に出す間隔（秒）")]
        [SerializeField] float logInterval = 2f;

        // 新宿駅の周りの地点（緯度・経度はおおよその値）
        static readonly (string name, double lat, double lng)[] Spots =
        {
            ("JR East exit", 35.6912, 139.7009),
            ("JR West exit", 35.6910, 139.6990),
            ("JR South exit", 35.6886, 139.7003),
            ("JR New South", 35.6876, 139.7015),
            ("Busta Shinjuku", 35.6884, 139.7016),
            ("Center of station", 35.6896, 139.7006),
        };

        readonly Dictionary<string, string> spotResults = new();
        string hereVps = "-";
        string status = "Starting...";
        float nextVpsCheck;
        float nextLog;
        bool spotsChecked;
        bool checkingHere;
        double bestHorizontal = double.MaxValue;
        double bestYaw = double.MaxValue;
        GUIStyle style;

        void Start()
        {
            if (earthManager == null) earthManager = FindFirstObjectByType<AREarthManager>();
            if (arCoreExtensions == null) arCoreExtensions = FindFirstObjectByType<ARCoreExtensions>();
#if UNITY_ANDROID
            // Geospatial API は端末の位置情報を使うので、許可がいる
            if (!Permission.HasUserAuthorizedPermission(Permission.FineLocation))
                Permission.RequestUserPermission(Permission.FineLocation);
#endif
        }

        void Update()
        {
            if (earthManager == null)
            {
                status = "AREarthManager is missing";
                return;
            }
            if (ARSession.state != ARSessionState.SessionTracking)
            {
                status = $"AR session: {ARSession.state}";
                return;
            }

            var support = earthManager.IsGeospatialModeSupported(GeospatialMode.Enabled);
            if (support == FeatureSupported.Unknown)
            {
                status = "Checking device support...";
                return;
            }
            if (support == FeatureSupported.Unsupported)
            {
                status = "This device does not support the Geospatial API";
                return;
            }

            if (!spotsChecked && earthManager.EarthState == EarthState.Enabled)
            {
                spotsChecked = true;
                StartCoroutine(CheckSpots());
            }

            if (earthManager.EarthTrackingState == TrackingState.Tracking)
            {
                var pose = earthManager.CameraGeospatialPose;
                if (pose.HorizontalAccuracy < bestHorizontal) bestHorizontal = pose.HorizontalAccuracy;
                if (pose.OrientationYawAccuracy < bestYaw) bestYaw = pose.OrientationYawAccuracy;

                if (!checkingHere && Time.time >= nextVpsCheck)
                {
                    nextVpsCheck = Time.time + vpsCheckInterval;
                    StartCoroutine(CheckHere(pose.Latitude, pose.Longitude));
                }

                if (Time.time >= nextLog)
                {
                    nextLog = Time.time + logInterval;
                    // Logcat で「EkiNaviGeo」で絞り込むと、歩いた記録を後から見られる
                    Debug.Log($"[EkiNaviGeo] lat {pose.Latitude:F7} lng {pose.Longitude:F7} alt {pose.Altitude:F1} " +
                              $"hAcc {pose.HorizontalAccuracy:F1} vAcc {pose.VerticalAccuracy:F1} " +
                              $"yawAcc {pose.OrientationYawAccuracy:F1} vpsHere {hereVps}");
                }
            }

            status = "";
        }

        IEnumerator CheckHere(double lat, double lng)
        {
            checkingHere = true;
            var promise = AREarthManager.CheckVpsAvailabilityAsync(lat, lng);
            yield return promise;
            hereVps = promise.Result.ToString();
            checkingHere = false;
        }

        IEnumerator CheckSpots()
        {
            foreach (var spot in Spots)
            {
                spotResults[spot.name] = "...";
                var promise = AREarthManager.CheckVpsAvailabilityAsync(spot.lat, spot.lng);
                yield return promise;
                spotResults[spot.name] = promise.Result.ToString();
                Debug.Log($"[EkiNaviGeo] VPS at {spot.name}: {promise.Result}");
            }
        }

        // 画面の文字は英数字だけにしている（端末によって日本語のフォントがないため）
        void OnGUI()
        {
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(Screen.height * 0.02f) };
                style.normal.textColor = Color.white;
            }

            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(status)) sb.AppendLine(status);
            if (earthManager != null)
            {
                sb.AppendLine($"Earth state: {earthManager.EarthState}");
                sb.AppendLine($"Earth tracking: {earthManager.EarthTrackingState}");
                if (earthManager.EarthTrackingState == TrackingState.Tracking)
                {
                    var pose = earthManager.CameraGeospatialPose;
                    sb.AppendLine($"Lat/Lng: {pose.Latitude:F6}, {pose.Longitude:F6}");
                    sb.AppendLine($"Altitude: {pose.Altitude:F1} m");
                    sb.AppendLine($"Horizontal error: {pose.HorizontalAccuracy:F1} m  (best {bestHorizontal:F1})");
                    sb.AppendLine($"Vertical error: {pose.VerticalAccuracy:F1} m");
                    sb.AppendLine($"Heading error: {pose.OrientationYawAccuracy:F1} deg  (best {bestYaw:F1})");
                    sb.AppendLine(pose.HorizontalAccuracy <= 5 && pose.OrientationYawAccuracy <= 10
                        ? "=> Looks localized by VPS"
                        : "=> Not localized yet (GPS only?)");
                }
            }
            sb.AppendLine($"VPS here: {hereVps}");
            sb.AppendLine();
            sb.AppendLine("VPS around Shinjuku station:");
            foreach (var spot in Spots)
            {
                spotResults.TryGetValue(spot.name, out var r);
                sb.AppendLine($"  {spot.name}: {r ?? "-"}");
            }

            GUI.Label(new Rect(20, 40, Screen.width - 40, Screen.height - 80), sb.ToString(), style);
        }
    }
}
