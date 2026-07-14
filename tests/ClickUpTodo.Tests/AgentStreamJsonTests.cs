using ClickUpTodo.Agent;

namespace ClickUpTodo.Tests;

/// <summary>
/// Pins the pure stream-json line parser behind the streaming one-off run (#187): which events yield
/// display lines, the tool-use activity marker, error-result surfacing, and the never-throw robustness
/// on blank/garbled/unknown lines. No real <c>claude</c> is spawned — the parser is a pure function over
/// a single JSONL line.
/// </summary>
public sealed class AgentStreamJsonTests
{
    [Fact]
    public void AssistantTextBlock_YieldsTheText()
    {
        const string line = """{"type":"assistant","message":{"content":[{"type":"text","text":"Hello, triaging now."}]}}""";
        Assert.Equal(["Hello, triaging now."], AgentStreamJson.ParseLine(line));
    }

    [Fact]
    public void AssistantToolUse_YieldsACompactMarker()
    {
        const string line = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]}}""";
        Assert.Equal([AgentStreamJson.ToolMarkerPrefix + "Bash"], AgentStreamJson.ParseLine(line));
    }

    [Fact]
    public void AssistantMultiBlock_YieldsEachBlockInOrder()
    {
        const string line = """
            {"type":"assistant","message":{"content":[
                {"type":"text","text":"Let me check."},
                {"type":"tool_use","id":"t1","name":"Read","input":{}},
                {"type":"text","text":"Found it."}
            ]}}
            """;
        Assert.Equal(
            ["Let me check.", AgentStreamJson.ToolMarkerPrefix + "Read", "Found it."],
            AgentStreamJson.ParseLine(line));
    }

    [Fact]
    public void AssistantEmptyTextBlock_IsSkipped()
    {
        const string line = """{"type":"assistant","message":{"content":[{"type":"text","text":""}]}}""";
        Assert.Empty(AgentStreamJson.ParseLine(line));
    }

    [Fact]
    public void UnicodeText_IsPreserved()
    {
        const string line = """{"type":"assistant","message":{"content":[{"type":"text","text":"café — 90% ✅"}]}}""";
        Assert.Equal(["café — 90% ✅"], AgentStreamJson.ParseLine(line));
    }

    [Fact]
    public void MultilineText_IsPreservedVerbatim()
    {
        const string line = """{"type":"assistant","message":{"content":[{"type":"text","text":"line one\nline two"}]}}""";
        Assert.Equal(["line one\nline two"], AgentStreamJson.ParseLine(line));
    }

    [Fact]
    public void SystemInitEvent_YieldsNothing()
    {
        const string line = """{"type":"system","subtype":"init","session_id":"s1","tools":["Bash"],"model":"claude"}""";
        Assert.Empty(AgentStreamJson.ParseLine(line));
    }

    [Fact]
    public void UserToolResultEvent_YieldsNothing()
    {
        const string line = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"lots of output"}]}}""";
        Assert.Empty(AgentStreamJson.ParseLine(line));
    }

    [Fact]
    public void SuccessResultEvent_YieldsNothing_SoItDoesNotDuplicateTheAssistantText()
    {
        const string line = """{"type":"result","subtype":"success","is_error":false,"result":"Final answer.","duration_ms":1200}""";
        Assert.Empty(AgentStreamJson.ParseLine(line));
    }

    [Fact]
    public void ErrorResultEvent_SurfacesTheErrorDetail()
    {
        const string line = """{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Model refused the request."}""";
        Assert.Equal(["Model refused the request."], AgentStreamJson.ParseLine(line));
    }

    [Fact]
    public void ErrorResultEvent_WithoutResultText_YieldsNothing()
    {
        const string line = """{"type":"result","subtype":"error","is_error":true}""";
        Assert.Empty(AgentStreamJson.ParseLine(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankLine_YieldsNothing(string? line) => Assert.Empty(AgentStreamJson.ParseLine(line));

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ broken json")]
    [InlineData("{\"type\":\"assistant\"")] // truncated / partial line
    public void MalformedLine_YieldsNothing_NeverThrows(string line) =>
        Assert.Empty(AgentStreamJson.ParseLine(line));

    [Fact]
    public void NonObjectJson_YieldsNothing()
    {
        Assert.Empty(AgentStreamJson.ParseLine("[1,2,3]"));
        Assert.Empty(AgentStreamJson.ParseLine("\"a string\""));
        Assert.Empty(AgentStreamJson.ParseLine("42"));
    }

    [Fact]
    public void UnknownEventType_YieldsNothing()
    {
        const string line = """{"type":"stream_event","event":{"foo":"bar"}}""";
        Assert.Empty(AgentStreamJson.ParseLine(line));
    }

    [Fact]
    public void EventWithNoType_YieldsNothing() =>
        Assert.Empty(AgentStreamJson.ParseLine("""{"message":{"content":[{"type":"text","text":"x"}]}}"""));

    [Fact]
    public void AssistantWithNonArrayContent_YieldsNothing() =>
        Assert.Empty(AgentStreamJson.ParseLine("""{"type":"assistant","message":{"content":"not an array"}}"""));

    [Fact]
    public void AssistantWithNoMessage_YieldsNothing() =>
        Assert.Empty(AgentStreamJson.ParseLine("""{"type":"assistant"}"""));

    [Fact]
    public void ToolUseWithoutName_IsSkipped() =>
        Assert.Empty(AgentStreamJson.ParseLine("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1"}]}}"""));
}
