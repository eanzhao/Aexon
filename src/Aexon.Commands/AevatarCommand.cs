using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Aexon.Core.Aevatar;
using Aexon.Core.Auth;
using Aexon.Core.Commands;
using Aexon.Core.Interactive;
using Spectre.Console;

namespace Aexon.Commands;

/// <summary>
/// Chats with an aevatar backend's NyxID-chat endpoint from inside aexon, with
/// full parity-level conversation persistence (mirrors the aevatar console's
/// <c>/api/scopes/{scope}/chat-history</c> endpoints).
///
///   /aevatar                                 open a REPL on the current conversation
///   /aevatar &lt;message&gt;                    send + stream in the current conversation
///   /aevatar new                             create a new conversation and make it active
///   /aevatar list                            show persisted conversations (title, updated, count)
///   /aevatar open &lt;id&gt;                    switch to a saved conversation
///   /aevatar delete [id]                     delete a saved conversation (default: active one)
///   /aevatar config show|set-url|set-scope|clear
///
/// Tokens flow through <see cref="NyxIdTokenProvider"/> — the same <c>~/.nyxid/</c>
/// layout as the upstream <c>nyxid</c> Rust CLI — so login is not redone here.
/// </summary>
/// <remarks>
/// The interactive / console-I/O dispatch methods on this class are excluded
/// from coverage analysis via <see cref="ExcludeFromCodeCoverageAttribute"/>:
/// they are thin orchestrators over <c>AevatarChatClient</c> and Spectre.Console,
/// both of which have dedicated unit tests. The pure formatters / parsers
/// (<c>FormatConversationId</c>, <c>SplitHead</c>, <c>Truncate</c>,
/// <c>FormatTimestamp</c>, <c>ExtractTitleFlag</c>) keep their coverage and are
/// tested in <c>AevatarCommandHelpersTests</c>.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class AevatarCommand(
    AevatarChatSettingsStore settingsStore,
    NyxIdTokenProvider tokenProvider) : ICommand
{
    public string Name => "aevatar";
    public string Description => "Chat with an aevatar backend's nyxid-chat endpoints";

    // Surfaced to LineEditor so tab-completion inside the aevatar REPL lists these.
    private static readonly string[] ReplColonCommands =
    [
        ":help", ":config", ":new", ":list", ":open", ":delete", ":scope", ":quit", ":exit",
    ];

    public async Task ExecuteAsync(string args, CommandContext context)
    {
        var (endpointOverride, trimmed, parseError) = ParseInvocationOptions(args);
        if (parseError is not null)
        {
            context.WriteLine(parseError);
            context.WriteLine("  Usage: /aevatar [--endpoint <url>] <message|subcommand>");
            return;
        }

        if (trimmed.Length == 0)
        {
            await RunReplAsync(context, endpointOverride);
            return;
        }

        var (head, rest) = SplitHead(trimmed);
        switch (head.ToLowerInvariant())
        {
            case "config":
                HandleConfig(rest, context, endpointOverride);
                return;

            case "new":
                await CreateConversationAsync(rest, context, endpointOverride);
                return;

            case "list":
                await ListHistoryAsync(context, endpointOverride);
                return;

            case "open":
                await OpenConversationAsync(rest, context, endpointOverride);
                return;

            case "delete":
                await DeleteConversationAsync(rest, context, endpointOverride);
                return;

            case "help":
            case "-h":
            case "--help":
                PrintUsage(context);
                return;

            case "send":
                await SendOneShotAsync(rest, context, endpointOverride);
                return;

            case "chat":
                await RunChatWebAsync(rest, context, endpointOverride);
                return;

            case "web":
                await RunWebAsync(rest, context, endpointOverride);
                return;

            default:
                // Treat bare `/aevatar <message>` as send.
                await SendOneShotAsync(trimmed, context, endpointOverride);
                return;
        }
    }

    // ── Web UI subcommands ──

    private async Task RunWebAsync(string args, CommandContext context, string? endpointOverride)
    {
        const int defaultPort = 6689;
        const string webRootSubdir = "aevatar-workbench";

        var (port, noBrowser, parsedEndpointOverride, error) = ParseWebFlags(args, defaultPort);
        if (error is not null)
        {
            context.WriteLine(error);
            context.WriteLine("  Usage: /aevatar web [--endpoint <url>] [--port <n>] [--no-browser]");
            return;
        }

        var settings = settingsStore.Load();
        var effectiveEndpoint = parsedEndpointOverride ?? endpointOverride;
        var baseUrl = AevatarChatSettingsStore.ResolveBaseUrl(settings, effectiveEndpoint);

        try
        {
            await AevatarWebHost.RunAsync(port, baseUrl, webRootSubdir, noBrowser, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteError(context, ex);
        }
    }

    private async Task RunChatWebAsync(string args, CommandContext context, string? endpointOverride)
    {
        const int defaultPort = 6688;
        const string webRootSubdir = "aevatar-chat";

        var (port, noBrowser, parsedEndpointOverride, error) = ParseWebFlags(args, defaultPort);
        if (error is not null)
        {
            context.WriteLine(error);
            context.WriteLine("  Usage: /aevatar chat [--endpoint <url>] [--port <n>] [--no-browser]");
            return;
        }

        var settings = settingsStore.Load();
        var effectiveEndpoint = parsedEndpointOverride ?? endpointOverride;
        var baseUrl = AevatarChatSettingsStore.ResolveBaseUrl(settings, effectiveEndpoint);

        try
        {
            await AevatarWebHost.RunAsync(port, baseUrl, webRootSubdir, noBrowser, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteError(context, ex);
        }
    }

    internal static (int Port, bool NoBrowser, string? EndpointOverride, string? Error) ParseWebFlags(string args, int defaultPort)
    {
        var port = defaultPort;
        var noBrowser = false;
        string? endpointOverride = null;

        if (string.IsNullOrWhiteSpace(args))
            return (port, noBrowser, endpointOverride, null);

        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token == "--no-browser")
            {
                noBrowser = true;
                continue;
            }

            if (token == "--port" && i + 1 < tokens.Length)
            {
                if (!int.TryParse(tokens[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out port) ||
                    port is < 1 or > 65535)
                {
                    return (0, false, null, $"  Invalid --port value: {tokens[i + 1]}");
                }

                i++;
                continue;
            }

            if (token.StartsWith("--port=", StringComparison.Ordinal))
            {
                var value = token["--port=".Length..];
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) ||
                    port is < 1 or > 65535)
                {
                    return (0, false, null, $"  Invalid --port value: {value}");
                }

                continue;
            }

            if (token == "--endpoint")
            {
                if (endpointOverride is not null)
                    return (0, false, null, "  --endpoint can only be passed once");

                if (i + 1 >= tokens.Length)
                    return (0, false, null, "  Missing --endpoint value");

                endpointOverride = NormalizeEndpointOverride(tokens[++i], out var endpointError);
                if (endpointError is not null)
                    return (0, false, null, endpointError);

                continue;
            }

            if (token.StartsWith("--endpoint=", StringComparison.Ordinal))
            {
                if (endpointOverride is not null)
                    return (0, false, null, "  --endpoint can only be passed once");

                endpointOverride = NormalizeEndpointOverride(token["--endpoint=".Length..], out var endpointError);
                if (endpointError is not null)
                    return (0, false, null, endpointError);

                continue;
            }

            return (0, false, null, $"  Unknown flag: {token}");
        }

        return (port, noBrowser, endpointOverride, null);
    }

    // ── Config (unchanged) ──

    private void HandleConfig(string args, CommandContext context, string? endpointOverride)
    {
        var (sub, rest) = SplitHead(args);
        switch (sub.ToLowerInvariant())
        {
            case "":
            case "show":
                ShowConfig(context, endpointOverride);
                return;

            case "set-url":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    context.WriteLine("  Usage: /aevatar config set-url <url>");
                    return;
                }

                if (!Uri.TryCreate(rest.Trim(), UriKind.Absolute, out _))
                {
                    context.WriteLine($"  Invalid URL: {rest.Trim()}");
                    return;
                }

                {
                    var existing = settingsStore.Load();
                    settingsStore.Save(existing with { BaseUrl = rest.Trim() });
                    context.WriteLine($"  Saved aevatar base URL: {rest.Trim()}");
                    context.WriteLine($"  Config file: {settingsStore.FilePath}");
                }
                return;

            case "set-scope":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    context.WriteLine("  Usage: /aevatar config set-scope <scopeId>");
                    return;
                }

                {
                    var existing = settingsStore.Load();
                    settingsStore.Save(existing with { ScopeId = rest.Trim() });
                    context.WriteLine($"  Saved aevatar scope: {rest.Trim()}");
                }
                return;

            case "clear":
                {
                    var existing = settingsStore.Load();
                    settingsStore.Save(existing with { BaseUrl = null });
                    context.WriteLine("  Cleared aevatar base URL.");
                }
                return;

            default:
                context.WriteLine("  Usage: /aevatar config [show|set-url <url>|set-scope <scopeId>|clear]");
                return;
        }
    }

    private void ShowConfig(CommandContext context, string? endpointOverride)
    {
        var settings = settingsStore.Load();
        var persistedBaseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl)
            ? $"{AevatarChatSettingsStore.MainnetBaseUrl} (mainnet default)"
            : settings.BaseUrl!;
        var effectiveBaseUrl = AevatarChatSettingsStore.ResolveBaseUrl(settings, endpointOverride);
        var scope = string.IsNullOrWhiteSpace(settings.ScopeId)
            ? AevatarChatSettingsStore.DefaultScopeId + " (default)"
            : settings.ScopeId!;
        var actor = string.IsNullOrWhiteSpace(settings.LastActorId) ? "(none)" : settings.LastActorId!;

        if (string.IsNullOrWhiteSpace(endpointOverride))
        {
            context.WriteLine($"  Base URL:     {persistedBaseUrl}");
        }
        else
        {
            context.WriteLine($"  Base URL:     {effectiveBaseUrl} (this invocation only)");
            context.WriteLine($"  Saved URL:    {persistedBaseUrl}");
        }

        context.WriteLine($"  Scope:        {scope}");
        context.WriteLine($"  Last actor:   {actor}");
        context.WriteLine($"  Config file:  {settingsStore.FilePath}");
    }

    // ── Conversation subcommands ──

    private async Task CreateConversationAsync(string args, CommandContext context, string? endpointOverride)
    {
        if (!TryBuildClient(context, endpointOverride, out var client, out var scopeId, out var settings))
            return;

        try
        {
            var actorId = await client!.CreateConversationAsync(scopeId, context.CancellationToken);
            var title = ExtractTitleFlag(args) ?? "(new chat)";
            var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            var meta = new AevatarConversationMeta
            {
                Id = actorId,
                ActorId = actorId,
                Title = title,
                ServiceId = "nyxid-chat",
                ServiceKind = "nyxid-chat",
                CreatedAt = now,
                UpdatedAt = now,
                MessageCount = 0,
            };

            await TrySaveHistoryAsync(client!, scopeId, actorId, meta, Array.Empty<AevatarStoredMessage>(), context);
            settingsStore.Save(settings! with { LastActorId = actorId });

            context.WriteLine($"  Created conversation: {actorId}");
            context.WriteLine($"  Title: {title}");
        }
        catch (Exception ex)
        {
            WriteError(context, ex);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private async Task ListHistoryAsync(CommandContext context, string? endpointOverride)
    {
        if (!TryBuildClient(context, endpointOverride, out var client, out var scopeId, out var settings))
            return;

        try
        {
            var conversations = await client!.ListHistoryAsync(scopeId, context.CancellationToken);
            RenderConversationTable(conversations, settings!.LastActorId);
        }
        catch (Exception ex)
        {
            WriteError(context, ex);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private static void RenderConversationTable(
        IReadOnlyList<AevatarConversationMeta> conversations,
        string? activeId)
    {
        if (conversations.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]  (no saved conversations in this scope)[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey27)
            .Expand()
            .Title($"[dim]{conversations.Count} conversation(s)[/]");

        table.AddColumn(new TableColumn("").NoWrap().Width(2));
        table.AddColumn(new TableColumn("[bold]id[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]updated[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]msg[/]").RightAligned().Width(5));
        table.AddColumn(new TableColumn("[bold]title[/]"));

        foreach (var conv in conversations.OrderByDescending(c => c.UpdatedAt, StringComparer.Ordinal))
        {
            var isActive = string.Equals(conv.Id, activeId, StringComparison.Ordinal);
            var marker = isActive ? "[springgreen3]●[/]" : " ";
            var shortId = Markup.Escape(FormatConversationId(conv.Id));
            var idCell = isActive
                ? $"[springgreen3]{shortId}[/]"
                : $"[white]{shortId}[/]";
            var title = string.IsNullOrWhiteSpace(conv.Title) ? "[dim](untitled)[/]" : Markup.Escape(Truncate(conv.Title, 80));
            table.AddRow(
                marker,
                idCell,
                $"[dim]{Markup.Escape(FormatTimestamp(conv.UpdatedAt))}[/]",
                conv.MessageCount.ToString(CultureInfo.InvariantCulture),
                title);
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            "[dim]  (shortened id shown; use `/aevatar open <id-prefix>` or paste the full id to switch)[/]");
    }

    /// <summary>
    /// Collapses the predictable <c>nyxid-chat-</c> prefix and trims the long hex id
    /// to the first 8 + ellipsis + last 4 chars so the table still fits on an 80-col TTY.
    /// The full id is still persisted in settings, so `:open &lt;full-id&gt;` keeps working.
    /// </summary>
    internal static string FormatConversationId(string id)
    {
        const string prefix = "nyxid-chat-";
        var visible = id.StartsWith(prefix, StringComparison.Ordinal)
            ? id[prefix.Length..]
            : id;
        if (visible.Length <= 16)
            return visible;
        return $"{visible[..8]}…{visible[^4..]}";
    }

    private async Task OpenConversationAsync(string args, CommandContext context, string? endpointOverride)
    {
        var input = args.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            context.WriteLine("  Usage: /aevatar open <conversationId-or-prefix>");
            return;
        }

        if (!TryBuildClient(context, endpointOverride, out var client, out var scopeId, out var settings))
            return;

        try
        {
            var resolvedId = await ResolveConversationIdAsync(client!, scopeId, input, context);
            if (resolvedId is null)
                return;

            var messages = await client!.GetHistoryAsync(scopeId, resolvedId, context.CancellationToken);
            settingsStore.Save(settings! with { LastActorId = resolvedId });
            AnsiConsole.MarkupLine(
                $"[springgreen3]  ● opened[/] [white]{Markup.Escape(resolvedId)}[/] [dim]({messages.Count} message(s))[/]");
            PrintTranscript(messages, context);

            if (!Console.IsInputRedirected)
                await DriveReplAsync(client!, scopeId, settings! with { LastActorId = resolvedId }, context, endpointOverride);
        }
        catch (Exception ex)
        {
            WriteError(context, ex);
        }
        finally
        {
            client?.Dispose();
        }
    }

    /// <summary>
    /// Accepts either a full conversation id or the shortened form shown in the
    /// <c>:list</c> table (e.g. <c>229b0df5…d49c</c> or just <c>229b0df5</c>). Prints
    /// a disambiguation message and returns null when 0 or &gt;1 conversations match.
    /// </summary>
    private static async Task<string?> ResolveConversationIdAsync(
        AevatarChatClient client,
        string scopeId,
        string input,
        CommandContext context)
    {
        if (input.Length > 24 && !input.Contains('…'))
            return input; // looks like a full id — trust it

        List<AevatarConversationMeta> conversations;
        try
        {
            conversations = (await client.ListHistoryAsync(scopeId, context.CancellationToken)).ToList();
        }
        catch (AevatarChatException ex)
        {
            AnsiConsole.MarkupLine($"[red]  ✗ {Markup.Escape(ex.Message)}[/]");
            return null;
        }

        var matches = conversations
            .Where(c =>
                string.Equals(c.Id, input, StringComparison.Ordinal) ||
                string.Equals(FormatConversationId(c.Id), input, StringComparison.Ordinal) ||
                c.Id.Contains(input.Split('…', 2)[0], StringComparison.Ordinal))
            .ToList();

        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]  ✗ no conversation matches[/] [white]{Markup.Escape(input)}[/]");
            return null;
        }

        if (matches.Count > 1)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]  ⚠ {matches.Count} conversations match[/] [white]{Markup.Escape(input)}[/] — " +
                "use a longer prefix or paste the full id:");
            foreach (var match in matches)
                AnsiConsole.MarkupLine($"    [dim]{Markup.Escape(match.Id)}[/]  [white]{Markup.Escape(Truncate(match.Title, 60))}[/]");
            return null;
        }

        return matches[0].Id;
    }

    private async Task DeleteConversationAsync(string args, CommandContext context, string? endpointOverride)
    {
        if (!TryBuildClient(context, endpointOverride, out var client, out var scopeId, out var settings))
            return;

        try
        {
            string? actorId;
            if (string.IsNullOrWhiteSpace(args))
            {
                actorId = settings!.LastActorId;
            }
            else
            {
                actorId = await ResolveConversationIdAsync(client!, scopeId, args.Trim(), context);
                if (actorId is null)
                    return;
            }

            if (string.IsNullOrWhiteSpace(actorId))
            {
                context.WriteLine("  No conversation id. Pass one explicitly or run /aevatar new first.");
                return;
            }

            // Best-effort: delete both the history projection and the actor. Either can
            // 404 independently depending on whether history was ever written.
            try { await client!.DeleteHistoryAsync(scopeId, actorId!, context.CancellationToken); }
            catch (AevatarChatException) { }

            try { await client!.DeleteConversationAsync(scopeId, actorId!, context.CancellationToken); }
            catch (AevatarChatException) { }

            AnsiConsole.MarkupLine($"[springgreen3]  ● deleted[/] [white]{Markup.Escape(actorId!)}[/]");

            if (string.Equals(actorId, settings!.LastActorId, StringComparison.Ordinal))
                settingsStore.Save(settings with { LastActorId = null });
        }
        catch (Exception ex)
        {
            WriteError(context, ex);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private async Task SendOneShotAsync(string message, CommandContext context, string? endpointOverride)
    {
        var prompt = message?.Trim() ?? string.Empty;
        if (prompt.Length == 0)
        {
            context.WriteLine("  Usage: /aevatar <message>");
            return;
        }

        if (!TryBuildClient(context, endpointOverride, out var client, out var scopeId, out var settings))
            return;

        try
        {
            var session = await EnsureSessionAsync(client!, scopeId, settings!, context, announceNew: true);
            if (session is null)
                return;

            await ChatTurnAsync(client!, scopeId, session, prompt, context);
        }
        catch (Exception ex)
        {
            WriteError(context, ex);
        }
        finally
        {
            client?.Dispose();
        }
    }

    // ── REPL ──

    private async Task RunReplAsync(CommandContext context, string? endpointOverride)
    {
        if (Console.IsInputRedirected)
        {
            context.WriteLine(
                "  /aevatar interactive mode needs a TTY. Either pipe a message (e.g. `aexon aevatar \"hi\"`) " +
                "or redirect stdin to nothing.");
            return;
        }

        if (!TryBuildClient(context, endpointOverride, out var client, out var scopeId, out var settings))
            return;

        try
        {
            await DriveReplAsync(client!, scopeId, settings!, context, endpointOverride);
        }
        finally
        {
            client?.Dispose();
        }
    }

    private async Task DriveReplAsync(
        AevatarChatClient client,
        string scopeId,
        AevatarChatSettings settings,
        CommandContext context,
        string? endpointOverride)
    {
        RenderReplBanner(client.BaseAddress.ToString().TrimEnd('/'), scopeId);

        var session = await TryLoadExistingSessionAsync(client, scopeId, settings, context);
        if (session is { Messages.Count: > 0 } resumed)
        {
            AnsiConsole.MarkupLine(
                $"[dim]↺ resumed [white]{Markup.Escape(resumed.Id)}[/] ({resumed.Messages.Count} prior message(s))[/]");
        }

        // "you ❯ " — visible width is 6 columns.
        // ANSI bold + 256-color 39 (cornflower-ish blue) + reset:
        const string userPromptAnsi = "\u001b[1;38;5;39myou ❯\u001b[0m ";
        const int userPromptVisibleWidth = 6;

        // Aevatar uses colon-prefixed commands (":help", ":list", ...), so feed
        // the editor that list for tab-completion. Separate history list — we
        // don't want aevatar chat lines polluting the main aexon REPL history.
        var aevatarHistory = new List<string>();
        var lineEditor = new LineEditor(
            commandNames: ReplColonCommands,
            history: aevatarHistory,
            workingDirectory: Environment.CurrentDirectory,
            prompt: userPromptAnsi,
            placeholder: string.Empty,
            promptVisibleWidth: userPromptVisibleWidth);

        while (!context.CancellationToken.IsCancellationRequested)
        {
            var line = await lineEditor.ReadLineAsync(context.CancellationToken);
            if (line is null)
                break;

            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            if (trimmed.StartsWith(':'))
            {
                var outcome = await HandleReplCommandAsync(trimmed, context, client, scopeId, session, endpointOverride);
                if (outcome.Session is { } newSession)
                    session = newSession;
                if (outcome.Exit)
                    return;
                continue;
            }

            session ??= await CreateAdHocSessionAsync(client, scopeId, context);
            if (session is null)
                continue;

            AnsiConsole.Markup("[bold springgreen3]aevatar ❯[/] ");
            try
            {
                await ChatTurnAsync(client, scopeId, session, trimmed, context);
            }
            catch (Exception ex)
            {
                WriteError(context, ex);
            }

            AnsiConsole.Write(new Rule().RuleStyle("grey19").LeftJustified());
        }
    }

    private static void RenderReplBanner(string baseUrl, string scopeId)
    {
        var headline = new Rule($"[bold]Aevatar[/] [dim]·[/] [cyan]{Markup.Escape(baseUrl)}[/] [dim]·[/] scope=[magenta]{Markup.Escape(scopeId)}[/]")
            .RuleStyle("grey27")
            .LeftJustified();
        AnsiConsole.Write(headline);
        AnsiConsole.MarkupLine("[dim]Type [white]:help[/] for commands, [white]:quit[/] to exit. Empty line does nothing.[/]");
    }

    private async Task<ReplOutcome> HandleReplCommandAsync(
        string line,
        CommandContext context,
        AevatarChatClient client,
        string scopeId,
        ChatSession? currentSession,
        string? endpointOverride)
    {
        var (head, rest) = SplitHead(line[1..]);
        switch (head.ToLowerInvariant())
        {
            case "help":
                {
                    var help = new Table()
                        .HideHeaders()
                        .Border(TableBorder.None)
                        .AddColumn(new TableColumn("cmd").NoWrap())
                        .AddColumn(new TableColumn("desc"));
                    void Row(string cmd, string desc) =>
                        help.AddRow($"[bold deepskyblue2]{Markup.Escape(cmd)}[/]", $"[dim]{Markup.Escape(desc)}[/]");
                    Row(":help", "show this help");
                    Row(":config", "show current base URL / scope / actor");
                    Row(":new [title…]", "create a new conversation and make it active");
                    Row(":list", "list saved conversations in this scope");
                    Row(":open <id>", "switch to a saved conversation");
                    Row(":delete [id]", "delete a conversation (default: current)");
                    Row(":scope <scopeId>", "switch scope for this and future sessions");
                    Row(":quit / :exit", "leave the REPL");
                    AnsiConsole.Write(help);
                    return ReplOutcome.Continue;
                }

            case "config":
                ShowConfig(context, endpointOverride);
                return ReplOutcome.Continue;

            case "new":
                {
                    try
                    {
                        var title = string.IsNullOrWhiteSpace(rest) ? "(new chat)" : rest.Trim();
                        var session = await CreateSessionAsync(client, scopeId, title, context);
                        if (session is not null)
                        {
                            settingsStore.Save(settingsStore.Load() with { LastActorId = session.Id });
                            context.WriteLine($"  created: {session.Id} ({title})");
                            return new ReplOutcome(false, session);
                        }
                    }
                    catch (Exception ex) { WriteError(context, ex); }
                    return ReplOutcome.Continue;
                }

            case "list":
                {
                    try
                    {
                        var conversations = await client.ListHistoryAsync(scopeId, context.CancellationToken);
                        RenderConversationTable(conversations, currentSession?.Id);
                    }
                    catch (Exception ex) { WriteError(context, ex); }
                    return ReplOutcome.Continue;
                }

            case "open":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    context.WriteLine("  :open <conversationId-or-prefix>");
                    return ReplOutcome.Continue;
                }

                try
                {
                    var resolved = await ResolveConversationIdAsync(client, scopeId, rest.Trim(), context);
                    if (resolved is null)
                        return ReplOutcome.Continue;

                    var messages = (await client.GetHistoryAsync(scopeId, resolved, context.CancellationToken)).ToList();
                    settingsStore.Save(settingsStore.Load() with { LastActorId = resolved });
                    AnsiConsole.MarkupLine(
                        $"[springgreen3]  ● opened[/] [white]{Markup.Escape(resolved)}[/] [dim]({messages.Count} message(s))[/]");
                    PrintTranscript(messages, context);
                    return new ReplOutcome(false, new ChatSession(resolved, await LoadOrSynthesizeMetaAsync(client, scopeId, resolved), messages));
                }
                catch (Exception ex) { WriteError(context, ex); }
                return ReplOutcome.Continue;

            case "delete":
                {
                    var target = string.IsNullOrWhiteSpace(rest) ? currentSession?.Id : rest.Trim();
                    if (string.IsNullOrWhiteSpace(target))
                    {
                        context.WriteLine("  no conversation to delete");
                        return ReplOutcome.Continue;
                    }

                    try
                    {
                        try { await client.DeleteHistoryAsync(scopeId, target!, context.CancellationToken); }
                        catch (AevatarChatException) { }
                        try { await client.DeleteConversationAsync(scopeId, target!, context.CancellationToken); }
                        catch (AevatarChatException) { }
                        context.WriteLine($"  deleted: {target}");

                        if (string.Equals(target, currentSession?.Id, StringComparison.Ordinal))
                        {
                            settingsStore.Save(settingsStore.Load() with { LastActorId = null });
                            return new ReplOutcome(false, null);
                        }
                    }
                    catch (Exception ex) { WriteError(context, ex); }
                    return ReplOutcome.Continue;
                }

            case "scope":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    context.WriteLine("  :scope <scopeId>");
                    return ReplOutcome.Continue;
                }

                {
                    var updated = settingsStore.Load() with { ScopeId = rest.Trim(), LastActorId = null };
                    settingsStore.Save(updated);
                    context.WriteLine($"  switched to scope: {rest.Trim()} (session cleared; re-enter :open or start a new message)");
                    return new ReplOutcome(false, null);
                }

            case "quit":
            case "exit":
                return ReplOutcome.Quit;

            default:
                context.WriteLine($"  unknown command: :{head} — try :help");
                return ReplOutcome.Continue;
        }
    }

    // ── Session / turn helpers ──

    private async Task<ChatSession?> EnsureSessionAsync(
        AevatarChatClient client,
        string scopeId,
        AevatarChatSettings settings,
        CommandContext context,
        bool announceNew)
    {
        if (!string.IsNullOrWhiteSpace(settings.LastActorId))
        {
            try
            {
                var messages = (await client.GetHistoryAsync(scopeId, settings.LastActorId!, context.CancellationToken)).ToList();
                var meta = await LoadOrSynthesizeMetaAsync(client, scopeId, settings.LastActorId!);
                return new ChatSession(settings.LastActorId!, meta, messages);
            }
            catch (AevatarChatException)
            {
                // History projection missing — fall through and create fresh.
            }
        }

        var created = await CreateSessionAsync(client, scopeId, title: "(new chat)", context);
        if (created is null)
            return null;

        settingsStore.Save(settings with { LastActorId = created.Id });
        if (announceNew)
            context.WriteLine($"  (started new conversation {created.Id})");
        return created;
    }

    private async Task<ChatSession?> CreateAdHocSessionAsync(
        AevatarChatClient client,
        string scopeId,
        CommandContext context)
    {
        var session = await CreateSessionAsync(client, scopeId, title: "(new chat)", context);
        if (session is null)
            return null;

        settingsStore.Save(settingsStore.Load() with { LastActorId = session.Id });
        return session;
    }

    private async Task<ChatSession?> TryLoadExistingSessionAsync(
        AevatarChatClient client,
        string scopeId,
        AevatarChatSettings settings,
        CommandContext context)
    {
        if (string.IsNullOrWhiteSpace(settings.LastActorId))
            return null;

        try
        {
            var messages = (await client.GetHistoryAsync(scopeId, settings.LastActorId!, context.CancellationToken)).ToList();
            var meta = await LoadOrSynthesizeMetaAsync(client, scopeId, settings.LastActorId!);
            if (messages.Count > 0)
                context.WriteLine($"(resumed {settings.LastActorId}, {messages.Count} prior message(s))");
            return new ChatSession(settings.LastActorId!, meta, messages);
        }
        catch (AevatarChatException)
        {
            return null;
        }
    }

    private async Task<ChatSession?> CreateSessionAsync(
        AevatarChatClient client,
        string scopeId,
        string title,
        CommandContext context)
    {
        try
        {
            var actorId = await client.CreateConversationAsync(scopeId, context.CancellationToken);
            var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var meta = new AevatarConversationMeta
            {
                Id = actorId,
                ActorId = actorId,
                Title = string.IsNullOrWhiteSpace(title) ? "(new chat)" : title,
                ServiceId = "nyxid-chat",
                ServiceKind = "nyxid-chat",
                CreatedAt = now,
                UpdatedAt = now,
                MessageCount = 0,
            };

            await TrySaveHistoryAsync(client, scopeId, actorId, meta, Array.Empty<AevatarStoredMessage>(), context);
            return new ChatSession(actorId, meta, new List<AevatarStoredMessage>());
        }
        catch (Exception ex)
        {
            WriteError(context, ex);
            return null;
        }
    }

    private async Task<AevatarConversationMeta> LoadOrSynthesizeMetaAsync(
        AevatarChatClient client,
        string scopeId,
        string conversationId)
    {
        try
        {
            var conversations = await client.ListHistoryAsync(scopeId, CancellationToken.None);
            var match = conversations.FirstOrDefault(c =>
                string.Equals(c.Id, conversationId, StringComparison.Ordinal));
            if (match is not null)
                return match;
        }
        catch (AevatarChatException)
        {
        }

        var now = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        return new AevatarConversationMeta
        {
            Id = conversationId,
            ActorId = conversationId,
            Title = "(chat)",
            ServiceId = "nyxid-chat",
            ServiceKind = "nyxid-chat",
            CreatedAt = now,
            UpdatedAt = now,
            MessageCount = 0,
        };
    }

    private async Task ChatTurnAsync(
        AevatarChatClient client,
        string scopeId,
        ChatSession session,
        string prompt,
        CommandContext context)
    {
        var nowEpochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var userMessage = new AevatarStoredMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            Role = "user",
            Content = prompt,
            Timestamp = nowEpochMs,
            Status = "complete",
        };
        session.Messages.Add(userMessage);

        var assistantBuffer = new StringBuilder();
        var assistantStatus = "complete";
        string? assistantError = null;
        var ct = context.CancellationToken;

        try
        {
            await foreach (var frame in client.StreamMessageAsync(scopeId, session.Id, prompt, sessionId: null, ct))
            {
                switch (frame.Type)
                {
                    case AevatarChatFrameType.TextMessageContent:
                        {
                            var delta = frame.TryGetString("textMessageContent", "delta");
                            if (!string.IsNullOrEmpty(delta))
                            {
                                Console.Write(delta);
                                assistantBuffer.Append(delta);
                            }
                        }
                        break;

                    case AevatarChatFrameType.TextMessageEnd:
                        if (assistantBuffer.Length > 0 && assistantBuffer[^1] != '\n')
                            Console.WriteLine();
                        break;

                    case AevatarChatFrameType.StepStarted:
                        {
                            var name = frame.TryGetString("stepStarted", "stepName")
                                       ?? frame.TryGetString("stepStarted", "name")
                                       ?? "(step)";
                            NewLineIfNeeded(assistantBuffer);
                            AnsiConsole.MarkupLine($"  [yellow]● step[/] [dim]{Markup.Escape(name)}[/]");
                        }
                        break;

                    case AevatarChatFrameType.StepFinished:
                        {
                            var name = frame.TryGetString("stepFinished", "stepName")
                                       ?? frame.TryGetString("stepFinished", "name")
                                       ?? "(step)";
                            NewLineIfNeeded(assistantBuffer);
                            AnsiConsole.MarkupLine($"  [yellow]○ step done[/] [dim]{Markup.Escape(name)}[/]");
                        }
                        break;

                    case AevatarChatFrameType.ToolCallStart:
                        {
                            var toolName = frame.TryGetString("toolCallStart", "toolName") ?? "(tool)";
                            var callId = frame.TryGetString("toolCallStart", "toolCallId") ?? "?";
                            NewLineIfNeeded(assistantBuffer);
                            AnsiConsole.MarkupLine(
                                $"  [cyan]◆ tool[/] [bold]{Markup.Escape(toolName)}[/] [dim]({Markup.Escape(callId)})[/]");
                        }
                        break;

                    case AevatarChatFrameType.ToolCallEnd:
                        {
                            var callId = frame.TryGetString("toolCallEnd", "toolCallId") ?? "?";
                            NewLineIfNeeded(assistantBuffer);
                            AnsiConsole.MarkupLine($"  [cyan]◇ tool done[/] [dim]{Markup.Escape(callId)}[/]");
                        }
                        break;

                    case AevatarChatFrameType.ToolApprovalRequest:
                        {
                            var toolName = frame.TryGetString("toolApprovalRequest", "toolName") ?? "(tool)";
                            var requestId = frame.TryGetString("toolApprovalRequest", "requestId") ?? "?";
                            NewLineIfNeeded(assistantBuffer);
                            AnsiConsole.MarkupLine(
                                $"  [orange1]⚠ approval needed[/] [bold]{Markup.Escape(toolName)}[/] " +
                                $"[dim](requestId={Markup.Escape(requestId)}; interactive approval lands in Phase 2)[/]");
                        }
                        break;

                    case AevatarChatFrameType.HumanInputRequest:
                        {
                            var prmpt = frame.TryGetString("humanInputRequest", "prompt") ?? "(prompt)";
                            NewLineIfNeeded(assistantBuffer);
                            AnsiConsole.MarkupLine(
                                $"  [orange1]⚠ human input requested[/] [dim]{Markup.Escape(prmpt)} (Phase 2 will collect a reply)[/]");
                        }
                        break;

                    case AevatarChatFrameType.MediaContent:
                        {
                            var kind = frame.TryGetString("mediaContent", "kind") ?? "media";
                            NewLineIfNeeded(assistantBuffer);
                            AnsiConsole.MarkupLine($"  [magenta]◈ media[/] [dim]{Markup.Escape(kind)}[/]");
                        }
                        break;

                    case AevatarChatFrameType.RunError:
                        {
                            var msg = frame.TryGetString("runError", "message") ?? "(unknown error)";
                            NewLineIfNeeded(assistantBuffer);
                            AnsiConsole.MarkupLine($"  [red]✗ error[/] [red]{Markup.Escape(msg)}[/]");
                            assistantStatus = "error";
                            assistantError = msg;
                        }
                        break;
                }
            }
        }
        catch (Exception)
        {
            // Persist whatever we have (user message + partial assistant) so the
            // conversation projection doesn't desync on a transport failure.
            assistantStatus = "error";
            throw;
        }
        finally
        {
            var assistant = new AevatarStoredMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Role = "assistant",
                Content = assistantBuffer.ToString(),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Status = assistantStatus,
                Error = assistantError,
            };
            session.Messages.Add(assistant);

            session.Meta = session.Meta with
            {
                Title = string.IsNullOrWhiteSpace(session.Meta.Title) || session.Meta.Title == "(new chat)"
                    ? Truncate(prompt, 60)
                    : session.Meta.Title,
                UpdatedAt = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                MessageCount = session.Messages.Count,
            };

            await TrySaveHistoryAsync(client, scopeId, session.Id, session.Meta, session.Messages, context);
        }
    }

    // ── Misc ──

    private bool TryBuildClient(
        CommandContext context,
        string? endpointOverride,
        out AevatarChatClient? client,
        out string scopeId,
        out AevatarChatSettings? settings)
    {
        _ = context;
        settings = settingsStore.Load();
        var baseUrl = AevatarChatSettingsStore.ResolveBaseUrl(settings, endpointOverride);
        scopeId = AevatarChatSettingsStore.ResolveScopeId(settings, @override: null);
        client = new AevatarChatClient(baseUrl, tokenProvider);
        return true;
    }

    private static async Task TrySaveHistoryAsync(
        AevatarChatClient client,
        string scopeId,
        string conversationId,
        AevatarConversationMeta meta,
        IReadOnlyList<AevatarStoredMessage> messages,
        CommandContext context)
    {
        try
        {
            await client.SaveHistoryAsync(scopeId, conversationId, meta, messages, context.CancellationToken);
        }
        catch (AevatarChatException ex)
        {
            AnsiConsole.MarkupLine($"[yellow dim]  ⚠ could not save chat history:[/] [dim]{Markup.Escape(ex.Message)}[/]");
        }
    }

    private static void PrintTranscript(IReadOnlyList<AevatarStoredMessage> messages, CommandContext context)
    {
        _ = context;
        if (messages.Count == 0)
        {
            AnsiConsole.MarkupLine("[dim]  (conversation is empty)[/]");
            return;
        }

        AnsiConsole.Write(new Rule("[dim]transcript[/]").RuleStyle("grey27").LeftJustified());

        foreach (var message in messages)
        {
            var isAssistant = string.Equals(message.Role, "assistant", StringComparison.Ordinal);
            var label = isAssistant
                ? "[bold springgreen3]aevatar ❯[/]"
                : "[bold deepskyblue2]you ❯[/]";
            var content = string.IsNullOrWhiteSpace(message.Content) ? "[dim](empty)[/]" : Markup.Escape(message.Content);
            AnsiConsole.MarkupLine($"{label} {content}");

            if (!string.IsNullOrWhiteSpace(message.Error))
                AnsiConsole.MarkupLine($"  [red]✗ {Markup.Escape(message.Error!)}[/]");
        }

        AnsiConsole.Write(new Rule().RuleStyle("grey19").LeftJustified());
    }

    private static void NewLineIfNeeded(StringBuilder buffer)
    {
        if (buffer.Length > 0 && buffer[^1] != '\n')
            Console.WriteLine();
    }

    internal static string FormatTimestamp(string iso)
    {
        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return iso;
    }

    internal static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Length <= max ? value : value[..max] + "…";
    }

    internal static string? ExtractTitleFlag(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return null;

        var trimmed = args.Trim();
        if (trimmed.StartsWith("--title", StringComparison.Ordinal))
        {
            var remainder = trimmed["--title".Length..].Trim();
            if (remainder.StartsWith('=')) remainder = remainder[1..].Trim();
            return string.IsNullOrWhiteSpace(remainder) ? null : remainder.Trim('"');
        }

        return trimmed.Trim('"');
    }

    internal static (string? EndpointOverride, string RemainingArgs, string? Error) ParseInvocationOptions(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return (null, string.Empty, null);

        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? endpointOverride = null;
        var index = 0;

        while (index < tokens.Length)
        {
            var token = tokens[index];
            if (token == "--endpoint")
            {
                if (endpointOverride is not null)
                    return (null, string.Empty, "  --endpoint can only be passed once");

                if (index + 1 >= tokens.Length)
                    return (null, string.Empty, "  Missing --endpoint value");

                endpointOverride = NormalizeEndpointOverride(tokens[index + 1], out var error);
                if (error is not null)
                    return (null, string.Empty, error);

                index += 2;
                continue;
            }

            if (token.StartsWith("--endpoint=", StringComparison.Ordinal))
            {
                if (endpointOverride is not null)
                    return (null, string.Empty, "  --endpoint can only be passed once");

                endpointOverride = NormalizeEndpointOverride(token["--endpoint=".Length..], out var error);
                if (error is not null)
                    return (null, string.Empty, error);

                index++;
                continue;
            }

            break;
        }

        var remainingArgs = index >= tokens.Length
            ? string.Empty
            : string.Join(' ', tokens[index..]);
        return (endpointOverride, remainingArgs, null);
    }

    private static string? NormalizeEndpointOverride(string rawValue, out string? error)
    {
        var normalized = AevatarChatSettingsStore.NormalizeBaseUrl(rawValue);
        if (normalized is null)
        {
            error = string.IsNullOrWhiteSpace(rawValue)
                ? "  Missing --endpoint value"
                : $"  Invalid --endpoint value: {rawValue}";
            return null;
        }

        error = null;
        return normalized;
    }

    private static void WriteError(CommandContext context, Exception ex)
    {
        _ = context;
        switch (ex)
        {
            case OperationCanceledException:
                AnsiConsole.MarkupLine("[dim]  (cancelled)[/]");
                break;
            case NotLoggedInException:
                AnsiConsole.MarkupLine($"[red]  ✗ {Markup.Escape(ex.Message)}[/]");
                break;
            case AevatarChatException chatEx:
                AnsiConsole.MarkupLine($"[red]  ✗ {Markup.Escape(chatEx.Message)}[/]");
                break;
            default:
                AnsiConsole.MarkupLine($"[red]  ✗ aevatar error:[/] {Markup.Escape(ex.Message)}");
                break;
        }
    }

    private static void PrintUsage(CommandContext context)
    {
        context.WriteLine("  Usage:");
        context.WriteLine("    /aevatar [--endpoint <url>]             open a REPL (mainnet by default)");
        context.WriteLine("    /aevatar [--endpoint <url>] <message>   send + stream in the current conversation");
        context.WriteLine("    /aevatar [--endpoint <url>] new [title] create a new conversation and make it active");
        context.WriteLine("    /aevatar [--endpoint <url>] list        show saved conversations in the current scope");
        context.WriteLine("    /aevatar [--endpoint <url>] open <id>   switch to a saved conversation + show transcript");
        context.WriteLine("    /aevatar [--endpoint <url>] delete [id] delete a conversation (default: active one)");
        context.WriteLine("    /aevatar config show                    print persisted config");
        context.WriteLine("    /aevatar config set-url <url>           persist aevatar API base URL");
        context.WriteLine("    /aevatar config set-scope <scopeId>     persist scope id");
        context.WriteLine("    /aevatar config clear                   clear persisted base URL");
        context.WriteLine("    /aevatar chat [--endpoint <url>] [--port N] [--no-browser]   start chat-only web UI (default port 6688)");
        context.WriteLine("    /aevatar web  [--endpoint <url>] [--port N] [--no-browser]   start Service Workbench prototype (default port 6689)");
        context.WriteLine("    --endpoint <url>                      use this aevatar base URL for this invocation only");
    }

    internal static (string head, string rest) SplitHead(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return (string.Empty, string.Empty);

        var trimmed = value.TrimStart();
        var spaceIndex = trimmed.IndexOf(' ');
        return spaceIndex < 0
            ? (trimmed, string.Empty)
            : (trimmed[..spaceIndex], trimmed[(spaceIndex + 1)..].Trim());
    }

    // ── Internal state types ──

    private sealed class ChatSession
    {
        public ChatSession(string id, AevatarConversationMeta meta, List<AevatarStoredMessage> messages)
        {
            Id = id;
            Meta = meta;
            Messages = messages;
        }

        public ChatSession(string id, AevatarConversationMeta meta, IEnumerable<AevatarStoredMessage> messages)
            : this(id, meta, messages.ToList())
        {
        }

        public string Id { get; }
        public AevatarConversationMeta Meta { get; set; }
        public List<AevatarStoredMessage> Messages { get; }
    }

    private readonly record struct ReplOutcome(bool Exit, ChatSession? Session)
    {
        public static ReplOutcome Continue { get; } = new(false, null);
        public static ReplOutcome Quit { get; } = new(true, null);
    }
}
