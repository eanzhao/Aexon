using Aexon.Core.Configuration;
using Aexon.Core.Query;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aexon.Cli;

internal sealed class ChatClientRuntime : IDisposable
{
    private readonly ServiceProvider _services;

    private ChatClientRuntime(
        ServiceProvider services,
        IChatClient chatClient,
        bool hasRequiredConfiguration,
        string? startupSummary)
    {
        _services = services;
        ChatClient = chatClient;
        HasRequiredConfiguration = hasRequiredConfiguration;
        StartupSummary = startupSummary;
    }

    public IChatClient ChatClient { get; }

    public bool HasRequiredConfiguration { get; }

    public string? StartupSummary { get; }

    public static ChatClientRuntime Create(
        AiProvider provider,
        string model,
        QueryEngineConfig config,
        AnthropicClientSettings anthropicSettings,
        NyxIdRoutingContext? nyxIdRouting = null)
    {
        var bootstrap = ChatClientFactory.Create(provider, model, anthropicSettings, nyxIdRouting);
        var pipelineSettings = ChatClientPipelineSettingsLoader.Load();

        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(pipelineSettings);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            if (pipelineSettings.EnableConsoleLogging)
            {
                builder.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "HH:mm:ss ";
                });
            }
        });

        services.AddChatClient(_ => bootstrap.ChatClient)
            .ConfigureOptions(options => ChatClientOptionDefaults.Apply(options, config))
            .Use((inner, serviceProvider) =>
                provider switch
                {
                    AiProvider.OpenAI => new OpenAIReasoningMiddleware(inner),
                    _ => inner,
                })
            .Use((inner, serviceProvider) =>
                provider == AiProvider.Anthropic
                    ? inner
                    : new RetryingChatClient(
                        inner,
                        pipelineSettings.MaxRetryAttempts,
                        pipelineSettings.RetryDelay,
                        serviceProvider.GetService<ILogger<RetryingChatClient>>()))
            .UseLogging()
            .UseOpenTelemetry(
                sourceName: "Aexon.Chat",
                configure: client => client.EnableSensitiveData = false);

        var serviceProvider = services.BuildServiceProvider();
        return new ChatClientRuntime(
            serviceProvider,
            serviceProvider.GetRequiredService<IChatClient>(),
            bootstrap.HasRequiredConfiguration,
            CombineStartupSummary(bootstrap.StartupSummary, pipelineSettings.StartupSummary));
    }

    public void Dispose() => _services.Dispose();

    private static string? CombineStartupSummary(params string?[] parts)
    {
        var lines = parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!)
            .ToArray();

        return lines.Length == 0
            ? null
            : string.Join(Environment.NewLine, lines);
    }
}

internal sealed record ChatClientPipelineSettings(
    int MaxRetryAttempts,
    TimeSpan RetryDelay,
    bool EnableConsoleLogging)
{
    public string? StartupSummary
    {
        get
        {
            var notes = new List<string>();
            if (MaxRetryAttempts > 1)
                notes.Add($"MEAI pipeline: retry middleware enabled ({MaxRetryAttempts} attempts, {RetryDelay.TotalMilliseconds:0} ms delay).");
            if (EnableConsoleLogging)
                notes.Add("MEAI pipeline: chat logging enabled on console.");

            return notes.Count == 0
                ? null
                : string.Join(Environment.NewLine, notes);
        }
    }
}

internal static class ChatClientPipelineSettingsLoader
{
    public static ChatClientPipelineSettings Load(Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        var attempts = ReadInt(getEnvironmentVariable("AEXON_CHAT_RETRY_ATTEMPTS"), fallback: 3, min: 1, max: 8);
        var delayMs = ReadInt(getEnvironmentVariable("AEXON_CHAT_RETRY_DELAY_MS"), fallback: 250, min: 0, max: 10_000);
        var enableConsoleLogging = ReadBool(getEnvironmentVariable("AEXON_CHAT_LOGGING"));

        return new ChatClientPipelineSettings(
            attempts,
            TimeSpan.FromMilliseconds(delayMs),
            enableConsoleLogging);
    }

    private static int ReadInt(string? value, int fallback, int min, int max)
    {
        if (!int.TryParse(value, out var parsed))
            return fallback;

        return Math.Clamp(parsed, min, max);
    }

    private static bool ReadBool(string? value) =>
        value is "1" or "true" or "TRUE" or "yes" or "YES" or "on" or "ON";
}
