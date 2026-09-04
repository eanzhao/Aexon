using System.Text.Json;
using Aexon.Core.Permissions;
using Aexon.Core.Skills;
using Aexon.Core.Tests.Runtime;
using Aexon.Core.Tools;
using Aexon.Tools;

namespace Aexon.Core.Tests.Tools;

public sealed class SkillToolTests
{
    [Fact]
    public async Task SkillTool_ExecuteAsync_ReturnsSkillBodyWhenFound()
    {
        using var temp = new TempDirectory();
        var home = temp.CreateDirectory("home");
        var repo = temp.CreateDirectory("repo");
        temp.WriteFile("repo/.aexon/skills/commit.md", """
        ---
        name: commit
        description: Commit changes cleanly
        ---
        Run git status first.
        """);
        var tool = new SkillTool(new SkillLoader(home), repo);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { name = "commit" }),
            CreateContext(repo));

        Assert.False(result.IsError);
        Assert.Equal("Run git status first.", result.Data.Trim());
        Assert.True(tool.IsReadOnly(default));
        Assert.True(tool.IsConcurrencySafe(default));
        Assert.Equal(
            "Loading skill commit",
            tool.GetActivityDescription(JsonSerializer.SerializeToElement(new { name = "commit" })));
    }

    [Fact]
    public async Task SkillTool_ExecuteAsync_ReturnsAvailableSkillNamesWhenMissing()
    {
        using var temp = new TempDirectory();
        var home = temp.CreateDirectory("home");
        var repo = temp.CreateDirectory("repo");
        temp.WriteFile("home/.aexon/skills/commit.md", """
        ---
        name: commit
        description: Commit changes cleanly
        ---
        Run git status first.
        """);
        var tool = new SkillTool(new SkillLoader(home), repo);

        var result = await tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { name = "review" }),
            CreateContext(repo));

        Assert.True(result.IsError);
        Assert.Contains("review", result.Data, StringComparison.Ordinal);
        Assert.Contains("Available skills: commit", result.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SkillTool_GetDescriptionAsync_ListsAvailableSkills()
    {
        using var temp = new TempDirectory();
        var home = temp.CreateDirectory("home");
        var repo = temp.CreateDirectory("repo");
        temp.WriteFile("home/.aexon/skills/commit.md", """
        ---
        name: commit
        description: Commit changes cleanly
        ---
        body
        """);
        temp.WriteFile("home/.claude/skills/review.md", """
        ---
        name: review
        description: Review the diff
        ---
        body
        """);
        var tool = new SkillTool(new SkillLoader(home), repo);

        var description = await tool.GetDescriptionAsync();

        Assert.Contains("Available skills:", description, StringComparison.Ordinal);
        Assert.Contains("- commit: Commit changes cleanly", description, StringComparison.Ordinal);
        Assert.Contains("- review: Review the diff", description, StringComparison.Ordinal);
    }

    private static ToolExecutionContext CreateContext(string workingDirectory) =>
        new()
        {
            WorkingDirectory = workingDirectory,
            PermissionContext = new PermissionContext
            {
                WorkingDirectory = workingDirectory,
            },
            Tools = [],
            Messages = [],
            CancellationToken = CancellationToken.None,
        };
}
