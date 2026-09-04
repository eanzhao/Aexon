using System.Globalization;

namespace Aexon.Cli;

internal sealed record CliOptions(
    bool ShowHelp,
    bool ContinueLatest,
    bool ForkSession,
    bool PrintMode,
    bool UseNyxId,
    string? WorkingDirectory,
    string? Model,
    string? Provider,
    string? ResumeTarget,
    string? SettingsPath,
    int? MaxTurns,
    NonInteractiveOutputFormat OutputFormat,
    NonInteractiveApprovalMode ApprovalMode,
    string? InitialPrompt,
    string? ParseError)
{
    public static CliOptions Parse(string[] args)
    {
        var showHelp = false;
        var continueLatest = false;
        var forkSession = false;
        var printMode = false;
        var useNyxId = false;
        string? workingDirectory = null;
        string? model = null;
        string? provider = null;
        string? resumeTarget = null;
        string? settingsPath = null;
        int? maxTurns = null;
        var outputFormat = NonInteractiveOutputFormat.Text;
        var approvalMode = NonInteractiveApprovalMode.Deny;
        var remaining = new List<string>();
        string? parseError = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help":
                case "-h":
                    showHelp = true;
                    break;

                case "--print":
                case "-p":
                    printMode = true;
                    break;

                case "--nyxid":
                    useNyxId = true;
                    break;

                case "--cwd":
                    if (!TryReadValue(args, ref i, "--cwd", out workingDirectory, out parseError))
                        goto Done;
                    break;

                case "--resume":
                    if (!TryReadValue(args, ref i, "--resume", out resumeTarget, out parseError))
                        goto Done;
                    break;

                case "--continue":
                    continueLatest = true;
                    break;

                case "--fork-session":
                    forkSession = true;
                    break;

                case "--model":
                case "-m":
                    if (!TryReadValue(args, ref i, args[i], out model, out parseError))
                        goto Done;
                    break;

                case "--provider":
                    if (!TryReadValue(args, ref i, "--provider", out provider, out parseError))
                        goto Done;
                    break;

                case "--settings":
                case "--mcp-config":
                    if (!TryReadValue(args, ref i, args[i], out settingsPath, out parseError))
                        goto Done;
                    break;

                case "--max-turns":
                    if (!TryReadValue(args, ref i, "--max-turns", out var maxTurnsRaw, out parseError))
                        goto Done;

                    if (!int.TryParse(maxTurnsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMaxTurns) ||
                        parsedMaxTurns <= 0)
                    {
                        parseError = "--max-turns 必须是正整数。";
                        goto Done;
                    }

                    maxTurns = parsedMaxTurns;
                    break;

                case "--output-format":
                    if (!TryReadValue(args, ref i, "--output-format", out var outputFormatRaw, out parseError))
                        goto Done;

                    if (!TryParseOutputFormat(outputFormatRaw, out outputFormat))
                    {
                        parseError = "--output-format 只支持 text、markdown 或 json。";
                        goto Done;
                    }

                    break;

                case "--approval-mode":
                    if (!TryReadValue(args, ref i, "--approval-mode", out var approvalModeRaw, out parseError))
                        goto Done;

                    if (!TryParseApprovalMode(approvalModeRaw, out approvalMode))
                    {
                        parseError = "--approval-mode 只支持 allow 或 deny。";
                        goto Done;
                    }

                    break;

                default:
                    remaining.Add(args[i]);
                    break;
            }
        }

    Done:
        return new CliOptions(
            showHelp,
            continueLatest,
            forkSession,
            printMode,
            useNyxId,
            workingDirectory,
            model,
            provider,
            resumeTarget,
            settingsPath,
            maxTurns,
            outputFormat,
            approvalMode,
            remaining.Count > 0 ? string.Join(' ', remaining) : null,
            parseError);
    }

    private static bool TryReadValue(
        string[] args,
        ref int index,
        string optionName,
        out string? value,
        out string? parseError)
    {
        if (index + 1 < args.Length)
        {
            value = args[++index];
            parseError = null;
            return true;
        }

        value = null;
        parseError = $"{optionName} 缺少参数值。";
        return false;
    }

    private static bool TryParseOutputFormat(
        string? value,
        out NonInteractiveOutputFormat format)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "text":
                format = NonInteractiveOutputFormat.Text;
                return true;
            case "markdown":
            case "md":
                format = NonInteractiveOutputFormat.Markdown;
                return true;
            case "json":
                format = NonInteractiveOutputFormat.Json;
                return true;
            default:
                format = NonInteractiveOutputFormat.Text;
                return false;
        }
    }

    private static bool TryParseApprovalMode(
        string? value,
        out NonInteractiveApprovalMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "allow":
                mode = NonInteractiveApprovalMode.Allow;
                return true;
            case "deny":
                mode = NonInteractiveApprovalMode.Deny;
                return true;
            default:
                mode = NonInteractiveApprovalMode.Deny;
                return false;
        }
    }
}
