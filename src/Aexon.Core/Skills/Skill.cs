namespace Aexon.Core.Skills;

public sealed record Skill(
    string Name,
    string Description,
    string Body,
    string SourcePath);

public sealed record SkillFrontmatter(
    string Name,
    string Description);
