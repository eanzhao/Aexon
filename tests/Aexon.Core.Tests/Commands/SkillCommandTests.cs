using Aexon.Commands;
using Aexon.Core.Agents;
using Aexon.Core.Commands;
using Aexon.Core.Context;
using Aexon.Core.Permissions;
using Aexon.Core.Query;
using Aexon.Core.Skills;
using Aexon.Core.Tests.Runtime;
using Aexon.Core.Tools;

namespace Aexon.Core.Tests.Commands;

public sealed class SkillCommandTests
{
    [Fact]
    public async Task ExecuteAsync_SubmitsPromptThatLoadsMatchingSkill()
    {
        using var temp = new TempDirectory();
        using var bundle = CreateEngineBundle(temp.Root);
        string? capturedPrompt = null;
        var command = new SkillCommand(new Skill("commit", "Create commits", "body", "/tmp/commit.md"));
        var context = new CommandContext
        {
            WriteLine = _ => { },
            Tools = new ToolRegistry(),
            QueryEngine = bundle.Engine,
            SubmitPromptAsync = prompt =>
            {
                capturedPrompt = prompt;
                return Task.CompletedTask;
            },
            PermissionContext = bundle.PermissionContext,
            AgentTaskRuntime = new InMemoryAgentTaskRuntime(),
            Commands = [],
            CancellationToken = CancellationToken.None,
        };

        await command.ExecuteAsync("stage and commit the current changes", context);

        Assert.NotNull(capturedPrompt);
        Assert.Contains("SkillTool", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("name=\"commit\"", capturedPrompt, StringComparison.Ordinal);
        Assert.Contains("stage and commit the current changes", capturedPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsUnavailableWhenPromptSubmissionIsMissing()
    {
        using var temp = new TempDirectory();
        using var bundle = CreateEngineBundle(temp.Root);
        var lines = new List<string>();
        var command = new SkillCommand(new Skill("commit", "Create commits", "body", "/tmp/commit.md"));
        var context = new CommandContext
        {
            WriteLine = lines.Add,
            Tools = new ToolRegistry(),
            QueryEngine = bundle.Engine,
            PermissionContext = bundle.PermissionContext,
            AgentTaskRuntime = new InMemoryAgentTaskRuntime(),
            Commands = [],
            CancellationToken = CancellationToken.None,
        };

        await command.ExecuteAsync("", context);

        Assert.Contains("Skill commands are unavailable", Assert.Single(lines), StringComparison.Ordinal);
    }

    private static EngineBundle CreateEngineBundle(string workingDirectory)
    {
        var permissionContext = new PermissionContext
        {
            WorkingDirectory = workingDirectory,
        };
        var provider = new ContextProvider
        {
            WorkingDirectory = workingDirectory,
            PermissionContext = permissionContext,
        };

        var engine = TestSupport.CreateQueryEngine(
            TestSupport.CreateChatClient(new FakeAnthropicHandler()),
            new ToolRegistry(),
            provider,
            new DefaultPermissionChecker(),
            new QueryEngineConfig
            {
                EnableAutoCompact = false,
            });

        return new EngineBundle(engine, permissionContext);
    }

    private sealed record EngineBundle(
        QueryEngine Engine,
        PermissionContext PermissionContext) : IDisposable
    {
        public void Dispose() => Engine.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
