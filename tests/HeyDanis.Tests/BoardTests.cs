using HeyDanis.Api;
using HeyDanis.Companion;
using HeyDanis.Companion.Board;
using Xunit;

namespace HeyDanis.Tests;

public class BoardTests
{
    [Fact]
    public void ExtractHtml_StripsCodeFencesAndChatter()
    {
        var output = "Hier ist die Tafel:\n```html\n<!DOCTYPE html><html><body>x</body></html>\n```\nViel Spaß!";

        Assert.Equal("<!DOCTYPE html><html><body>x</body></html>", BoardPrompt.ExtractHtml(output));
    }

    [Fact]
    public void ExtractHtml_WithoutHtml_ThrowsUserFriendlyError()
    {
        var error = Assert.Throws<BoardGenerationException>(() => BoardPrompt.ExtractHtml("Sorry, geht nicht."));
        Assert.Contains("ohne HTML", error.Message);
    }

    [Fact]
    public void UserPrompt_ContainsTopicHistoryAndPreviousBoardForUpdates()
    {
        var request = new BoardRequest(
            "mach das größer",
            "Pythagoras-Dreieck größer zeichnen",
            UpdateExisting: true,
            PreviousHtml: "<!DOCTYPE html><html>alt</html>",
            History: new[] { new Message("erklär pythagoras", "klar, an der tafel.") },
            CursorScreen: null);

        var prompt = BoardPrompt.BuildUserPrompt(request, screenshotReference: null);

        Assert.Contains("Pythagoras-Dreieck größer zeichnen", prompt);
        Assert.Contains("erklär pythagoras", prompt);
        Assert.Contains("<!DOCTYPE html><html>alt</html>", prompt);
    }

    [Fact]
    public void CliResult_ReturnsTheAnswerText()
    {
        var stdout = """{"type":"result","subtype":"success","is_error":false,"result":"<!DOCTYPE html><html></html>"}""";

        Assert.Equal("<!DOCTYPE html><html></html>", ClaudeCodeCli.InterpretJsonResult(stdout, "", 0));
    }

    [Fact]
    public void CliResult_LoginProblem_ExplainsHowToLogIn()
    {
        var stdout = """{"type":"result","is_error":true,"result":"Invalid API key · Please run /login"}""";

        var error = Assert.Throws<BoardGenerationException>(() => ClaudeCodeCli.InterpretJsonResult(stdout, "", 1));
        Assert.Contains("nicht angemeldet", error.Message);
    }

    [Fact]
    public void CliResult_BoardAboutAuthenticationErrors_IsNotMistakenForLoginProblem()
    {
        var stdout = """{"type":"result","is_error":false,"result":"<!DOCTYPE html><html>authentication error handling in REST APIs</html>"}""";

        Assert.Contains("authentication", ClaudeCodeCli.InterpretJsonResult(stdout, "", 0));
    }

    [Fact]
    public void BoardIntent_IsReadFromStructuredAnswer()
    {
        var json = """{"spokenText":"moment, ich zeichne dir das an die tafel.","showBoard":true,"boardTopic":"Kreuzprodukt in 3D","boardMode":"update","pointIntents":[{"kind":"none","screen":1}]}""";

        var intent = CompanionManager.ParseBoardIntent(json);

        Assert.True(intent.Show);
        Assert.True(intent.Update);
        Assert.Equal("Kreuzprodukt in 3D", intent.Topic);
        Assert.False(CompanionManager.ParseBoardIntent("""{"spokenText":"hi","pointIntents":[]}""").Show);
    }
}
