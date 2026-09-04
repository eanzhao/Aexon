using System.Text.Json;
using System.Text.Json.Serialization;
using Aexon.Core.Skills;
using Aexon.Core.Tools;

namespace Aexon.Tools;

public sealed class SkillToolInput
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public sealed class SkillTool : ITool
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SkillLoader _skillLoader;
    private readonly string _workingDirectory;

    public SkillTool()
        : this(new SkillLoader(), Environment.CurrentDirectory)
    {
    }

    public SkillTool(SkillLoader skillLoader, string workingDirectory)
    {
        _skillLoader = skillLoader ?? throw new ArgumentNullException(nameof(skillLoader));
        _workingDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory);
    }

    public string Name => "SkillTool";

    public string[] Aliases => ["Skill"];

    public Task<string> GetDescriptionAsync()
    {
        var skills = LoadSkills();
        if (skills.Count == 0)
            return Task.FromResult("Load a reusable Markdown skill by name. Available skills: (none).");

        var lines = skills.Values
            .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .Select(skill => $"- {skill.Name}: {skill.Description}");

        return Task.FromResult(
            "Load a reusable Markdown skill by name.\nAvailable skills:\n" +
            string.Join("\n", lines));
    }

    public JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "name": {
              "type": "string",
              "description": "The skill name to load"
            }
          },
          "required": ["name"],
          "additionalProperties": false
        }
        """).RootElement.Clone();
    }

    public Task<string> GetPromptAsync(ToolPromptContext context)
    {
        return Task.FromResult("""
            Load a reusable Markdown skill definition and return its body verbatim.

            Usage:
            - Pass the exact skill name in the name field
            - Use this when a slash command or the system prompt references an available skill
            - The result is the Markdown body only; follow the returned instructions in later turns
            """);
    }

    public Task<ValidationResult> ValidateInputAsync(JsonElement input, ToolExecutionContext context)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<SkillToolInput>(input, JsonOptions);
            return Task.FromResult(string.IsNullOrWhiteSpace(parsed?.Name)
                ? ValidationResult.Invalid("name is required.")
                : ValidationResult.Valid());
        }
        catch (JsonException ex)
        {
            return Task.FromResult(ValidationResult.Invalid(ex.Message));
        }
    }

    public bool IsReadOnly(JsonElement input) => true;

    public bool IsConcurrencySafe(JsonElement input) => true;

    public string? GetActivityDescription(JsonElement? input)
    {
        if (input?.TryGetProperty("name", out var nameProperty) != true)
            return "Loading skill";

        var name = nameProperty.GetString();
        return string.IsNullOrWhiteSpace(name)
            ? "Loading skill"
            : $"Loading skill {name}";
    }

    public Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = JsonSerializer.Deserialize<SkillToolInput>(input, JsonOptions);
        if (string.IsNullOrWhiteSpace(parsed?.Name))
            return Task.FromResult(ToolResult.Error("name is required."));

        var skills = LoadSkills();
        if (!skills.TryGetValue(parsed.Name.Trim(), out var skill))
        {
            return Task.FromResult(
                ToolResult.Error(
                    $"Skill '{parsed.Name.Trim()}' not found. Available skills: {FormatAvailableSkillNames(skills)}"));
        }

        return Task.FromResult(ToolResult.Success(skill.Body));
    }

    private Dictionary<string, Skill> LoadSkills() => _skillLoader.Load(_workingDirectory);

    private static string FormatAvailableSkillNames(IReadOnlyDictionary<string, Skill> skills) =>
        skills.Count == 0
            ? "(none)"
            : string.Join(
                ", ",
                skills.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
}
