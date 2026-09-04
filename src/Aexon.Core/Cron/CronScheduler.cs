using System.Diagnostics;

namespace Aexon.Core.Cron;

/// <summary>
/// Background scheduler that evaluates cron jobs and triggers command execution.
/// </summary>
public sealed class CronScheduler : IAsyncDisposable
{
    private readonly ICronRuntime _runtime;
    private readonly string _workingDirectory;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public CronScheduler(ICronRuntime runtime, string workingDirectory)
    {
        _runtime = runtime;
        _workingDirectory = workingDirectory;
        _loop = RunAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }

        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                await TickAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Scheduler tick failures should not kill the loop.
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var jobs = _runtime.ListJobs();

        foreach (var job in jobs)
        {
            if (!job.Enabled || job.NextRunAt == null || job.NextRunAt > now)
                continue;

            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteJobAsync(job, cancellationToken);
        }
    }

    private async Task ExecuteJobAsync(CronJob job, CancellationToken cancellationToken)
    {
        var executionId = $"exec-{job.Id}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var startedAt = DateTimeOffset.UtcNow;
        string? output = null;
        bool success;

        try
        {
            if (job.Kind == CronJobKind.Wakeup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                success = true;
                output = string.IsNullOrWhiteSpace(job.Prompt)
                    ? "Wakeup fired."
                    : job.Prompt;
            }
            else
            {
                var (exitCode, text) = await RunCommandAsync(
                    job.Command,
                    _workingDirectory,
                    cancellationToken);
                success = exitCode == 0;
                output = text.Length > 4096 ? text[..4096] : text;
            }
        }
        catch (Exception ex)
        {
            success = false;
            output = ex.Message;
        }

        _runtime.RecordExecution(new CronExecutionRecord
        {
            Id = executionId,
            JobId = job.Id,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Success = success,
            Output = output,
        });
    }

    private static async Task<(int ExitCode, string Output)> RunCommandAsync(
        string command,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var shell = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var shellArg = OperatingSystem.IsWindows() ? "/c" : "-c";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = shell,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add(shellArg);
        process.StartInfo.ArgumentList.Add(command);

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = string.IsNullOrEmpty(stderr) ? stdout : $"{stdout}\n{stderr}";
        return (process.ExitCode, output.TrimEnd());
    }
}
