using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace EkiNavi.Alignment.Tests
{
    public class SignCatalogTests
    {
        const string BundledPath = "EkiNavi/Data/signs.json";

        static string RepoPath(params string[] parts) =>
            Path.Combine(new[] { Application.dataPath, ".." }.Concat(parts).ToArray());

        static string Bundled() => File.ReadAllText(Path.Combine(Application.dataPath, BundledPath));

        [Test]
        public void BundledSignsMatchTheServer()
        {
            // アプリに同梱した一覧が、サーバーの一覧と同じであること。ずれていたら次でコピーし直す:
            //   cp server/station_data/signs.json Assets/EkiNavi/Data/signs.json
            var server = File.ReadAllText(RepoPath("server", "station_data", "signs.json"));
            Assert.That(Bundled().Replace("\r\n", "\n"), Is.EqualTo(server.Replace("\r\n", "\n")),
                "Assets/EkiNavi/Data/signs.json が server/station_data/signs.json と違います");
        }

        [Test]
        public void ParsesTheBundledSigns()
        {
            var catalog = SignCatalog.Parse(Bundled());
            Assert.That(catalog.Signs.Count, Is.GreaterThan(0));
            Assert.That(catalog.TryGetByImageName("sign_2f_south", out var sign), Is.True);
            Assert.That(sign.id, Is.EqualTo("S-2F-SOUTH"));
            Assert.That(sign.ordinal, Is.EqualTo(2f));
            Assert.That(sign.azimuthDeg, Is.EqualTo(348f));
            Assert.That(sign.centerHeightMeters, Is.GreaterThan(0f));
            Assert.That(catalog.TryGetById("S-2F-SOUTH", out var same), Is.True);
            Assert.That(same, Is.SameAs(sign));
        }

        [Test]
        public void ParsesTheStationSample()
        {
            // GET /v1/station の応答もそのまま読める
            var json = File.ReadAllText(RepoPath("contracts", "samples", "station.json"));
            var catalog = SignCatalog.Parse(json);
            Assert.That(catalog.Signs.Count, Is.EqualTo(SignCatalog.Parse(Bundled()).Signs.Count));
        }

        [Test]
        public void UnknownNamesAreNotFound()
        {
            var catalog = SignCatalog.Parse(Bundled());
            Assert.That(catalog.TryGetByImageName("no_such_image", out _), Is.False);
            Assert.That(catalog.TryGetByImageName(null, out _), Is.False);
            Assert.That(catalog.TryGetById("no-such-id", out _), Is.False);
        }

        [Test]
        public void RejectsBrokenLists()
        {
            Assert.Throws<FormatException>(() => SignCatalog.Parse("{\"signs\": []}"));
            Assert.Throws<FormatException>(() => SignCatalog.Parse("{}"));
            Assert.Throws<FormatException>(() => SignCatalog.Parse(
                "{\"signs\": [{\"id\": \"A\", \"imageName\": \"same\"}, {\"id\": \"B\", \"imageName\": \"same\"}]}"));
            Assert.Throws<FormatException>(() => SignCatalog.Parse(
                "{\"signs\": [{\"id\": \"A\", \"imageName\": \"\"}]}"));
        }

        [Test]
        public void AlignUsesTheCorrections()
        {
            var sign = new SignEntry
            {
                id = "T", imageName = "t", x = 20f, z = 136.5f, azimuthDeg = 353f,
                correctionYawDeg = 3f, correctionX = 0.5f, correctionZ = -1f,
            };
            var detected = new Vector2(1.5f, -2f);
            var normal = new Vector2(0.5f, 0.866025f);
            var expected = SignAlignment.FromDetection(new Vector2(20f, 136.5f), 353f, detected, normal, 3f, new Vector2(0.5f, -1f));
            var actual = sign.Align(detected, normal);
            Assert.That(actual.YawDeg, Is.EqualTo(expected.YawDeg).Within(1e-4f));
            Assert.That((actual.SignMap - expected.SignMap).magnitude, Is.LessThan(1e-4f));
            // 補正後の板の位置が、認識した位置に来る
            Assert.That((actual.MapToAr(new Vector2(20.5f, 135.5f)) - detected).magnitude, Is.LessThan(1e-3f));
        }
    }
}
