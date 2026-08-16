using Argus.Server.Input;
using Argus.Server.Windows;

namespace Argus.Server.Tests;

/// <summary>
/// These run against the real desktop, so they assert on invariants rather than on a fixed list -
/// whatever the machine happens to have open must satisfy them.
/// </summary>
public class WindowEnumerationTests
{
    [Fact]
    public void Enumerate_returns_only_usable_windows()
    {
        foreach (var window in WindowEnumerator.Enumerate())
        {
            Assert.NotEqual(0, window.Handle);
            Assert.False(string.IsNullOrWhiteSpace(window.Title));
            Assert.False(string.IsNullOrWhiteSpace(window.ProcessName));
            Assert.True(window.ProcessId > 0);
            Assert.True(window.Width >= 80, $"{window.Title} was {window.Width} wide");
            Assert.True(window.Height >= 80, $"{window.Title} was {window.Height} tall");
        }
    }

    [Fact]
    public void Enumerate_does_not_return_duplicate_handles()
    {
        var handles = WindowEnumerator.Enumerate().Select(w => w.Handle).ToList();
        Assert.Equal(handles.Count, handles.Distinct().Count());
    }

    [Fact]
    public void Enumerate_excludes_the_desktop_and_taskbar_shells()
    {
        var classes = WindowEnumerator.Enumerate().Select(w => w.ClassName).ToList();

        Assert.DoesNotContain("Progman", classes);
        Assert.DoesNotContain("Shell_TrayWnd", classes);
        Assert.DoesNotContain("WorkerW", classes);
    }

    [Fact]
    public void Describe_returns_null_for_a_handle_that_is_not_a_window()
    {
        Assert.Null(WindowEnumerator.Describe(0));
        Assert.Null(WindowEnumerator.Describe(-1));
    }

    [Fact]
    public void Describe_agrees_with_Enumerate_for_a_live_window()
    {
        var first = WindowEnumerator.Enumerate().FirstOrDefault();
        if (first is null) return;   // headless machine - nothing to compare against

        var described = WindowEnumerator.Describe(first.Handle);

        Assert.NotNull(described);
        Assert.Equal(first.Handle, described!.Handle);
        Assert.Equal(first.ProcessName, described.ProcessName);
    }

    [Fact]
    public void MatchKey_combines_process_and_title()
    {
        var window = SampleWindow("notepad++", "*new 1 - Notepad++");
        Assert.Equal("notepad++|*new 1 - Notepad++", window.MatchKey);
    }

    [Fact]
    public void FindByMatchKey_prefers_an_exact_title_match()
    {
        var candidates = new[]
        {
            SampleWindow("notepad++", "other.txt - Notepad++", handle: 1),
            SampleWindow("notepad++", "wanted.txt - Notepad++", handle: 2),
        };

        var found = WindowEnumerator.FindByMatchKey("notepad++|wanted.txt - Notepad++", candidates);

        Assert.NotNull(found);
        Assert.Equal(2, found!.Handle);
    }

    [Fact]
    public void FindByMatchKey_falls_back_to_the_same_process_when_the_title_changed()
    {
        // Editors retitle themselves constantly; losing the tile every time a file is saved would
        // make the dashboard useless.
        var candidates = new[] { SampleWindow("notepad++", "different.txt - Notepad++", handle: 9) };

        var found = WindowEnumerator.FindByMatchKey("notepad++|gone.txt - Notepad++", candidates);

        Assert.NotNull(found);
        Assert.Equal(9, found!.Handle);
    }

    [Fact]
    public void FindByMatchKey_returns_null_when_the_process_is_not_running()
    {
        var candidates = new[] { SampleWindow("notepad++", "a - Notepad++") };
        Assert.Null(WindowEnumerator.FindByMatchKey("someothereditor|a", candidates));
    }

    [Fact]
    public void FindByMatchKey_tolerates_a_malformed_key()
    {
        var candidates = new[] { SampleWindow("notepad++", "a - Notepad++") };
        Assert.Null(WindowEnumerator.FindByMatchKey("nopipehere", candidates));
    }

    [Theory]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS")]   // Windows Terminal - verified silent failure
    [InlineData("Chrome_WidgetWin_1")]              // Chromium / Electron - verified silent failure
    public void Classes_that_swallow_posted_keys_are_classified_as_needing_focus_mode(string className)
    {
        Assert.False(WindowEnumerator.SupportsBackgroundInput(className));

        var window = SampleWindow("whatever", "title", className: className);
        Assert.NotNull(InputRouter.BackgroundUnsupportedReason(window));
    }

    [Theory]
    [InlineData("Notepad++")]
    [InlineData("MSPaintApp")]
    [InlineData("CabinetWClass")]
    public void Ordinary_win32_classes_allow_background_input(string className)
    {
        Assert.True(WindowEnumerator.SupportsBackgroundInput(className));

        var window = SampleWindow("notepad++", "a - Notepad++", className: className);
        Assert.Null(InputRouter.BackgroundUnsupportedReason(window));
    }

    [Fact]
    public void Classic_conhost_is_recognised_as_a_console()
    {
        Assert.True(WindowEnumerator.IsConsoleClass("ConsoleWindowClass"));
        Assert.False(WindowEnumerator.IsConsoleClass("CASCADIA_HOSTING_WINDOW_CLASS"));
    }

    [Fact]
    public void A_console_window_is_never_reported_as_needing_focus_mode()
    {
        // Consoles route through WriteConsoleInput, which works without focus.
        var console = SampleWindow("powershell", "Windows PowerShell", className: "ConsoleWindowClass");
        Assert.Null(InputRouter.BackgroundUnsupportedReason(console with { IsConsole = true }));
    }

    private static WindowInfo SampleWindow(
        string processName,
        string title,
        long handle = 1,
        string className = "SomeClass") => new()
    {
        Handle = handle,
        Title = title,
        ProcessName = processName,
        ProcessId = 100,
        ClassName = className,
        Width = 800,
        Height = 600,
        IsConsole = WindowEnumerator.IsConsoleClass(className),
        SupportsBackgroundInput = WindowEnumerator.SupportsBackgroundInput(className),
    };
}
