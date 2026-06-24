using ClickUpTodo.Setup;

namespace ClickUpTodo.Tests;

public sealed class ListIdParsingTests
{
    [Theory]
    [InlineData("901401775377", "901401775377")]                                              // bare id
    [InlineData("  901401775377  ", "901401775377")]                                          // padded id
    [InlineData("https://odbm.clickup.com/9014107164/v/l/6-901401775377-1", "901401775377")]  // composite view segment
    [InlineData("https://app.clickup.com/9014107164/v/li/901401775377", "901401775377")]      // /v/li/ deep link
    [InlineData("https://app.clickup.com/9014107164/v/l/901401775377", "901401775377")]        // /v/l/ plain
    public void ExtractListId_ReturnsListIdNotWorkspaceId(string input, string expected)
    {
        Assert.Equal(expected, SetupWizard.ExtractListId(input));
    }
}
