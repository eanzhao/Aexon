using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Aexon.Core.Agents;
using Aexon.Core.Auth;
using Aexon.Core.Commands;
using Aexon.Core.Compaction;
using Aexon.Core.Configuration;
using Aexon.Core.Context;
using Aexon.Core.Memory;
using Aexon.Core.Messages;
using Aexon.Core.Permissions;
using Aexon.Core.Query;
using Aexon.Core.Skills;
using Aexon.Core.Storage;
using Aexon.Core.Tools;

namespace Aexon.Commands;

/// <summary>
/// NyxID login command. Mirrors the upstream `nyxid login` flags:
///   /login                        — browser flow (default base URL or last saved)
///   /login &lt;base-url&gt;           — browser flow against the given server
///   /login --password [--email X] — email/password flow
/// </summary>
public sealed class LoginCommand(
    NyxIdAuthService authService,
    NyxIdCredentialStore credentialStore,
    string defaultBaseUrl) : ICommand
{
    public string Name => "login";
    public string Description => "Sign in with NyxID";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var parsed = LoginArgs.Parse(args);

        var requestedBaseUrl = string.IsNullOrWhiteSpace(parsed.BaseUrlOverride)
            ? credentialStore.Load()?.BaseUrl ?? defaultBaseUrl
            : parsed.BaseUrlOverride!;

        try
        {
            var previous = credentialStore.Load();
            NyxIdCredentials credentials;
            if (parsed.UsePassword)
            {
                credentials = await RunPasswordLoginAsync(parsed, requestedBaseUrl, context);
            }
            else
            {
                credentials = await authService.LoginAsync(requestedBaseUrl, context.CancellationToken);
            }

            var preservedDefaults = previous != null &&
                                    string.Equals(previous.BaseUrl, credentials.BaseUrl, StringComparison.OrdinalIgnoreCase);
            var toSave = preservedDefaults
                ? credentials with
                {
                    DefaultProvider = previous!.DefaultProvider,
                    DefaultModel = previous.DefaultModel,
                }
                : credentials;
            credentialStore.Save(toSave);

            var email = ReadEmailClaim(credentials.IdToken) ?? ReadEmailClaim(credentials.AccessToken);
            if (!string.IsNullOrWhiteSpace(email))
            {
                context.WriteLine($"  Signed in to NyxID as {email}.");
            }
            else
            {
                context.WriteLine($"  Signed in to NyxID at {credentials.BaseUrl}.");
            }

            if (string.IsNullOrWhiteSpace(toSave.DefaultProvider))
            {
                context.WriteLine("  No default LLM provider set. Run `aexon llm` to pick one.");
            }
            else
            {
                var modelSuffix = string.IsNullOrWhiteSpace(toSave.DefaultModel)
                    ? string.Empty
                    : $" ({toSave.DefaultModel})";
                context.WriteLine($"  Default LLM: {toSave.DefaultProvider}{modelSuffix}.");
            }
        }
        catch (Exception ex)
        {
            context.WriteLine($"  NyxID login failed: {ex.Message}");
        }
    }

    private async Task<NyxIdCredentials> RunPasswordLoginAsync(
        LoginArgs parsed,
        string baseUrl,
        CommandContext context)
    {
        var email = parsed.Email;
        if (string.IsNullOrWhiteSpace(email))
        {
            Console.Write("Email: ");
            email = context.ReadInputLine?.Invoke() ?? Console.ReadLine();
        }

        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("Email is required for password login.");

        var password = ReadPasswordSilently("Password: ")
                       ?? throw new InvalidOperationException("Password is required.");

        return await authService.LoginWithPasswordAsync(
            baseUrl,
            email.Trim(),
            password,
            context.CancellationToken);
    }

    private static string? ReadPasswordSilently(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
            return Console.ReadLine();

        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                    password.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar))
                password.Append(key.KeyChar);
        }

        return password.Length == 0 ? null : password.ToString();
    }

    private static string? ReadEmailClaim(string? jwt) =>
        NyxIdJwtPayloadReader.TryGetStringClaim(jwt, "email", out var email) ? email : null;

    private sealed record LoginArgs(string? BaseUrlOverride, bool UsePassword, string? Email)
    {
        public static LoginArgs Parse(string raw)
        {
            string? baseUrl = null;
            string? email = null;
            var usePassword = false;

            if (string.IsNullOrWhiteSpace(raw))
                return new LoginArgs(null, false, null);

            var tokens = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                switch (token)
                {
                    case "--password":
                        usePassword = true;
                        break;

                    case "--email":
                        if (i + 1 < tokens.Length)
                            email = tokens[++i];
                        break;

                    default:
                        if (!token.StartsWith('-') && baseUrl is null)
                            baseUrl = token;
                        break;
                }
            }

            return new LoginArgs(baseUrl, usePassword, email);
        }
    }
}

/// <summary>
/// Represents NyxID logout command.
/// </summary>
public sealed class LogoutCommand(
    NyxIdAuthService authService,
    NyxIdCredentialStore credentialStore) : ICommand
{
    public string Name => "logout";
    public string Description => "Sign out from NyxID";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var credentials = credentialStore.Load();
        if (credentials == null)
        {
            context.WriteLine("  No NyxID login is currently stored.");
            return;
        }

        try
        {
            await authService.LogoutAsync(
                credentials.BaseUrl,
                credentials.AccessToken ?? string.Empty,
                context.CancellationToken);
            context.WriteLine("  Signed out from NyxID.");
        }
        catch (Exception ex)
        {
            context.WriteLine($"  NyxID logout failed: {ex.Message}");
        }
    }
}

/// <summary>
/// Manages the default NyxID-brokered LLM provider for this machine. Running
/// <c>/llm</c> with no subcommand walks the user through an interactive picker.
/// </summary>
/// <remarks>
/// Excluded from coverage — every subcommand drives HTTP against NyxID
/// plus an interactive TTY prompt (Spectre or Console.ReadLine fallback).
/// The underlying helpers carry their own unit tests:
/// <c>NyxIdKeysClientTests</c> pins the /models parser, and the save +
/// credential-mutation helpers in <c>NyxIdProviderPicker</c> are pure
/// functions covered indirectly through the shared picker path.
/// Behavioral correctness of the dispatch is verified by running
/// <c>aexon llm</c> / <c>aexon llm use &lt;slug&gt;</c> against mainnet.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class LlmCommand(
    NyxIdCredentialStore credentialStore,
    NyxIdLlmStatusClient statusClient,
    NyxIdKeysClient keysClient) : ICommand
{
    public string Name => "llm";
    public string Description => "List or set the default NyxID-brokered LLM provider (interactive)";

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var parts = string.IsNullOrWhiteSpace(args)
            ? Array.Empty<string>()
            : args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "use";

        switch (sub)
        {
            case "show":
                ShowCurrent(context);
                return;
            case "list":
                await ListAsync(context);
                return;
            case "use":
                if (parts.Length >= 2)
                {
                    await UseDirectAsync(parts[1], parts.Length >= 3 ? parts[2] : null, context);
                }
                else
                {
                    await UseInteractiveAsync(context);
                }
                return;
            case "clear":
                ClearDefault(context);
                return;
            default:
                context.WriteLine("  Usage: /llm [show|list|use [<provider> [model]]|clear]");
                return;
        }
    }

    private void ShowCurrent(CommandContext context)
    {
        var credentials = credentialStore.Load();
        if (credentials == null)
        {
            context.WriteLine("  Not signed in. Run /login first.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(credentials.DefaultProxySlug))
        {
            var proxyDisplay = string.IsNullOrWhiteSpace(credentials.DefaultProxyLabel)
                ? credentials.DefaultProxySlug!
                : $"{credentials.DefaultProxyLabel} ({credentials.DefaultProxySlug})";
            var modelSuffix = string.IsNullOrWhiteSpace(credentials.DefaultModel)
                ? string.Empty
                : $" — model {credentials.DefaultModel}";
            context.WriteLine($"  Default LLM: AI Service {proxyDisplay}{modelSuffix}");
            context.WriteLine($"  NyxID: {credentials.BaseUrl}");
            return;
        }

        if (string.IsNullOrWhiteSpace(credentials.DefaultProvider))
        {
            context.WriteLine("  No default LLM provider set. Run /llm to pick one.");
            return;
        }

        var gatewayModelSuffix = string.IsNullOrWhiteSpace(credentials.DefaultModel)
            ? string.Empty
            : $" ({credentials.DefaultModel})";
        context.WriteLine($"  Default LLM: gateway provider {credentials.DefaultProvider}{gatewayModelSuffix}");
        context.WriteLine($"  NyxID: {credentials.BaseUrl}");
    }

    private async Task ListAsync(CommandContext context)
    {
        var credentials = credentialStore.Load();
        if (credentials == null)
        {
            context.WriteLine("  Not signed in. Run /login first.");
            return;
        }

        var status = await NyxIdProviderPicker.TryFetchStatusAsync(
            statusClient,
            credentials.BaseUrl,
            context.WriteLine,
            context.CancellationToken);
        if (status != null)
            NyxIdProviderPicker.PrintStatus(status, credentials, context.WriteLine);

        context.WriteLine(string.Empty);
        context.WriteLine("  Discovering NyxID AI Services…");
        var proxyEntries = await NyxIdProviderPicker.DiscoverProxyServicesAsync(
            keysClient,
            credentials.BaseUrl,
            context.WriteLine,
            context.CancellationToken);
        if (proxyEntries.Count == 0)
        {
            context.WriteLine("  (no LLM-capable AI Services)");
            return;
        }

        context.WriteLine($"  AI Services on {credentials.BaseUrl}:");
        foreach (var entry in proxyEntries)
        {
            var marker = string.Equals(
                entry.DisplaySlug,
                credentials.DefaultProxySlug,
                StringComparison.OrdinalIgnoreCase)
                ? " (default)"
                : string.Empty;
            context.WriteLine(
                $"    • {entry.DisplaySlug,-20} [{entry.Status}]{marker}  {entry.DisplayName} — {entry.ProbedModels.Count} model(s)");
        }
    }

    private async Task UseInteractiveAsync(CommandContext context)
    {
        var credentials = credentialStore.Load();
        if (credentials == null)
        {
            context.WriteLine("  Not signed in. Run /login first.");
            return;
        }

        if (Console.IsInputRedirected)
        {
            context.WriteLine("  Interactive picker needs a TTY. Pass the provider explicitly:");
            context.WriteLine("    /llm use <provider> [model]");
            return;
        }

        await NyxIdProviderPicker.RunAsync(
            credentialStore,
            statusClient,
            keysClient,
            credentials,
            context.WriteLine,
            new SpectreProviderPickerUi(),
            context.CancellationToken);
    }

    private async Task UseDirectAsync(string providerInput, string? modelInput, CommandContext context)
    {
        var credentials = credentialStore.Load();
        if (credentials == null)
        {
            context.WriteLine("  Not signed in. Run /login first.");
            return;
        }

        var rawInput = providerInput.Trim();
        var explicitProxy = rawInput.StartsWith("proxy:", StringComparison.OrdinalIgnoreCase);
        var slug = (explicitProxy ? rawInput[6..] : rawInput).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(slug))
        {
            context.WriteLine("  Usage: /llm use <provider-slug> [model]");
            context.WriteLine("         /llm use proxy:<ai-service-slug> [model]   (NyxID AI Service)");
            return;
        }

        if (!explicitProxy && NyxIdProviderPicker.IsSupportedProviderSlug(slug))
        {
            await UseGatewayProviderDirectAsync(credentials, slug, modelInput, context);
            return;
        }

        await UseProxyServiceDirectAsync(credentials, slug, modelInput, context);
    }

    private async Task UseGatewayProviderDirectAsync(
        NyxIdCredentials credentials,
        string providerSlug,
        string? modelInput,
        CommandContext context)
    {
        var status = await NyxIdProviderPicker.TryFetchStatusAsync(
            statusClient,
            credentials.BaseUrl,
            context.WriteLine,
            context.CancellationToken);
        if (status == null)
            return;

        var match = status.Providers
            .FirstOrDefault(p => string.Equals(p.ProviderSlug, providerSlug, StringComparison.OrdinalIgnoreCase));

        if (match == null)
        {
            context.WriteLine($"  NyxID did not list provider '{providerSlug}'. Run /llm list to see what's available.");
            return;
        }

        if (!match.IsReady)
        {
            context.WriteLine($"  Provider '{providerSlug}' is '{match.Status}' on NyxID. Connect a credential in the NyxID UI first.");
            return;
        }

        var model = string.IsNullOrWhiteSpace(modelInput) ? null : modelInput.Trim();
        NyxIdProviderPicker.SaveDefaultGatewayProvider(
            credentialStore,
            credentials,
            providerSlug,
            model,
            context.WriteLine);
    }

    private async Task UseProxyServiceDirectAsync(
        NyxIdCredentials credentials,
        string slug,
        string? modelInput,
        CommandContext context)
    {
        IReadOnlyList<NyxIdAiServiceInfo> services;
        try
        {
            services = await keysClient.ListAsync(credentials.BaseUrl, context.CancellationToken);
        }
        catch (Exception ex)
        {
            context.WriteLine($"  Failed to list NyxID AI Services: {ex.Message}");
            return;
        }

        var info = services.FirstOrDefault(s =>
            string.Equals(s.Slug, slug, StringComparison.OrdinalIgnoreCase));
        if (info == null)
        {
            context.WriteLine(
                $"  '{slug}' is not a known provider. Use one of 'anthropic' / 'openai' for the gateway,");
            context.WriteLine(
                "  or add an AI Service with that slug in the NyxID dashboard. Run /llm list to see what's available.");
            return;
        }

        if (!info.IsReady || !info.IsHttpService)
        {
            context.WriteLine(
                $"  AI Service '{slug}' is '{info.Status}' (active={info.IsActive}, type={info.ServiceType}).");
            context.WriteLine("  Activate it in the NyxID UI before selecting it.");
            return;
        }

        string pickedModel;
        if (!string.IsNullOrWhiteSpace(modelInput))
        {
            pickedModel = modelInput.Trim();
        }
        else
        {
            var models = await keysClient.TryProbeModelsAsync(
                credentials.BaseUrl,
                info.Slug,
                context.CancellationToken);
            if (models is { Count: > 0 })
            {
                pickedModel = models[0];
                context.WriteLine(
                    $"  No model specified — picking first reported by '{info.Label}': {pickedModel}");
                context.WriteLine("  (pass `/llm use <slug> <model>` to choose a different one)");
            }
            else
            {
                context.WriteLine(
                    $"  '{info.Label}' did not return an OpenAI-compatible /v1/models list, so no");
                context.WriteLine("  model could be auto-selected. Pass `/llm use <slug> <model>` explicitly.");
                return;
            }
        }

        NyxIdProviderPicker.SaveDefaultProxyService(
            credentialStore,
            credentials,
            info.Slug,
            info.Label,
            pickedModel,
            context.WriteLine);
    }

    private void ClearDefault(CommandContext context)
    {
        var credentials = credentialStore.Load();
        if (credentials == null)
        {
            context.WriteLine("  Not signed in. Run /login first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(credentials.DefaultProvider) &&
            string.IsNullOrWhiteSpace(credentials.DefaultModel) &&
            string.IsNullOrWhiteSpace(credentials.DefaultProxySlug))
        {
            context.WriteLine("  No default LLM provider was set.");
            return;
        }

        credentialStore.Save(credentials with
        {
            DefaultProvider = null,
            DefaultModel = null,
            DefaultProxySlug = null,
            DefaultProxyLabel = null,
        });
        context.WriteLine("  Cleared default LLM provider.");
    }
}

