using System.Text.RegularExpressions;

namespace Aexon.Core.Permissions;

/// <summary>
/// Defines command risk levels used by permission prompts.
/// </summary>
public enum DangerousCommandRiskLevel
{
    Safe,
    Caution,
    Dangerous,
}

/// <summary>
/// Represents a dangerous-command assessment.
/// </summary>
public sealed record DangerousCommandAssessment(
    DangerousCommandRiskLevel RiskLevel,
    string? Reason = null);

/// <summary>
/// Detects shell commands that deserve higher-friction permission prompts.
/// </summary>
public static class DangerousCommandDetector
{
    private static readonly IReadOnlyList<DetectionRule> Rules =
    [
        new(
            DangerousCommandRiskLevel.Dangerous,
            "Recursive force deletion",
            CreateRegex(@"\brm\s+-rf\b|\brm\s+-fr\b|\brm\s+-r\s+-f\b|\brm\s+-f\s+-r\b")),
        new(
            DangerousCommandRiskLevel.Dangerous,
            "Forced git push",
            CreateRegex(@"\bgit\s+push\b[^;&|\r\n]*(--force(?:-with-lease)?|-f)\b")),
        new(
            DangerousCommandRiskLevel.Dangerous,
            "Hard git reset",
            CreateRegex(@"\bgit\s+reset\b[^;&|\r\n]*\s--hard\b")),
        new(
            DangerousCommandRiskLevel.Dangerous,
            "Dangerous SQL schema change",
            CreateRegex(@"\bDROP\s+TABLE\b")),
        new(
            DangerousCommandRiskLevel.Dangerous,
            "Dangerous SQL data deletion",
            CreateRegex(@"\bDELETE\s+FROM\b")),
        new(
            DangerousCommandRiskLevel.Dangerous,
            "World-writable chmod",
            CreateRegex(@"\bchmod\b(?:\s+-[^\s]+\b)*\s+777\b")),
        new(
            DangerousCommandRiskLevel.Dangerous,
            "Force kill",
            CreateRegex(@"\bkill\s+-9\b")),
        new(
            DangerousCommandRiskLevel.Dangerous,
            "Filesystem formatting",
            CreateRegex(@"\bmkfs(?:\.[a-z0-9_-]+)?\b")),
        new(
            DangerousCommandRiskLevel.Dangerous,
            "Low-level disk copy",
            CreateRegex(@"(^|[;&|\s])dd(\s|$)")),
        new(
            DangerousCommandRiskLevel.Caution,
            "Network mutation via curl",
            CreateRegex(@"\bcurl\b[^;&|\r\n]*(?:-X|--request)\s*['""]?(POST|PUT|DELETE)['""]?\b")),
        new(
            DangerousCommandRiskLevel.Caution,
            "Network mutation via wget",
            CreateRegex(@"\bwget\b[^;&|\r\n]*--post(?:-data|-file)?\b")),
    ];

    public static DangerousCommandAssessment Classify(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new DangerousCommandAssessment(DangerousCommandRiskLevel.Safe);

        DetectionRule? matchedRule = null;
        foreach (var rule in Rules)
        {
            if (!rule.Pattern.IsMatch(command))
                continue;

            if (matchedRule == null || rule.RiskLevel > matchedRule.RiskLevel)
                matchedRule = rule;

            if (rule.RiskLevel == DangerousCommandRiskLevel.Dangerous)
                break;
        }

        return matchedRule == null
            ? new DangerousCommandAssessment(DangerousCommandRiskLevel.Safe)
            : new DangerousCommandAssessment(matchedRule.RiskLevel, matchedRule.Reason);
    }

    private static Regex CreateRegex(string pattern) =>
        new(
            pattern,
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant |
            RegexOptions.Singleline |
            RegexOptions.Compiled);

    private sealed record DetectionRule(
        DangerousCommandRiskLevel RiskLevel,
        string Reason,
        Regex Pattern);
}
