using Aexon.Core.Markdown;

namespace Aexon.Core.Skills;

public sealed class SkillLoader
{
    private readonly string _homeDirectory;

    public SkillLoader(string? homeDirectory = null)
    {
        _homeDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(homeDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : homeDirectory);
    }

    public Dictionary<string, Skill> Load(string workingDirectory)
    {
        var skills = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in GetSearchDirectories(workingDirectory))
        {
            if (!Directory.Exists(directory))
                continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                continue;
            }

            foreach (var path in files)
            {
                if (TryLoadSkill(path, out var skill))
                    skills[skill.Name] = skill;
            }
        }

        return skills;
    }

    internal IReadOnlyList<string> GetSearchDirectories(string workingDirectory)
    {
        var fullWorkingDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory);

        return
        [
            Path.Combine(_homeDirectory, ".aexon", "skills"),
            Path.Combine(_homeDirectory, ".claude", "skills"),
            Path.Combine(fullWorkingDirectory, ".aexon", "skills"),
            Path.Combine(fullWorkingDirectory, ".claude", "skills"),
        ];
    }

    private static bool TryLoadSkill(string path, out Skill skill)
    {
        skill = null!;

        try
        {
            var markdown = File.ReadAllText(path);
            var parsed = FrontmatterParser.Parse(markdown);
            if (parsed.HadInvalidFrontmatter ||
                !TryParseFrontmatter(parsed.Frontmatter, out var frontmatter))
            {
                return false;
            }

            skill = new Skill(
                frontmatter.Name,
                frontmatter.Description,
                parsed.Content,
                Path.GetFullPath(path));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseFrontmatter(
        IReadOnlyDictionary<string, object?> frontmatter,
        out SkillFrontmatter skillFrontmatter)
    {
        skillFrontmatter = null!;

        var name = frontmatter.TryGetValue("name", out var rawName)
            ? Convert.ToString(rawName)?.Trim()
            : null;
        var description = frontmatter.TryGetValue("description", out var rawDescription)
            ? Convert.ToString(rawDescription)?.Trim()
            : null;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
            return false;

        skillFrontmatter = new SkillFrontmatter(name, description);
        return true;
    }
}
