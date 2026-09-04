using Aexon.Core.Agents;

namespace Aexon.Core.Commands;

/// <summary>
/// Defines a slash command.
/// </summary>
public interface ICommand
{
    string Name { get; }
    string Description { get; }
    string[] Aliases => [];

    /// <summary>
    /// Gets a value indicating whether the command requires arguments.
    /// </summary>
    bool RequiresArgs => false;

    /// <summary>
    /// Executes the command.
    /// </summary>
    Task ExecuteAsync(string args, CommandContext context);
}

/// <summary>
/// Provides the runtime services needed by a command.
/// </summary>
public class CommandContext
{
    public required Action<string> WriteLine { get; init; }
    public required Tools.ToolRegistry Tools { get; init; }
    public required Query.QueryEngine QueryEngine { get; init; }
    public Func<string, Task>? SubmitPromptAsync { get; init; }
    public Query.AiProvider AiProvider { get; init; } = Query.AiProvider.Anthropic;
    public required Permissions.PermissionContext PermissionContext { get; init; }
    public required IAgentTaskRuntime AgentTaskRuntime { get; init; }
    public AgentAutoResumeMode AgentAutoResumeMode { get; init; } = AgentAutoResumeMode.Queue;
    public AgentRuntimeOptions? AgentRuntimeOptions { get; init; }
    public IAgentTeamRuntime? AgentTeamRuntime { get; init; }
    public IAgentMessageRuntime? AgentMessageRuntime { get; init; }
    public IAgentMessageActivationRuntime? AgentMessageActivationRuntime { get; init; }
    public required IReadOnlyList<ICommand> Commands { get; init; }
    public Func<TimeSpan, CancellationToken, Task>? DelayAsync { get; init; }
    public Func<string?>? ReadInputLine { get; init; }
    public DateTimeOffset? SessionStartedAt { get; init; }
    public Func<int>? SessionTurnCountProvider { get; init; }
    public CancellationToken CancellationToken { get; init; }
    public Action? RequestExit { get; init; }
    public Action? RequestClear { get; init; }

    public AgentAutoResumeMode CurrentAgentAutoResumeMode =>
        AgentRuntimeOptions?.AutoResumeMode ?? AgentAutoResumeMode;

    public int CurrentSessionTurnCount => SessionTurnCountProvider?.Invoke() ?? 0;
}

/// <summary>
/// Registers and resolves slash commands.
/// </summary>
public class CommandRegistry
{
    private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ICommand command)
    {
        _commands[command.Name] = command;
        foreach (var alias in command.Aliases)
            _commands[alias] = command;
    }

    public ICommand? Get(string name)
    {
        name = name.TrimStart('/');
        _commands.TryGetValue(name, out var command);
        return command;
    }

    public IReadOnlyList<ICommand> GetAll() =>
        _commands.Values.Distinct().OrderBy(c => c.Name).ToList();

    public bool IsCommand(string input) =>
        input.StartsWith('/') && Get(input.Split(' ')[0].TrimStart('/')) != null;
}
