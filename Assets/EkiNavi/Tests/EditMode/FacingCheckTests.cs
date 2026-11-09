using EkiNavi.Alignment;
using NUnit.Framework;

namespace EkiNavi.Tests
{
    public class FacingCheckTests
    {
        [TestCase(0f, 0f, 0f)]
        [TestCase(350f, 10f, 20f)]
        [TestCase(10f, 350f, 20f)]
        [TestCase(0f, 180f, 180f)]
        public void ErrorDeg_は回り込みを含めた差を返す(float a, float b, float expected)
        {
            Assert.That(FacingCheck.ErrorDeg(a, b), Is.EqualTo(expected).Within(0.001f));
        }

        [TestCase(59.9f, false)]
        [TestCase(60f, true)]
        [TestCase(180f, true)]
        public void IsAway_は60度以上で出す(float error, bool expected)
        {
            Assert.That(FacingCheck.IsAway(false, error), Is.EqualTo(expected));
        }

        [TestCase(55f, true)]
        [TestCase(50f, false)]
        [TestCase(10f, false)]
        public void IsAway_は一度出たら50度以下に戻るまで消さない(float error, bool expected)
        {
            Assert.That(FacingCheck.IsAway(true, error), Is.EqualTo(expected));
        }
    }
}
