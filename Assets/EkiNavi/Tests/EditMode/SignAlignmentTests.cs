using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace EkiNavi.Alignment.Tests
{
    /// <summary>
    /// server/app/alignment.py（基準の実装）と同じ答えが出るかを、contracts/alignment_vectors.json で確かめる。
    /// </summary>
    public class SignAlignmentTests
    {
        const float PositionTolerance = 1e-3f; // 1mm
        const float AngleTolerance = 1e-3f;    // 度

        // ---- contracts/alignment_vectors.json の形 ----
        // JsonUtility が値を入れるので、「代入されていない」警告は出さない
#pragma warning disable 0649

        [Serializable]
        class VectorFile
        {
            public Case[] vectors;
        }

        [Serializable]
        class Case
        {
            public string name;
            public XZ signMap;
            public float signAzimuthDeg;
            public float correctionYawDeg;
            public XZ correction;
            public XZ detectedPosition;
            public XZ detectedNormal;
            public float expectedYawDeg;
            public Pair[] mapToAr;
        }

        [Serializable]
        class Pair
        {
            public XZ map;
            public XZ ar;
        }

        [Serializable]
        class XZ
        {
            public float x;
            public float z;

            public Vector2 ToVector2() => new Vector2(x, z);
        }
#pragma warning restore 0649

        static Case[] LoadVectors()
        {
            // Assets の1つ上がリポジトリの一番上
            var path = Path.Combine(Application.dataPath, "..", "contracts", "alignment_vectors.json");
            Assert.That(File.Exists(path), Is.True, $"見つかりません: {path}");
            var file = JsonUtility.FromJson<VectorFile>(File.ReadAllText(path));
            Assert.That(file?.vectors, Is.Not.Null.And.Not.Empty, "alignment_vectors.json に vectors がありません");
            return file.vectors;
        }

        static SignAlignment Align(Case c) => SignAlignment.FromDetection(
            c.signMap.ToVector2(),
            c.signAzimuthDeg,
            c.detectedPosition.ToVector2(),
            c.detectedNormal.ToVector2(),
            c.correctionYawDeg,
            c.correction.ToVector2());

        // ---- 検算用データとの突き合わせ ----

        [Test]
        public void YawMatchesTheReference()
        {
            foreach (var c in LoadVectors())
            {
                var yaw = Align(c).YawDeg;
                Assert.That(Mathf.Abs(Mathf.DeltaAngle(yaw, c.expectedYawDeg)), Is.LessThan(AngleTolerance),
                    $"{c.name}: yaw {yaw} / 期待 {c.expectedYawDeg}");
                Assert.That(yaw, Is.GreaterThan(-180f).And.LessThanOrEqualTo(180f), $"{c.name}: 範囲外 {yaw}");
            }
        }

        [Test]
        public void MapToArMatchesTheReference()
        {
            foreach (var c in LoadVectors())
            {
                var alignment = Align(c);
                Assert.That(c.mapToAr, Is.Not.Null.And.Not.Empty, $"{c.name}: mapToAr がありません");
                foreach (var pair in c.mapToAr)
                {
                    var actual = alignment.MapToAr(pair.map.ToVector2());
                    var expected = pair.ar.ToVector2();
                    Assert.That((actual - expected).magnitude, Is.LessThan(PositionTolerance),
                        $"{c.name}: 地図 ({pair.map.x}, {pair.map.z}) → {actual} / 期待 {expected}");
                }
            }
        }

        // ---- Unity の回転との一致 ----

        [Test]
        public void RotateYMatchesUnityQuaternion()
        {
            var v = new Vector2(3f, -4f);
            foreach (var yaw in new[] { 0f, 37f, -160f, 90f, 180f, 353f })
            {
                var expected = Quaternion.Euler(0f, yaw, 0f) * new Vector3(v.x, 0f, v.y);
                var actual = Azimuth.RotateY(v, yaw);
                Assert.That((actual - new Vector2(expected.x, expected.z)).magnitude, Is.LessThan(PositionTolerance),
                    $"yaw {yaw}: {actual} / Unity {expected}");
            }
        }

        [Test]
        public void AzimuthOfFollowsNorthClockwise()
        {
            Assert.That(Azimuth.Of(new Vector2(0f, 1f)), Is.EqualTo(0f).Within(AngleTolerance));   // 北
            Assert.That(Azimuth.Of(new Vector2(1f, 0f)), Is.EqualTo(90f).Within(AngleTolerance));  // 東
            Assert.That(Azimuth.Of(new Vector2(0f, -1f)), Is.EqualTo(180f).Within(AngleTolerance)); // 南
            Assert.That(Azimuth.Of(new Vector2(-1f, 0f)), Is.EqualTo(270f).Within(AngleTolerance)); // 西
        }

        [Test]
        public void NormalizeWrapsIntoHalfOpenRange()
        {
            Assert.That(Azimuth.Normalize(190f), Is.EqualTo(-170f).Within(AngleTolerance));
            Assert.That(Azimuth.Normalize(-190f), Is.EqualTo(170f).Within(AngleTolerance));
            Assert.That(Azimuth.Normalize(180f), Is.EqualTo(180f).Within(AngleTolerance));
            Assert.That(Azimuth.Normalize(-180f), Is.EqualTo(180f).Within(AngleTolerance));
            Assert.That(Azimuth.Normalize(720f), Is.EqualTo(0f).Within(AngleTolerance));
        }

        // ---- RouteRenderer の置き方 ----

        [Test]
        public void RouteRendererPosePutsEveryRoutePointWhereMapToArSays()
        {
            foreach (var c in LoadVectors())
            {
                var alignment = Align(c);
                var first = new Vector2(21f, 141f);
                const float floorY = -1.4f;
                var pose = alignment.RouteRendererPose(first, floorY);

                // RouteRenderer は (点 − 最初の点) を自分の transform で置く
                foreach (var pair in c.mapToAr)
                {
                    var map = pair.map.ToVector2();
                    var local = new Vector3(map.x - first.x, 0f, map.y - first.y);
                    var world = pose.position + pose.rotation * local;
                    var expected = alignment.MapToAr(map, floorY);
                    Assert.That((world - expected).magnitude, Is.LessThan(PositionTolerance),
                        $"{c.name}: {world} / 期待 {expected}");
                }
            }
        }

        [Test]
        public void TheSignLandsOnItsDetectedPositionAndFacing()
        {
            var signMap = new Vector2(20f, 136.5f);
            const float signAzimuth = 353f;
            var detected = new Vector2(1.5f, -2f);
            var detectedNormal = new Vector2(0.5f, 0.866025f);
            var alignment = SignAlignment.FromDetection(signMap, signAzimuth, detected, detectedNormal);

            Assert.That((alignment.MapToAr(signMap) - detected).magnitude, Is.LessThan(PositionTolerance));

            // 地図で登録した板の向きを回すと、認識した法線の向きになる
            var rad = signAzimuth * Mathf.Deg2Rad;
            var rotated = Azimuth.RotateY(new Vector2(Mathf.Sin(rad), Mathf.Cos(rad)), alignment.YawDeg);
            Assert.That((rotated - detectedNormal.normalized).magnitude, Is.LessThan(PositionTolerance));
        }

        // ---- 認識結果の取り出し ----

        [Test]
        public void HorizontalNormalIgnoresTilt()
        {
            // 少し上を向いた壁の板。水平にすると北東
            var tilted = new Vector3(1f, 0.3f, 1f);
            Assert.That(SignDetection.TryHorizontalNormal(tilted, out var h), Is.True);
            Assert.That(h.magnitude, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(Azimuth.Of(h), Is.EqualTo(45f).Within(AngleTolerance));
        }

        [Test]
        public void HorizontalNormalRejectsFlatSigns()
        {
            // 床や天井と平行な板は、方位が決まらないので使わない
            Assert.That(SignDetection.TryHorizontalNormal(Vector3.up, out _), Is.False);
            Assert.That(SignDetection.TryHorizontalNormal(new Vector3(0.1f, 1f, 0f), out _), Is.False);
        }

        [Test]
        public void BackSideDetectionIsRejected()
        {
            var sign = new Vector3(0f, 1.5f, 0f);
            var normal = new Vector2(0f, 1f); // 板は北を向いている
            Assert.That(SignDetection.IsViewedFromFront(sign, normal, new Vector3(0.3f, 1.4f, 2f)), Is.True);
            Assert.That(SignDetection.IsViewedFromFront(sign, normal, new Vector3(0.3f, 1.4f, -2f)), Is.False);
        }
    }
}
