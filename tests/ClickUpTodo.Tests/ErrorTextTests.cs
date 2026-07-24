using ClickUpTodo.ClickUp;
using ClickUpTodo.Tui;

namespace ClickUpTodo.Tests;

/// <summary>
/// The shared status-line exception formatter both TUI hosts use (#346). A
/// <see cref="ClickUpApiException"/> carries a curated message; anything else falls back to the raw
/// <see cref="Exception.Message"/>.
/// </summary>
public sealed class ErrorTextTests
{
    [Fact]
    public void Short_ClickUpApiException_UsesItsCuratedMessage()
    {
        var ex = new ClickUpApiException(429, "getTasks", new InvalidOperationException("raw kiota detail"));

        var text = ErrorText.Short(ex);

        Assert.Equal(ex.Message, text);
        Assert.Contains("getTasks", text);
        Assert.DoesNotContain("raw kiota detail", text); // the inner exception's message is not surfaced
    }

    [Fact]
    public void Short_OtherException_UsesRawMessage()
    {
        var ex = new TimeoutException("network timed out");

        Assert.Equal("network timed out", ErrorText.Short(ex));
    }
}
