using System.Text;
using TraceMQ.Api.Correlation;

namespace TraceMQ.Tests;

public class CorrelationExtractorTests
{
    private const string Guid1 = "7347ba7c-d1e5-41c4-be7e-c1b19e3a0b0c";

    private static string? Extract(string json, params string[] paths) =>
        new CorrelationExtractor(paths).Extract(Encoding.UTF8.GetBytes(json));

    [Theory]
    // the ordinary case
    [InlineData("""{"trigger":{"uid":"KEY"}}""", "KEY")]
    // the envelope from the real payloads, key not first and not last
    [InlineData("""{"timestamp":1,"trigger":{"uid":"KEY","topic":"a/b"},"actionStatus":{"Status":0}}""", "KEY")]
    // a sibling subtree with the same leaf name must not be picked up
    [InlineData("""{"other":{"uid":"WRONG"},"trigger":{"uid":"KEY"}}""", "KEY")]
    // a leaf of the right name at the wrong depth must not match
    [InlineData("""{"uid":"WRONG","trigger":{"uid":"KEY"}}""", "KEY")]
    // deeper nesting between the segments
    [InlineData("""{"trigger":{"meta":{"x":1},"uid":"KEY"}}""", "KEY")]
    public void FindsTheKeyAtTheConfiguredPath(string json, string expected)
    {
        Assert.Equal(expected, Extract(json, "trigger.uid"));
    }

    [Theory]
    [InlineData("")]                                        // empty payload
    [InlineData("   ")]                                     // whitespace
    [InlineData("not json at all")]                         // plain text
    [InlineData("{\"trigger\":{\"uid\":")]                  // truncated mid-value
    [InlineData("""{"trigger":{}}""")]                      // path present, leaf absent
    [InlineData("""{"other":1}""")]                         // path absent entirely
    [InlineData("""{"trigger":"a string"}""")]              // path continues, value does not
    [InlineData("""{"trigger":{"uid":null}}""")]            // explicit null
    [InlineData("""{"trigger":{"uid":{"nested":1}}}""")]    // resolves to an object
    [InlineData("""{"trigger":{"uid":["a"]}}""")]           // resolves to an array
    [InlineData("""["trigger","uid"]""")]                   // array at the root
    [InlineData(""""a bare string"""")]                     // scalar at the root
    [InlineData("123")]                                     // number at the root
    public void YieldsNoKeyAndDoesNotThrow(string json)
    {
        Assert.Null(Extract(json, "trigger.uid"));
    }

    [Fact]
    public void TruncationAfterTheKeyStillYieldsTheKey()
    {
        // The scan is best-effort and forward-only: the key was unambiguously present at the
        // configured path before the payload ran out. A publisher emitting broken JSON is
        // exactly the case someone is trying to trace, so keeping the message correlated is
        // worth more than being strict about the bytes after it. Truncation BEFORE the path
        // still yields nothing, which the table above covers.
        Assert.Equal("KEY", Extract("""{"trigger":{"uid":"KEY"}""", "trigger.uid"));
    }

    [Fact]
    public void NonUtf8BytesYieldNoKey()
    {
        var extractor = new CorrelationExtractor(["trigger.uid"]);

        Assert.Null(extractor.Extract(new byte[] { 0x00, 0xFF, 0x00, 0xFF }));
    }

    [Fact]
    public void TriesPathsInOrderAndTakesTheFirstThatResolves()
    {
        const string json = """{"header":{"correlationId":"SECOND"}}""";

        Assert.Equal("SECOND", Extract(json, "trigger.uid", "header.correlationId"));
    }

    [Fact]
    public void AnEarlierPathWinsOverALaterOne()
    {
        const string json = """{"trigger":{"uid":"FIRST"},"header":{"correlationId":"SECOND"}}""";

        Assert.Equal("FIRST", Extract(json, "trigger.uid", "header.correlationId"));
    }

    [Fact]
    public void FallsThroughAPathThatResolvesToNull()
    {
        const string json = """{"trigger":{"uid":null},"header":{"correlationId":"SECOND"}}""";

        Assert.Equal("SECOND", Extract(json, "trigger.uid", "header.correlationId"));
    }

    [Fact]
    public void KeepsTheCaseItWasGiven()
    {
        // The column is COLLATE NOCASE, so search is case-insensitive either way; the stored
        // value should still be what the service actually published.
        var upper = Guid1.ToUpperInvariant();

        Assert.Equal(upper, Extract($$$"""{"trigger":{"uid":"{{{upper}}}"}}""", "trigger.uid"));
    }

    [Theory]
    [InlineData("trigger.uid")]
    [InlineData("$.trigger.uid")]
    [InlineData("  trigger . uid  ")]
    public void AcceptsThePathInTheFormsTheUiMightProduce(string path)
    {
        Assert.Equal("KEY", Extract("""{"trigger":{"uid":"KEY"}}""", path));
    }

    [Fact]
    public void ASingleSegmentPathWorks()
    {
        Assert.Equal("KEY", Extract("""{"uid":"KEY"}""", "uid"));
    }

    [Fact]
    public void NumbersAndBooleansBecomeText()
    {
        Assert.Equal("230", Extract("""{"sequence":230}""", "sequence"));
        Assert.Equal("true", Extract("""{"flag":true}""", "flag"));
    }

    [Fact]
    public void NoConfiguredPathsMeansNoKey()
    {
        Assert.Null(Extract("""{"trigger":{"uid":"KEY"}}"""));
        Assert.Null(Extract("""{"trigger":{"uid":"KEY"}}""", "", "   "));
    }

    [Fact]
    public void CapsTheNumberOfPaths()
    {
        var many = Enumerable.Range(0, 20).Select(i => $"p{i}.uid");

        Assert.Equal(CorrelationExtractor.MaxPaths, new CorrelationExtractor(many).Paths.Count);
    }

    [Fact]
    public void RefusesAPathologicallyLargePayload()
    {
        var padding = new string('x', CorrelationExtractor.MaxPayloadBytes);
        var json = $$"""{"trigger":{"uid":"KEY"},"pad":"{{padding}}"}""";

        Assert.Null(Extract(json, "trigger.uid"));
    }

    [Fact]
    public void HandlesAKeyBuriedBehindALargeSiblingSubtree()
    {
        var items = string.Join(",", Enumerable.Range(0, 2000).Select(i => $$"""{"i":{{i}},"t":"line {{i}}"}"""));
        var json = $$$"""{"marking":{"lines":[{{{items}}}]},"trigger":{"uid":"KEY"}}""";

        Assert.Equal("KEY", Extract(json, "trigger.uid"));
    }
}
