using Aexon.Core.Skills;
using Aexon.Core.Tests.Runtime;

namespace Aexon.Core.Tests.Skills;

public sealed class SkillLoaderTests
{
    [Fact]
    public void Load_ParsesFrontmatterBodyAndSourcePath()
    {
        using var temp = new TempDirectory();
        var home = temp.CreateDirectory("home");
        var repo = temp.CreateDirectory("repo");
        var skillPath = temp.WriteFile("home/.aexon/skills/commit.md", """
        ---
        name: commit
        description: Create well-formatted commits
        ---
        When creating commits, follow these steps:
        1. Run git status
        """);

        var loader = new SkillLoader(home);

        var skills = loader.Load(repo);

        var skill = Assert.Single(skills).Value;
        Assert.Equal("commit", skill.Name);
        Assert.Equal("Create well-formatted commits", skill.Description);
        Assert.Equal(
            """
            When creating commits, follow these steps:
            1. Run git status
            """,
            skill.Body.TrimEnd());
        Assert.Equal(Path.GetFullPath(skillPath), skill.SourcePath);
    }

    [Fact]
    public void Load_ProjectDirectoryWinsOverUserDirectoryOnNameCollision()
    {
        using var temp = new TempDirectory();
        var home = temp.CreateDirectory("home");
        var repo = temp.CreateDirectory("repo");
        var projectSkillPath = temp.WriteFile("repo/.aexon/skills/commit.md", """
        ---
        name: commit
        description: Project commit skill
        ---
        project body
        """);
        temp.WriteFile("home/.aexon/skills/commit.md", """
        ---
        name: commit
        description: User commit skill
        ---
        user body
        """);

        var loader = new SkillLoader(home);

        var skills = loader.Load(repo);

        var skill = Assert.Single(skills).Value;
        Assert.Equal("Project commit skill", skill.Description);
        Assert.Equal("project body", skill.Body.Trim());
        Assert.Equal(Path.GetFullPath(projectSkillPath), skill.SourcePath);
    }

    [Fact]
    public void Load_SkipsBadOrMissingFrontmatterGracefully()
    {
        using var temp = new TempDirectory();
        var home = temp.CreateDirectory("home");
        var repo = temp.CreateDirectory("repo");
        temp.WriteFile("home/.aexon/skills/bad.md", """
        ---
        hello
        ---
        ignored
        """);
        temp.WriteFile("home/.aexon/skills/missing.md", "plain markdown without frontmatter");
        temp.WriteFile("home/.aexon/skills/good.md", """
        ---
        name: review
        description: Review changes
        ---
        review body
        """);

        var loader = new SkillLoader(home);

        var skills = loader.Load(repo);

        var skill = Assert.Single(skills).Value;
        Assert.Equal("review", skill.Name);
    }

    [Fact]
    public void Load_ReturnsEmptyDictionaryWhenSkillDirectoriesAreMissing()
    {
        using var temp = new TempDirectory();
        var home = temp.CreateDirectory("home");
        var repo = temp.CreateDirectory("repo");
        var loader = new SkillLoader(home);

        var skills = loader.Load(repo);

        Assert.Empty(skills);
    }
}
