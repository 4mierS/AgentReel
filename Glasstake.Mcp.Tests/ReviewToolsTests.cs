using ModelContextProtocol.Protocol;
using Xunit;

namespace Glasstake.Mcp.Tests;

public class ReviewToolsTests
{
    [Theory]
    [InlineData("claude-code", null, "Claude")]
    [InlineData("claude-ai", "Claude Desktop", "Claude")]
    [InlineData("cursor-vscode", "Cursor", "Cursor")]
    [InlineData("my-agent", null, "my-agent")]
    public void The_agent_is_named_as_the_user_knows_it(string name, string? title, string expected)
    {
        Assert.Equal(expected, ReviewTools.AgentName(new Implementation { Name = name, Title = title, Version = "1" }));
    }

    [Fact]
    public void An_unknown_client_has_no_name()
    {
        Assert.Null(ReviewTools.AgentName(null));
    }
}
