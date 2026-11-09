using NUnit.Framework;

namespace EkiNavi.Alignment.Tests
{
    public class MapsRouteLinkTests
    {
        // https://maps.app.goo.gl/n2xowwHmNfAAgKVV9 の転送先（新大久保駅 → 参宮橋駅、電車）
        const string ShinOkuboToSangubashi =
            "https://www.google.com/maps/dir/%E6%96%B0%E5%A4%A7%E4%B9%85%E4%BF%9D%E9%A7%85%E3%80%81%E3%80%92169-0073+%E6%9D%B1%E4%BA%AC%E9%83%BD%E6%96%B0%E5%AE%BF%E5%8C%BA%E7%99%BE%E4%BA%BA%E7%94%BA%EF%BC%91%E4%B8%81%E7%9B%AE/%E5%8F%82%E5%AE%AE%E6%A9%8B%E9%A7%85%E3%80%81%E3%80%92151-0053+%E6%9D%B1%E4%BA%AC%E9%83%BD%E6%B8%8B%E8%B0%B7%E5%8C%BA%E4%BB%A3%E3%80%85%E6%9C%A8%EF%BC%94%E4%B8%81%E7%9B%AE%EF%BC%96/@35.69011,139.6875575,15z/am=t/data=!4m14!4m13!1m5!1m1!1s0x60188d2f37e44929:0x70e03a4ceb52d545!2m2!1d139.7002258!2d35.7012459!1m5!1m1!1s0x60188ccb75257b8b:0xfdea8b6b076f9b70!2m2!1d139.6935182!2d35.6786577!3e3?entry=tts&g_ep=EgoyMDI2MTEwMy4wIPu8ASoASAFQAw%3D%3D&skid=91000529-233a-44dc-afbe-104e3c4894d7";

        [Test]
        public void ReadsNamesAndPointsFromTheDirectionsLink()
        {
            Assert.IsTrue(MapsRouteLink.TryParse(ShinOkuboToSangubashi, out var route));
            Assert.AreEqual("新大久保駅", route.Origin.Name);
            Assert.AreEqual("参宮橋駅", route.Destination.Name);
            Assert.AreEqual(35.7012459, route.Origin.Lat, 1e-6);
            Assert.AreEqual(139.6935182, route.Destination.Lng, 1e-6);
        }

        [Test]
        public void ShinOkuboToSangubashiTransfersToOdakyuAtShinjuku()
        {
            Assert.IsTrue(MapsRouteLink.TryResolve(ShinOkuboToSangubashi, out var result));
            Assert.AreEqual(MapsRouteLink.Shinjuku, result.StationId);
            Assert.AreEqual("odakyu", result.LineId);
            Assert.AreEqual("jr", result.FromLineId);  // JR の改札内 → 小田急線の乗り換え改札
        }

        [Test]
        public void FindsTheLinkInSharedText()
        {
            string shared = "新大久保駅から参宮橋駅への経路\nhttps://maps.app.goo.gl/n2xowwHmNfAAgKVV9";
            string url = MapsRouteLink.ExtractMapsUrl(shared);
            Assert.AreEqual("https://maps.app.goo.gl/n2xowwHmNfAAgKVV9", url);
            Assert.IsTrue(MapsRouteLink.IsShortUrl(url));
            Assert.IsNull(MapsRouteLink.ExtractMapsUrl("https://example.com/maps/dir/a/b"));
        }

        [Test]
        public void ApiStyleLinkInEnglishWorks()
        {
            string url = "https://www.google.com/maps/dir/?api=1&origin=Shin-Okubo+Station&destination=Hatsudai+Station&travelmode=transit";
            Assert.IsTrue(MapsRouteLink.TryResolve(url, out var result));
            Assert.AreEqual(MapsRouteLink.Shinjuku, result.StationId);
            Assert.AreEqual("keio", result.LineId);
        }

        [Test]
        public void StartingAtShinjukuPicksTheDestinationLine()
        {
            string url = "https://www.google.com/maps/dir/?api=1&origin=%E6%96%B0%E5%AE%BF%E9%A7%85&destination=%E4%B9%9D%E6%AE%B5%E4%B8%8B%E9%A7%85";
            Assert.IsTrue(MapsRouteLink.TryResolve(url, out var result));
            Assert.AreEqual(MapsRouteLink.Shinjuku, result.StationId);
            Assert.AreEqual("toei_shinjuku", result.LineId);
        }

        [Test]
        public void RouteThatDoesNotNeedShinjukuIsNotMatched()
        {
            // 小田急線の中だけで済む経路（町田 → 海老名）は、新宿で乗り換えない
            string url = "https://www.google.com/maps/dir/?api=1&origin=Machida&destination=Ebina";
            Assert.IsTrue(MapsRouteLink.TryResolve(url, out var result));
            Assert.IsNull(result.StationId);
        }

        [Test]
        public void ShinjukuPlaceLinkSelectsTheStation()
        {
            string url = "https://www.google.com/maps/place/%E6%96%B0%E5%AE%BF%E9%A7%85/@35.6896,139.7006,17z";
            Assert.IsTrue(MapsRouteLink.TryResolve(url, out var result));
            Assert.AreEqual(MapsRouteLink.Shinjuku, result.StationId);
            Assert.IsNull(result.LineId);
        }
    }
}
