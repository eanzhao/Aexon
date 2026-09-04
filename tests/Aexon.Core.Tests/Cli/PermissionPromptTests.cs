using System.Text.Json;
using Aexon.Cli;

namespace Aexon.Core.Tests.Cli;

/// <summary>
/// Covers permission prompt formatting and risk classification.
/// </summary>
public sealed class PermissionPromptTests
{
    [Fact]
    public void PermissionPrompt_LabelsReadOnlyBashAsSafe()
    {
        var viewModel = PermissionPrompt.BuildViewModel(
            "Bash",
            "Allow command?",
            JsonSerializer.SerializeToElement(new { command = "git status" }));

        Assert.Equal(PermissionRiskLevel.Safe, viewModel.RiskLevel);
        Assert.Equal("green", viewModel.RiskColor);
        Assert.Equal("Command", viewModel.ProminentLabel);
        Assert.Equal("git status", viewModel.ProminentValue);
    }

    [Fact]
    public void PermissionPrompt_LabelsDestructiveBashAsDangerous()
    {
        var viewModel = PermissionPrompt.BuildViewModel(
            "Bash",
            "Allow command?",
            JsonSerializer.SerializeToElement(new { command = "rm -rf build" }));

        Assert.Equal(PermissionRiskLevel.Dangerous, viewModel.RiskLevel);
        Assert.Equal("red", viewModel.RiskColor);
    }

    [Fact]
    public void PermissionPrompt_HighlightsFilePathForFileEdits()
    {
        var viewModel = PermissionPrompt.BuildViewModel(
            "Edit",
            "Allow edit?",
            JsonSerializer.SerializeToElement(new
            {
                file_path = "/tmp/app.cs",
                old_string = "before",
                new_string = "after",
            }));

        Assert.Equal(PermissionRiskLevel.Caution, viewModel.RiskLevel);
        Assert.Equal("File", viewModel.ProminentLabel);
        Assert.Equal("/tmp/app.cs", viewModel.ProminentValue);
    }
}
