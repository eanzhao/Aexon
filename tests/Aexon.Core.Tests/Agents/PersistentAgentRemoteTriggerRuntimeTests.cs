using Aexon.Core.Agents;
using Aexon.Core.Tests.Runtime;

namespace Aexon.Core.Tests.Agents;

public sealed class PersistentAgentRemoteTriggerRuntimeTests
{
    [Fact]
    public async Task Scheduler_FiresPersistedDueScheduleTriggers()
    {
        var tasks = new InMemoryAgentTaskRuntime();
        var task = tasks.CreateWorkItem("Scheduled work", owner: "subagent");
        var dueTrigger = new AgentRemoteTrigger
        {
            Id = "trigger-1",
            WorkItemId = task.Id,
            Kind = AgentRemoteTriggerKind.Schedule,
            Schedule = "*/5 * * * *",
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            NextTriggerAt = DateTimeOffset.UtcNow.AddMilliseconds(-10),
        };
        var metadata = new[]
        {
            AgentRemoteTriggerPersistence.CreateTriggerEntry(dueTrigger),
        };

        var runtime = await PersistentAgentRemoteTriggerRuntime.CreateAsync(
            new RecordingJournal(),
            tasks,
            metadata);

        await using var scheduler = new AgentRemoteTriggerScheduler(
            runtime,
            TimeSpan.FromMilliseconds(10));

        // The scheduler records a fire in stages: it starts the background run
        // (making it visible here) and only afterwards stamps LastTriggeredAt on
        // the trigger. Wait for the last stage so all three assertions observe a
        // fully recorded fire rather than a half-applied one.
        await WaitForAsync(() =>
            tasks.ListBackgroundRuns().Count == 1 &&
            runtime.GetTrigger("trigger-1")?.LastTriggeredAt is not null);

        var run = Assert.Single(tasks.ListBackgroundRuns());
        Assert.Contains(run.Output, line => line.Contains("trigger-1", StringComparison.Ordinal));
        Assert.NotNull(runtime.GetTrigger("trigger-1")!.LastTriggeredAt);
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (predicate())
                return;

            await Task.Delay(20);
        }

        Assert.True(predicate(), "Condition was not met before timeout.");
    }
}
