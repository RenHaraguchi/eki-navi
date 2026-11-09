using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace EkiNavi.Alignment
{
    /// <summary>
    /// Google マップの経路のリンクから、乗り換えを案内する駅と、乗り換える路線を決める。
    ///
    /// 使い方: Google マップで経路を出して「共有」→ 駅ナビAR を選ぶ（またはリンクをコピーして、駅を選ぶ画面で貼り付ける）。
    /// 例: 新大久保駅 → 参宮橋駅 の経路なら、新宿駅で小田急線に乗り換える、と決める。
    ///
    /// リンクには、出発と到着の駅名と座標しか入っていない（どの電車に乗るかは入っていない）。
    /// そこで、到着の駅がどの路線にあるかを下の表で引き、新宿駅で乗り換える路線を推定する。
    /// 推定なので、路線を選ぶ画面で利用者が変えられるようにしておく。
    ///
    /// 短いリンク（maps.app.goo.gl）は、そのままでは中身が分からない。
    /// 先に転送先を取り出して（IncomingLink がネットにつないで行う）、長いリンクにしてから渡す。
    /// </summary>
    public static class MapsRouteLink
    {
        public const string Shinjuku = "shinjuku";

        // 新宿駅の真ん中あたり（JR の改札内の中心付近）
        const double ShinjukuLat = 35.6896, ShinjukuLng = 139.7006;
        const double NearShinjukuMeters = 800;

        /// <summary>乗り換え先として選べる路線（路線を選ぶ画面に出す順）。</summary>
        public static readonly string[] TransferLines = { "odakyu", "keio", "toei_shinjuku", "marunouchi" };

        /// <summary>経路の端（出発か到着）。名前は「駅」「Station」や住所を取り除いたもの。</summary>
        public struct Place
        {
            public string Name;
            public bool HasLocation;
            public double Lat, Lng;
        }

        public struct Route
        {
            public Place Origin, Destination;
        }

        public struct Result
        {
            /// <summary>案内する駅（今は "shinjuku" だけ）。見つからなければ null。</summary>
            public string StationId;
            /// <summary>乗り換える路線（TransferLines のどれか）。決められなければ null。</summary>
            public string LineId;
            /// <summary>新宿駅に着く路線（"jr" か TransferLines のどれか）= 案内を始める改札の中。分からなければ null。</summary>
            public string FromLineId;
            public string OriginName, DestinationName;
        }

        // ---- リンクを見分ける ----

        static readonly Regex UrlPattern = new Regex(@"https?://[^\s<>""']+", RegexOptions.Compiled);

        /// <summary>共有で届いた文章（「〇〇から〇〇への経路 https://…」など）から、Google マップのリンクを取り出す。</summary>
        public static string ExtractMapsUrl(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            foreach (Match m in UrlPattern.Matches(text))
                if (IsMapsUrl(m.Value)) return m.Value.TrimEnd('.', ',', ')', '」', '。');
            return null;
        }

        public static bool IsMapsUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            string host = uri.Host.ToLowerInvariant();
            if (host == "maps.app.goo.gl" || host == "maps.google.com" || host.StartsWith("maps.google.")) return true;
            if (host == "goo.gl" && uri.AbsolutePath.StartsWith("/maps")) return true;
            return (host == "google.com" || host.EndsWith(".google.com") || host.StartsWith("www.google."))
                   && uri.AbsolutePath.StartsWith("/maps");
        }

        /// <summary>転送先を調べないと中身が分からない短いリンクか。</summary>
        public static bool IsShortUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            string host = uri.Host.ToLowerInvariant();
            return host == "maps.app.goo.gl" || (host == "goo.gl" && uri.AbsolutePath.StartsWith("/maps"));
        }

        // ---- 長いリンクを読む ----

        static readonly Regex DataPoint = new Regex(@"!1d(-?\d+(?:\.\d+)?)!2d(-?\d+(?:\.\d+)?)", RegexOptions.Compiled);

        /// <summary>
        /// 長いリンクから、出発と到着を読む。次の形に対応する:
        /// - https://www.google.com/maps/dir/出発/到着/@緯度,経度,15z/data=…!1d経度!2d緯度…
        /// - https://www.google.com/maps/dir/?api=1&amp;origin=…&amp;destination=…
        /// - https://maps.google.com/?saddr=…&amp;daddr=…
        /// - https://www.google.com/maps/place/駅名/… （到着だけ）
        /// </summary>
        public static bool TryParse(string url, out Route route)
        {
            route = default;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            var query = ParseQuery(uri.Query);

            string origin = null, destination = null;
            if (query.TryGetValue("origin", out var o)) origin = o;
            if (query.TryGetValue("destination", out var d)) destination = d;
            if (query.TryGetValue("saddr", out var sa)) origin = sa;
            if (query.TryGetValue("daddr", out var da)) destination = da;

            var segments = uri.AbsolutePath.Split('/').Select(Uri.UnescapeDataString).ToList();
            int dir = segments.IndexOf("dir");
            if (dir >= 0 && destination == null)
            {
                // dir の後ろ、@座標 や data= の手前までが経由地の並び。最初が出発、最後が到着
                var stops = segments.Skip(dir + 1).TakeWhile(s => !s.StartsWith("@") && !s.StartsWith("data=") && !s.StartsWith("am=")).ToList();
                if (stops.Count >= 2)
                {
                    origin = stops[0];
                    destination = stops[stops.Count - 1];
                }
                else if (stops.Count == 1)
                {
                    destination = stops[0];
                }
            }
            int place = segments.IndexOf("place");
            if (place >= 0 && place + 1 < segments.Count && destination == null) destination = segments[place + 1];
            if (query.TryGetValue("q", out var q) && destination == null) destination = q;

            route.Origin.Name = CleanName(origin);
            route.Destination.Name = CleanName(destination);

            // 経路のリンクでは、data= の中に 出発 → 到着 の順で座標が入っている（!1d 経度 !2d 緯度）
            var points = DataPoint.Matches(uri.OriginalString).Cast<Match>().ToList();
            if (dir >= 0 && points.Count >= 2)
            {
                SetLocation(ref route.Origin, points[0]);
                SetLocation(ref route.Destination, points[points.Count - 1]);
            }
            else if (points.Count == 1)
            {
                SetLocation(ref route.Destination, points[0]);
            }
            // 名前の代わりに「緯度,経度」が入っていることもある
            TryLatLngName(ref route.Origin);
            TryLatLngName(ref route.Destination);

            return !string.IsNullOrEmpty(route.Destination.Name) || route.Destination.HasLocation;
        }

        static void SetLocation(ref Place place, Match m)
        {
            place.Lng = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            place.Lat = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            place.HasLocation = true;
        }

        static void TryLatLngName(ref Place place)
        {
            if (string.IsNullOrEmpty(place.Name)) return;
            var parts = place.Name.Split(',');
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lng))
            {
                place.Lat = lat;
                place.Lng = lng;
                place.HasLocation = true;
                place.Name = "";
            }
        }

        static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(query)) return result;
            foreach (var pair in query.TrimStart('?').Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                string key = pair.Substring(0, eq);
                string value = Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                result[key] = value;
            }
            return result;
        }

        /// <summary>「新大久保駅、〒169-0073 東京都…」→「新大久保駅」。「+」は空白に戻す。</summary>
        static string CleanName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Replace('+', ' ').Trim();
            int cut = s.IndexOfAny(new[] { '、', ',', '，' });
            // 「35.69,139.70」のような座標は切らない
            if (cut > 0 && !Regex.IsMatch(s, @"^-?\d+(\.\d+)?,\s*-?\d+(\.\d+)?$")) s = s.Substring(0, cut);
            return s.Trim();
        }

        // ---- 駅と路線を決める ----

        /// <summary>経路から、案内する駅と乗り換える路線を決める。</summary>
        public static Result Resolve(Route route)
        {
            var result = new Result { OriginName = route.Origin.Name, DestinationName = route.Destination.Name };
            string originKey = StationKey(route.Origin.Name), destinationKey = StationKey(route.Destination.Name);
            bool originIsShinjuku = originKey == Shinjuku || Near(route.Origin);
            bool destinationIsShinjuku = destinationKey == Shinjuku || Near(route.Destination);
            var originLines = LinesOf(originKey);
            var destinationLines = LinesOf(destinationKey);

            if (destinationIsShinjuku)
            {
                // 新宿駅が行き先: 駅だけ決める（どの出口・のりばかは、路線の画面で選んでもらう）
                result.StationId = Shinjuku;
                return result;
            }

            // 到着の駅がある路線のうち、出発の駅にはない路線 = 新宿で乗り換える路線
            string line = TransferLines.FirstOrDefault(l => destinationLines.Contains(l) && !originLines.Contains(l));
            if (originIsShinjuku)
            {
                result.StationId = Shinjuku;
                result.LineId = TransferLines.FirstOrDefault(destinationLines.Contains);
                return result;
            }
            // 出発の駅が新宿に来る路線にある（または分からない）なら、新宿で乗り換えるとみなす
            bool originReachesShinjuku = originLines.Count == 0 || originLines.Contains("jr") || originLines.Overlaps(TransferLines);
            if (line != null && originReachesShinjuku)
            {
                result.StationId = Shinjuku;
                result.LineId = line;
                // 出発の駅から新宿に来る路線。JR を優先する（JR の改札内から案内を始める経路が多いため）
                if (originLines.Contains("jr")) result.FromLineId = "jr";
                else result.FromLineId = TransferLines.FirstOrDefault(l => originLines.Contains(l) && l != line);
            }
            return result;
        }

        /// <summary>リンクをそのまま渡して、駅と路線を決める。読めなければ false。</summary>
        public static bool TryResolve(string url, out Result result)
        {
            result = default;
            if (!TryParse(url, out var route)) return false;
            result = Resolve(route);
            return true;
        }

        static bool Near(Place p)
        {
            if (!p.HasLocation) return false;
            double dLat = (p.Lat - ShinjukuLat) * 111_000;
            double dLng = (p.Lng - ShinjukuLng) * 111_000 * Math.Cos(ShinjukuLat * Math.PI / 180);
            return Math.Sqrt(dLat * dLat + dLng * dLng) <= NearShinjukuMeters;
        }

        static HashSet<string> LinesOf(string key) =>
            key != null && StationLines.TryGetValue(key, out var lines) ? new HashSet<string>(lines) : new HashSet<string>();

        /// <summary>駅名を表のキーにする。日本語はそのまま（「駅」を除く）、英語は小文字の英字だけにする。</summary>
        public static string StationKey(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string s = name.Trim();
            s = Regex.Replace(s, @"[\s　]", "");
            s = Regex.Replace(s, @"(駅|站|역)$", "");
            if (Regex.IsMatch(s, @"^[\x00-\x7FÀ-ſ]+$"))
            {
                s = s.ToLowerInvariant()
                    .Replace("ō", "o").Replace("ū", "u").Replace("ā", "a").Replace("ē", "e").Replace("ī", "i");
                s = Regex.Replace(s, @"station$", "");
                s = Regex.Replace(s, "[^a-z0-9]", "");
            }
            return Aliases.TryGetValue(s, out var key) ? key : s;
        }

        // 表のキーは日本語の駅名。英語の名前はここで日本語に寄せる
        static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>
        {
            ["新宿"] = Shinjuku, ["shinjuku"] = Shinjuku, ["バスタ新宿"] = Shinjuku, ["新宿西口"] = Shinjuku,
            ["shinokubo"] = "新大久保", ["yoyogi"] = "代々木", ["harajuku"] = "原宿", ["shibuya"] = "渋谷",
            ["takadanobaba"] = "高田馬場", ["ikebukuro"] = "池袋", ["okubo"] = "大久保", ["nakano"] = "中野",
            ["kichijoji"] = "吉祥寺", ["mitaka"] = "三鷹", ["tachikawa"] = "立川", ["tokyo"] = "東京",
            ["shinagawa"] = "品川", ["ebisu"] = "恵比寿", ["yokohama"] = "横浜", ["omiya"] = "大宮",
            ["minamishinjuku"] = "南新宿", ["sangubashi"] = "参宮橋", ["yoyogihachiman"] = "代々木八幡",
            ["yoyogiuehara"] = "代々木上原", ["shimokitazawa"] = "下北沢", ["kyodo"] = "経堂",
            ["seijogakuenmae"] = "成城学園前", ["noborito"] = "登戸", ["shinyurigaoka"] = "新百合ヶ丘",
            ["machida"] = "町田", ["sagamiono"] = "相模大野", ["ebina"] = "海老名", ["honatsugi"] = "本厚木",
            ["odawara"] = "小田原", ["fujisawa"] = "藤沢", ["katasenoshima"] = "片瀬江ノ島", ["hakoneyumoto"] = "箱根湯本",
            ["hatsudai"] = "初台", ["hatagaya"] = "幡ヶ谷", ["sasazuka"] = "笹塚", ["meidaimae"] = "明大前",
            ["chitosekarasuyama"] = "千歳烏山", ["chofu"] = "調布", ["fuchu"] = "府中", ["seisekisakuragaoka"] = "聖蹟桜ヶ丘",
            ["takahatafudo"] = "高幡不動", ["keiohachioji"] = "京王八王子", ["hachioji"] = "八王子", ["takaosanguchi"] = "高尾山口",
            ["hashimoto"] = "橋本", ["keiotamacenter"] = "京王多摩センター",
            ["shinjukusanchome"] = "新宿三丁目", ["akebonobashi"] = "曙橋", ["ichigaya"] = "市ヶ谷", ["kudanshita"] = "九段下",
            ["jimbocho"] = "神保町", ["iwamotocho"] = "岩本町", ["bakuroyokoyama"] = "馬喰横山", ["morishita"] = "森下",
            ["sumiyoshi"] = "住吉", ["ojima"] = "大島", ["funabori"] = "船堀", ["motoyawata"] = "本八幡",
            ["nishishinjuku"] = "西新宿", ["nakanosakaue"] = "中野坂上", ["ogikubo"] = "荻窪", ["shinjukugyoemmae"] = "新宿御苑前",
            ["yotsuya"] = "四ツ谷", ["yotsuyasanchome"] = "四谷三丁目", ["akasakamitsuke"] = "赤坂見附",
            ["kasumigaseki"] = "霞ケ関", ["ginza"] = "銀座", ["otemachi"] = "大手町", ["ochanomizu"] = "御茶ノ水",
            ["korakuen"] = "後楽園", ["myogadani"] = "茗荷谷",
        };

        // 駅 → 通っている路線（新宿駅で乗り換えを案内する路線と JR だけ。必要な駅だけ）
        static readonly Dictionary<string, string[]> StationLines = Build(
            ("jr", "新大久保 代々木 原宿 渋谷 恵比寿 品川 高田馬場 池袋 大久保 東中野 中野 高円寺 阿佐ケ谷 荻窪 吉祥寺 三鷹 立川 八王子 " +
                   "四ツ谷 市ヶ谷 飯田橋 御茶ノ水 神田 東京 大崎 赤羽 浦和 大宮 横浜 武蔵小杉 橋本 町田 藤沢 小田原"),
            ("odakyu", "南新宿 参宮橋 代々木八幡 代々木上原 東北沢 下北沢 世田谷代田 梅ヶ丘 豪徳寺 経堂 千歳船橋 祖師ヶ谷大蔵 " +
                       "成城学園前 喜多見 狛江 和泉多摩川 登戸 向ヶ丘遊園 新百合ヶ丘 町田 相模大野 海老名 本厚木 秦野 小田原 " +
                       "箱根湯本 藤沢 片瀬江ノ島 唐木田 小田急多摩センター"),
            ("keio", "初台 幡ヶ谷 笹塚 代田橋 明大前 下高井戸 桜上水 上北沢 八幡山 芦花公園 千歳烏山 仙川 つつじヶ丘 調布 " +
                     "府中 分倍河原 聖蹟桜ヶ丘 高幡不動 京王八王子 北野 高尾 高尾山口 京王稲田堤 京王永山 京王多摩センター 橋本 八王子"),
            ("toei_shinjuku", "新宿三丁目 曙橋 市ヶ谷 九段下 神保町 小川町 岩本町 馬喰横山 浜町 森下 菊川 住吉 西大島 大島 東大島 " +
                              "船堀 一之江 瑞江 篠崎 本八幡"),
            ("marunouchi", "西新宿 中野坂上 新中野 東高円寺 新高円寺 南阿佐ケ谷 荻窪 中野新橋 中野富士見町 方南町 新宿三丁目 " +
                           "新宿御苑前 四谷三丁目 四ツ谷 赤坂見附 国会議事堂前 霞ケ関 銀座 東京 大手町 淡路町 御茶ノ水 本郷三丁目 " +
                           "後楽園 茗荷谷 新大塚 池袋"));

        static Dictionary<string, string[]> Build(params (string line, string stations)[] rows)
        {
            var map = new Dictionary<string, List<string>>();
            foreach (var (line, stations) in rows)
                foreach (var station in stations.Split(' '))
                {
                    if (!map.TryGetValue(station, out var lines)) map[station] = lines = new List<string>();
                    lines.Add(line);
                }
            return map.ToDictionary(p => p.Key, p => p.Value.ToArray());
        }
    }
}
