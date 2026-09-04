using Aexon.Core.Context;
using Aexon.Core.Query;
using Aexon.Core.Skills;
using Aexon.Core.Tests.Runtime;

namespace Aexon.Core.Tests.Runtime;

public sealed class SkillContextProviderTests
{
    [Fact]
    public async Task BuildSystemPromptAsync_IncludesSkillSectionAndCapsEntriesAtTwenty()
    {
        using var temp = new TempDirectory();
        var home = temp.CreateDirectory("home");
        var repo = temp.CreateDirectory("repo");
        for (var i = 1; i <= 21; i++)
        {
            temp.WriteFile($"home/.aexon/skills/skill{i:D2}.md", $"""
            ---
            name: skill{i:D2}
            description: Description {i:D2}
            ---
            body {i:D2}
            """);
        }

        var provider = new ContextProvider
        {
            WorkingDirectory = repo,
            SkillLoader = new SkillLoader(home),
        };

        var prompt = await provider.BuildSystemPromptAsync([], new QueryEngineConfig());
        var skillLines = prompt.Split('\n')
            .Where(line => line.StartsWith("Available skills: ", StringComparison.Ordinal))
            .ToArray();

        Assert.Contains("# Skills", prompt, StringComparison.Ordinal);
        Assert.Equal(20, skillLines.Length);
        Assert.Contains("Available skills: skill01 — Description 01", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("skill21", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildSystemPromptAsync_OmitsSkillSectionWhenNoSkillsExist()
    {
        using var temp = new TempDirectory();
        var home = temp.CreateDirectory("home");
        var repo = temp.CreateDirectory("repo");
        var provider = new ContextProvider
        {
            WorkingDirectory = repo,
            SkillLoader = new SkillLoader(home),
        };

        var prompt = await provider.BuildSystemPromptAsync([], new QueryEngineConfig());

        Assert.DoesNotContain("# Skills", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("Available skills:", prompt, StringComparison.Ordinal);
    }
}
