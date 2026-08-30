using System.Diagnostics;
using Argus.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Argus.Server.Tests;

/// <summary>
/// What each "Open with" choice actually starts.
///
/// These check the argument string rather than launching anything, on purpose: the bug they exist
/// to catch is a terminal being handed no argument at all, which does not throw, does not log, and
/// looks from the browser exactly like success - a prompt opens in the right folder and the script
/// you tapped never runs.
/// </summary>
public class OpenWithLauncherTests
{
    private const string Folder = @"D:\Work\git\mynrd\mahjong";
    private const string Script = @"D:\Work\git\mynrd\mahjong\run.ps1";
    private const string Batch = @"D:\Work\git\mynrd\mahjong\build.bat";
    private const string Text = @"D:\Work\git\mynrd\mahjong\README.md";

    private static ProcessStartInfo Plan(string target, string app, bool isDirectory = false) =>
        OpenWithLauncher.Plan(target, isDirectory, app);

    private static OpenWithLauncher Launcher() => new(NullLogger<OpenWithLauncher>.Instance);

    [Fact]
    public void PowerShell_runs_a_ps1_rather_than_opening_a_prompt_beside_it()
    {
        // The reported bug: picking PowerShell on run.ps1 opened pwsh in the right folder and
        // stopped there.
        var plan = Plan(Script, "powershell");

        Assert.Equal("pwsh.exe", plan.FileName);
        Assert.Equal($"-NoExit -File \"{Script}\"", plan.Arguments);
        Assert.Equal(Folder, plan.WorkingDirectory);
    }

    [Fact]
    public void Command_prompt_runs_a_bat()
    {
        var plan = Plan(Batch, "cmd");

        Assert.Equal("cmd.exe", plan.FileName);
        Assert.Equal($"/S /K \"\"{Batch}\"\"", plan.Arguments);
        Assert.Equal(Folder, plan.WorkingDirectory);
    }

    [Fact]
    public void Command_prompt_runs_a_cmd_file_too()
    {
        const string target = @"C:\tools\deploy.cmd";

        Assert.Equal($"/S /K \"\"{target}\"\"", Plan(target, "cmd").Arguments);
    }

    [Theory]
    [InlineData(@"C:\tools\RUN.PS1")]
    [InlineData(@"C:\tools\Run.Ps1")]
    public void The_extension_check_ignores_case(string target)
    {
        Assert.Contains("-File", Plan(target, "powershell").Arguments);
    }

    [Fact]
    public void A_path_holding_an_ampersand_keeps_its_quotes_through_cmd()
    {
        // Without /S, cmd preserves the quotes only when the path holds none of &<>()@^| - so an
        // "R&D" folder would have its quotes stripped and the command cut at the ampersand.
        const string target = @"C:\R&D\build.bat";

        Assert.Equal($"/S /K \"\"{target}\"\"", Plan(target, "cmd").Arguments);
    }

    [Fact]
    public void PowerShell_on_something_it_cannot_run_still_opens_a_prompt_at_the_folder()
    {
        var plan = Plan(Text, "powershell");

        Assert.Equal(string.Empty, plan.Arguments);
        Assert.Equal(Folder, plan.WorkingDirectory);
    }

    [Fact]
    public void Command_prompt_on_something_it_cannot_run_still_opens_a_prompt_at_the_folder()
    {
        var plan = Plan(Text, "cmd");

        Assert.Equal("/K", plan.Arguments);
        Assert.Equal(Folder, plan.WorkingDirectory);
    }

    [Theory]
    [InlineData("powershell", "")]
    [InlineData("cmd", "/K")]
    public void A_terminal_on_a_folder_opens_there_and_runs_nothing(string app, string expected)
    {
        // There is no script to run, and a folder is not one - so neither terminal should try.
        var plan = Plan(Folder, app, isDirectory: true);

        Assert.Equal(expected, plan.Arguments);
        Assert.Equal(Folder, plan.WorkingDirectory);
    }

    [Fact]
    public void A_ps1_handed_to_the_command_prompt_is_not_run()
    {
        // cmd cannot execute a .ps1, and /K on one opens a window that only complains.
        Assert.Equal("/K", Plan(Script, "cmd").Arguments);
    }

    [Fact]
    public void A_bat_handed_to_PowerShell_is_not_run()
    {
        // pwsh -File takes a .ps1 only; handing it a .bat fails rather than running it.
        Assert.Equal(string.Empty, Plan(Batch, "powershell").Arguments);
    }

    [Fact]
    public void Claude_gets_a_terminal_at_the_folder_whatever_the_target_is()
    {
        // It works on a folder, so a file only decides which folder - including a script, which it
        // should not be made to execute.
        Assert.Equal("-NoExit -Command claude", Plan(Script, "claude").Arguments);
        Assert.Equal(Folder, Plan(Script, "claude").WorkingDirectory);
        Assert.Equal(Folder, Plan(Folder, "claude", isDirectory: true).WorkingDirectory);
    }

    [Fact]
    public void VS_Code_is_handed_the_target_itself_not_its_folder()
    {
        Assert.Equal("code", Plan(Script, "vscode").FileName);
        Assert.Equal($"\"{Script}\"", Plan(Script, "vscode").Arguments);
        Assert.Equal($"\"{Folder}\"", Plan(Folder, "vscode", isDirectory: true).Arguments);
    }

    [Fact]
    public void Explorer_highlights_a_file_but_opens_a_folder()
    {
        // /select, on a folder would open its parent with the folder highlighted, which is one
        // level up from what was asked for.
        Assert.Equal($"/select,\"{Script}\"", Plan(Script, "explorer").Arguments);
        Assert.Equal($"\"{Folder}\"", Plan(Folder, "explorer", isDirectory: true).Arguments);
    }

    [Fact]
    public void The_default_app_is_the_shell_association_for_the_target()
    {
        var plan = Plan(Text, OpenWithLauncher.DefaultApp);

        Assert.Equal(Text, plan.FileName);
        Assert.Equal(string.Empty, plan.Arguments);
    }

    [Fact]
    public void An_unknown_app_key_falls_back_to_the_default_association()
    {
        Assert.Equal(Text, Plan(Text, "notepad-plus-plus-maybe").FileName);
    }

    [Fact]
    public void A_file_at_the_root_of_a_drive_keeps_the_drive_as_its_folder()
    {
        // A bad guess here leaves WorkingDirectory pointing at the file, and Process.Start refuses
        // to start at all.
        Assert.Equal(@"C:\", Plan(@"C:\autoexec.bat", "cmd").WorkingDirectory);
    }

    [Fact]
    public void Every_app_the_page_offers_starts_the_app_rather_than_the_file()
    {
        // The list in the UI and the switch that acts on it are separate. A key added to one and
        // not the other falls through to the default association, which for a .ps1 is Notepad -
        // silently, and only for the app that was forgotten.
        foreach (var app in OpenWithLauncher.Apps.Where(a => a.Key != OpenWithLauncher.DefaultApp))
        {
            var plan = Plan(Script, app.Key);

            Assert.NotEqual(Script, plan.FileName);
        }
    }

    [Fact]
    public void A_path_that_does_not_exist_is_refused_before_anything_is_started()
    {
        var result = Launcher().Open(@"D:\nothing\here.ps1", "powershell");

        Assert.False(result.Started);
        Assert.Contains("no longer exists", result.Reason);
    }

    [Fact]
    public void An_empty_path_is_refused()
    {
        Assert.False(Launcher().Open("   ", "powershell").Started);
    }
}
