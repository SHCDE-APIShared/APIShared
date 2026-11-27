using APIShared.Internal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Globalization;
namespace APIShared.Core.Tests;
[TestClass]
public class JsonTests
{
    [TestMethod]
    public void NumbersEscapesAndRoundTripAreCultureIndependent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var payload = (Dictionary<string, object>)DependencyFreeJson.Parse("{\"number\":1.25,\"max\":18446744073709551615,\"text\":\"line\\n\\\"\\\\ä\",\"array\":[null,true,false,-2]}");
            Assert.AreEqual(1.25, payload["number"]);
            Assert.AreEqual(ulong.MaxValue, payload["max"]);
            Assert.AreEqual("line\n\"\\ä", payload["text"]);
            var encoded = DependencyFreeJson.Serialize(payload);
            Assert.IsTrue(encoded.EndsWith("\r\n", StringComparison.Ordinal));
            var decoded = (Dictionary<string, object>)DependencyFreeJson.Parse(encoded);
            Assert.AreEqual(payload["number"], decoded["number"]);
            Assert.AreEqual(payload["text"], decoded["text"]);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
    [TestMethod]
    public void CompactEncodingPreservesStringsAndValuesWithoutChangingTheFileEncoding()
    {
        const string text = "  spaced \"quote\\\" \\ \\\\ \r\n\tä Ω  ";
        var payload = new Dictionary<string, object> { ["text"] = text,
            ["nested"] = new Dictionary<string, object> { ["values"] = new object[] { true, null, 1.25, "" } } };
        string compact = DependencyFreeJson.SerializeCompact(payload);
        Assert.IsFalse(compact.Contains('\r') || compact.Contains('\n') || compact.Contains('\t'));
        var parsed = (Dictionary<string, object>)DependencyFreeJson.Parse(compact);
        Assert.AreEqual(text, parsed["text"]);
        var values = (List<object>)((Dictionary<string, object>)parsed["nested"])["values"];
        Assert.AreEqual(true, values[0]);
        Assert.IsNull(values[1]);
        Assert.AreEqual(1.25, values[2]);
        Assert.AreEqual("", values[3]);
        Assert.IsTrue(DependencyFreeJson.Serialize(payload).EndsWith("\r\n", StringComparison.Ordinal));
    }
    [TestMethod]
    public void CapturedValuesAreDetachedAndPreserveExactNestedNumbers()
    {
        const decimal precise = 1.2345678901234567890123456789m;
        var input = new Dictionary<string, object> { ["precise"] = precise, ["maximum"] = ulong.MaxValue };
        object captured = DependencyFreeJson.CaptureValue(input, depth: 1);
        input["precise"] = 0;
        string json = DependencyFreeJson.SerializeCompact(new Dictionary<string, object> { ["value"] = captured });
        using var document = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual(precise, document.RootElement.GetProperty("value").GetProperty("precise").GetDecimal());
        Assert.AreEqual(ulong.MaxValue, document.RootElement.GetProperty("value").GetProperty("maximum").GetUInt64());
        Assert.ThrowsExactly<InvalidDataException>(() => DependencyFreeJson.CaptureValue(double.NaN, depth: 1));
    }
    [TestMethod]
    public void CaptureReservesItsEnclosingObjectsAndCannotBypassTheDepthLimit()
    {
        object nested = 1;
        for (int i = 0; i < DependencyFreeJson.MaximumDepth; i++) nested = new[] { nested };
        DependencyFreeJson.Parse(DependencyFreeJson.SerializeCompact(nested));
        Assert.ThrowsExactly<InvalidDataException>(() => DependencyFreeJson.CaptureValue(nested, depth: 2));
        object captured = DependencyFreeJson.CaptureValue(new[] { 1 }, depth: 1);
        Assert.ThrowsExactly<InvalidDataException>(() => DependencyFreeJson.SerializeCompact(
            new Dictionary<string, object> { ["outer"] = new Dictionary<string, object> { ["inner"] = captured } }));
    }
    [TestMethod]
    [DataRow("{\"x\":1,\"x\":2}")]
    [DataRow("{\"x\":1,}")]
    [DataRow("[1,]")]
    [DataRow("01")]
    [DataRow("1e999")]
    [DataRow("true false")]
    [DataRow("\"bad\ntext\"")]
    [DataRow("{")]
    [DataRow("[")]
    [DataRow("")]
    public void InvalidInputIsRejected(string input) =>
        Assert.ThrowsExactly<InvalidDataException>(() => DependencyFreeJson.Parse(input));
    [TestMethod] public void TrailingCommaIsExplicitlyOptIn()
    {
        var parsed = (List<object>)DependencyFreeJson.Parse("[1,]", true);
        Assert.HasCount(1, parsed);
    }
    [TestMethod] public void DepthNonFiniteNumbersAndCyclesAreRejected()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => DependencyFreeJson.Parse(new string('[',70)+"0"+new string(']',70)));
        Assert.ThrowsExactly<InvalidDataException>(() => DependencyFreeJson.Serialize(double.NaN));
        Assert.ThrowsExactly<InvalidDataException>(() => DependencyFreeJson.Serialize(double.PositiveInfinity));
        var cycle = new List<object>(); cycle.Add(cycle);
        Assert.ThrowsExactly<InvalidDataException>(() => DependencyFreeJson.Serialize(cycle));
    }
}
