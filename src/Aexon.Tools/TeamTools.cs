using System.Text.Json;
using System.Text.Json.Serialization;
using Aexon.Core.Agents;
using Aexon.Core.Permissions;
using Aexon.Core.Tools;

namespace Aexon.Tools;

/// <summary>
/// Represents the input payload for team creation.
/// </summary>
public sealed class TeamCreateToolInput
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("lead")]
    public string? Lead { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("members")]
    public string[]? Members { get; set; }
}

/// <summary>
/// Represents the input payload for team inspection.
/// </summary>
public sealed class TeamStatusToolInput
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("include_members")]
    public bool IncludeMembers { get; set; } = true;
}

/// <summary>
/// Represents the input payload for team dissolution.
/// </summary>
public sealed class TeamDissolveToolInput
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>
/// Creates a team runtime record.
/// </summary>
public sealed class TeamCreateTool : ITool
{
    private readonly IAgentTeamRuntime _runtime;

    public TeamCreateTool(IAgentTeamRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    public string Name => "TeamCreate";

    public Task<string> GetDescriptionAsync() =>
        Task.FromResult("Create a team record and seed its roster.");

    public JsonElement GetInputSchema() =>
        JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "name": { "type": "string" },
            "lead": { "type": "string" },
            "description": { "type": "string" },
            "members": {
              "type": "array",
              "items": { "type": "string" }
            }
          },
          "required": ["name"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public Task<string> GetPromptAsync(ToolPromptContext context) =>
        Task.FromResult("Create a new team with an explicit name and optional roster seed.");

    public Task<ValidationResult> ValidateInputAsync(JsonElement input, ToolExecutionContext context)
    {
        var parsed = JsonSerializer.Deserialize<TeamCreateToolInput>(input);
        return Task.FromResult(
            parsed == null || string.IsNullOrWhiteSpace(parsed.Name)
                ? ValidationResult.Invalid("name is required.")
                : ValidationResult.Valid());
    }

    public bool IsEnabled() => true;
    public bool IsReadOnly(JsonElement input) => false;
    public bool IsConcurrencySafe(JsonElement input) => false;
    public string GetUserFacingName(JsonElement? input = null) => "Create team";
    public string? GetActivityDescription(JsonElement? input) => "Creating team";

    public Task<PermissionResult> CheckPermissionsAsync(JsonElement input, ToolExecutionContext context) =>
        Task.FromResult(PermissionResult.Allow());

    public Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = JsonSerializer.Deserialize<TeamCreateToolInput>(input);
        if (parsed == null || string.IsNullOrWhiteSpace(parsed.Name))
            return Task.FromResult(ToolResult.Error("name is required."));

        try
        {
            var team = _runtime.CreateTeam(
                parsed.Name,
                description: parsed.Description,
                leadName: parsed.Lead);

            foreach (var member in parsed.Members ?? [])
            {
                if (string.IsNullOrWhiteSpace(member) ||
                    string.Equals(member.Trim(), parsed.Lead?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _runtime.AddMember(team.Id, member);
            }

            team = _runtime.GetTeam(team.Id) ?? team;

            return Task.FromResult(ToolResult.Success(TeamFormatter.FormatCreateResult(team)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error(ex.Message));
        }
    }
}

/// <summary>
/// Inspects team runtime state.
/// </summary>
public sealed class TeamStatusTool : ITool
{
    private readonly IAgentTeamRuntime _runtime;

    public TeamStatusTool(IAgentTeamRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    public string Name => "TeamStatus";

    public Task<string> GetDescriptionAsync() =>
        Task.FromResult("Show team overview or inspect a single team.");

    public JsonElement GetInputSchema() =>
        JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "include_members": { "type": "boolean" }
          },
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public Task<string> GetPromptAsync(ToolPromptContext context) =>
        Task.FromResult("Inspect teams and their rosters.");

    public Task<ValidationResult> ValidateInputAsync(JsonElement input, ToolExecutionContext context) =>
        Task.FromResult(ValidationResult.Valid());

    public bool IsEnabled() => true;
    public bool IsReadOnly(JsonElement input) => true;
    public bool IsConcurrencySafe(JsonElement input) => true;
    public string GetUserFacingName(JsonElement? input = null) => "Team status";
    public string? GetActivityDescription(JsonElement? input) => "Inspecting team";

    public Task<PermissionResult> CheckPermissionsAsync(JsonElement input, ToolExecutionContext context) =>
        Task.FromResult(PermissionResult.Allow());

    public Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = JsonSerializer.Deserialize<TeamStatusToolInput>(input) ?? new TeamStatusToolInput();
        if (string.IsNullOrWhiteSpace(parsed.Id))
            return Task.FromResult(ToolResult.Success(TeamFormatter.FormatOverview(_runtime)));

        var team = AgentTeamLookup.ResolveTeam(_runtime, parsed.Id.Trim());
        return Task.FromResult(
            team == null
                ? ToolResult.Error($"No team matched '{parsed.Id.Trim()}'.")
                : ToolResult.Success(parsed.IncludeMembers
                    ? TeamFormatter.FormatDetails(team)
                    : TeamFormatter.FormatSummary(team)));
    }
}

/// <summary>
/// Dissolves a team runtime record.
/// </summary>
public sealed class TeamDissolveTool : ITool
{
    private readonly IAgentTeamRuntime _runtime;

    public TeamDissolveTool(IAgentTeamRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    public string Name => "TeamDissolve";

    public Task<string> GetDescriptionAsync() =>
        Task.FromResult("Dissolve an existing team.");

    public JsonElement GetInputSchema() =>
        JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "id": { "type": "string" },
            "reason": { "type": "string" }
          },
          "required": ["id"],
          "additionalProperties": false
        }
        """).RootElement.Clone();

    public Task<string> GetPromptAsync(ToolPromptContext context) =>
        Task.FromResult("Dissolve a team when it is no longer needed.");

    public Task<ValidationResult> ValidateInputAsync(JsonElement input, ToolExecutionContext context)
    {
        var parsed = JsonSerializer.Deserialize<TeamDissolveToolInput>(input);
        return Task.FromResult(
            parsed == null || string.IsNullOrWhiteSpace(parsed.Id)
                ? ValidationResult.Invalid("id is required.")
                : ValidationResult.Valid());
    }

    public bool IsEnabled() => true;
    public bool IsReadOnly(JsonElement input) => false;
    public bool IsConcurrencySafe(JsonElement input) => false;
    public string GetUserFacingName(JsonElement? input = null) => "Dissolve team";
    public string? GetActivityDescription(JsonElement? input) => "Dissolving team";

    public Task<PermissionResult> CheckPermissionsAsync(JsonElement input, ToolExecutionContext context) =>
        Task.FromResult(PermissionResult.Allow());

    public Task<ToolResult> ExecuteAsync(
        JsonElement input,
        ToolExecutionContext context,
        IProgress<ToolProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var parsed = JsonSerializer.Deserialize<TeamDissolveToolInput>(input);
        if (parsed == null || string.IsNullOrWhiteSpace(parsed.Id))
            return Task.FromResult(ToolResult.Error("id is required."));

        var team = AgentTeamLookup.ResolveTeam(_runtime, parsed.Id.Trim());
        if (team == null)
            return Task.FromResult(ToolResult.Error($"No team matched '{parsed.Id.Trim()}'."));

        if (!_runtime.DeleteTeam(team.Id))
            return Task.FromResult(ToolResult.Error($"No team matched '{parsed.Id.Trim()}'."));

        return Task.FromResult(ToolResult.Success(TeamFormatter.FormatDissolveResult(team, parsed.Reason)));
    }
}
