using System.Text;
using TraceMQ.Api.Storage;

namespace TraceMQ.Tests;

public class PayloadPreviewTests
{
    private static string? Preview(string text) => PayloadPreview.From(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void KeepsAShortPayloadAsItIs()
    {
        Assert.Equal("""{"state":"RUNNING"}""", Preview("""{"state":"RUNNING"}"""));
    }

    [Fact]
    public void FlattensAPrettyPrintedPayloadOntoOneLine()
    {
        var pretty = "{\n  \"state\": \"RUNNING\",\n  \"speed\": 420\n}";

        Assert.Equal("""{ "state": "RUNNING", "speed": 420 }""", Preview(pretty));
    }

    [Fact]
    public void CollapsesRunsOfWhitespace()
    {
        Assert.Equal("a b", Preview("a      \t\n\n   b"));
    }

    [Fact]
    public void DropsControlCharactersRatherThanSmearingTheRow()
    {
        Assert.Equal("ab", Preview("ab"));
    }

    [Fact]
    public void TruncatesToTheCharacterLimit()
    {
        var preview = Preview(new string('x', 1_000));

        Assert.Equal(PayloadPreview.MaxChars, preview!.Length);
    }

    [Fact]
    public void HasNoPreviewForBinary()
    {
        Assert.Null(PayloadPreview.From([0x00, 0xFF, 0x00, 0xFF]));
    }

    [Fact]
    public void HasNoPreviewForNothing()
    {
        Assert.Null(PayloadPreview.From(null));
        Assert.Null(PayloadPreview.From([]));
        Assert.Null(Preview("   \n\t  "));
    }

    [Fact]
    public void SurvivesAHeadThatCutsAMultiByteCharacterInHalf()
    {
        // The head is a byte cut, so this is the normal case rather than an edge one: without
        // trimming the partial character a strict decode would reject the whole preview, and
        // every payload containing an accent would show as binary.
        var full = Encoding.UTF8.GetBytes("""{"site":"Odda Smelteverk a"}""".Replace("a\"}", "å\"}"));
        var cutMidCharacter = full[..^3];

        var preview = PayloadPreview.From(cutMidCharacter);

        Assert.NotNull(preview);
        Assert.StartsWith("""{"site":"Odda""", preview);
        Assert.DoesNotContain('�', preview);
    }

    [Fact]
    public void KeepsWholeMultiByteCharactersIntact()
    {
        var nordic = "åäö";

        Assert.Equal($$"""{"a":"{{nordic}}"}""", Preview($$"""{"a":"{{nordic}}"}"""));
    }
}
