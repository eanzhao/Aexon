using System.Reflection;
using System.Text.Json;
using Aexon.Core.Cron;
using Aexon.Core.Messages;
using Aexon.Core.Permissions;
using Aexon.Core.Tests.Runtime;
using Aexon.Core.Tools;
using Aexon.Tools;

namespace Aexon.Core.Tests.Tools;

public sealed class SleepAndScheduleWakeupToolTests
{
    [Fact]
    public async Task SleepTool_ValidateInputAsync_RejectsInvalidSeconds()
    {
        var tool = new SleepTool();
        var context = CreateContext();

        var missing = await tool.ValidateInputAsync(Json(new { }), context);
        var tooSmall = await tool.ValidateInputAsync(Json(new { seconds = 0 }), context);
        var tooLarge = await tool.ValidateInputAsync(Json(new { seconds = 3601 }), context);

        Assert.Equal("seconds is required.", missing.Message);
        Assert.Equal("seconds must be between 1 and 3600.", tooSmall.Message);
        Assert.Equal("seconds must be between 1 and 3600.", tooLarge.Message);
    }

    [Fact]
    public async Task SleepTool_ExecuteAsync_HonorsCancellation()
    {
        var tool = new SleepTool();
        var context = CreateContext();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await tool.ExecuteAsync(
                Json(new { seconds = 1 }),
                context,
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ScheduleWakeupTool_ValidateInputAsync_RejectsInvalidInput()
    {
        var runtime = new InMemoryCronRuntime();
        var tool = new ScheduleWakeupTool(runtime);
        var context = CreateContext(loopSessionId: "session-26");

        var missingDelay = await tool.ValidateInputAsync(
            Json(new { prompt = "check build", reason = "follow up" }),
            context);
        var invalidDelay = await tool.ValidateInputAsync(
            Json(new { delaySeconds = 30, prompt = "check build", reason = "follow up" }),
            context);
        var missingPrompt = await tool.ValidateInputAsync(
            Json(new { delaySeconds = 60, reason = "follow up" }),
            context);
        var missingReason = await tool.ValidateInputAsync(
            Json(new { delaySeconds = 60, prompt = "check build" }),
            context);

        Assert.Equal("delaySeconds is required.", missingDelay.Message);
        Assert.Equal("delaySeconds must be between 60 and 3600.", invalidDelay.Message);
        Assert.Equal("prompt is required.", missingPrompt.Message);
        Assert.Equal("reason is required.", missingReason.Message);
    }

    [Fact]
    public async Task ScheduleWakeupTool_ValidateInputAsync_RequiresActiveLoopSession()
    {
        var runtime = new InMemoryCronRuntime();
        var tool = new ScheduleWakeupTool(runtime);

        var invalid = await tool.ValidateInputAsync(
            Json(new
            {
                delaySeconds = 60,
                prompt = "check build",
                reason = "follow up",
            }),
            CreateContext());

        Assert.Equal("ScheduleWakeup requires an active loop session.", invalid.Message);
    }

    [Fact]
    public async Task ScheduleWakeupTool_ExecuteAsync_SchedulesAndFiresWakeup()
    {
        var journal = new RecordingJournal { SessionId = "loop-session-26" };
        var runtime = await PersistentCronRuntime.CreateAsync(journal);
        var emittedMessages = new List<ConversationMessage>();
        runtime.SetMessageSink((message, _) =>
        {
            emittedMessages.Add(message);
            return Task.CompletedTask;
        });

        var tool = new ScheduleWakeupTool(runtime);
        var context = CreateContext(loopSessionId: "loop-session-26");

        var result = await tool.ExecuteAsync(
            Json(new
            {
                delaySeconds = 60,
                prompt = "Check the build again.",
                reason = "CI should have finished by then",
            }),
            context);

        Assert.False(result.IsError);
        Assert.Contains("Scheduled wakeup", result.Data, StringComparison.Ordinal);

        var job = Assert.Single(runtime.ListJobs());
        Assert.Equal(CronJobKind.Wakeup, job.Kind);
        Assert.True(job.RunOnce);
        Assert.Equal("loop-session-26", job.SessionId);
        Assert.Equal("Check the build again.", job.Prompt);
        Assert.Equal("CI should have finished by then", job.Description);

        using var temp = new TempDirectory();
        await using var scheduler = new CronScheduler(runtime, temp.Root);
        await InvokeInstanceTaskAsync(scheduler, "ExecuteJobAsync", job, CancellationToken.None);

        var history = Assert.Single(runtime.ListHistory(job.Id));
        Assert.True(history.Success);
        Assert.Equal(job.Id, history.JobId);
        Assert.Equal("Check the build again.", history.Output);

        var updatedJob = runtime.GetJob(job.Id);
        Assert.NotNull(updatedJob);
        Assert.False(updatedJob!.Enabled);
        Assert.Null(updatedJob.NextRunAt);

        var scheduled = Assert.IsType<SystemScheduledTaskFireMessage>(Assert.Single(emittedMessages));
        Assert.True(scheduled.HasWakeupPrompt);
        Assert.Equal(job.Id, scheduled.TaskName);
        Assert.Equal("loop-session-26", scheduled.SessionId);
        Assert.Equal("Check the build again.", scheduled.Prompt);
        Assert.Equal("CI should have finished by then", scheduled.Reason);
    }

    private static JsonElement Json(object value) =>
        JsonSerializer.SerializeToElement(value);

    private static ToolExecutionContext CreateContext(string? loopSessionId = null) =>
        new()
        {
            WorkingDirectory = "/tmp",
            PermissionContext = new PermissionContext(),
            Tools = [],
            Messages = [],
            CancellationToken = CancellationToken.None,
            LoopSessionId = loopSessionId,
        };

    private static async Task InvokeInstanceTaskAsync(
        CronScheduler scheduler,
        string methodName,
        params object[] arguments)
    {
        var method = typeof(CronScheduler).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(scheduler, arguments)!);
        await task;
    }
}
