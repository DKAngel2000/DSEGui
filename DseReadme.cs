using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Navigation;
using BodyBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.SolidColorBrush;
using MonoFont = System.Windows.Media.FontFamily;

namespace DseGui;

// ─── dse readme ───────────────────────────────────────────────────────────────

// Snapshot of https://github.com/SouNobbao/dse/blob/master/readme.md (2026-11-07).
// Kept as a string so nothing extra has to ship with the single-file publish.
internal static class DseReadme
{
    public const string SourceUrl = "https://github.com/SouNobbao/dse/blob/master/readme.md";
    public const string SnapshotLabel = "7 Nov 2026";

    public const string Markdown =
"""
# dse

## What dse does

The DLL extracts a [temporary executable](https://github.com/SouNobbao/dse/blob/master/elevator.cpp) that's compiled on the action build of this repo.

Upon opening the game, it checks if you're running under the test-signed setup or you alterady have Driver Signature Enforncement previously off. [checks are here](https://github.com/SouNobbao/dse/blob/master/checks.cpp)

Other related checks are HVCI/VBS, this tool won't turn off those, make sure you've disabled them before running.

Upon opening it disables dse and hooks over to CreateWindowExW, ensuring that the program is opened before turning dse back on.

`CreateProcess`/`CreateProcessWithTokenW` are only hooked to load dse.dll for that game in case of a launcher.

## How to use

1. Add the DLL to the load list (`xxxxex.ini`, `xxxowo.ini`, `xxxxxact.json`) example setups.
    ### Example setups
    ```
    xxxex.ini
    [load_dlls]
    0=dse.dll
    ```

    ```
    xxxowo.ini
    [LoadDlls]
    0=dse.dll
    ```

    ```
    xxxxxact.json
    "load_dlls": ["dse.dll"],
    ```
2. Configure `dse.ini` if needed.

## Running without showing the prompt
1. DSE can now indentify if a game can have its UAC prompt skipped, create a task like this
    ```powershell
    $exe  = "C:\FullPathToExe"
    $name = "dse_" + [IO.Path]::GetFileNameWithoutExtension($exe)

    # Create a new task
    $action    = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
    
    # Setup at which level the task/executable should run
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -RunLevel Highest -LogonType Interactive

    # Configure the task settings
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)

    # Register the task
    Register-ScheduledTask -TaskName $name -Force -Action $action -Principal $principal -Settings $settings
    ```
2. dse can now skip prompts
    > Note that dse will not create a task for you, nor try to bypass UAC. If the task is not found dse_elevator will be ran.

> [!WARNING]    
> Its mandatory using runlevel highest, setting the correct working directory and exe, else dse will ignore the task 

## Steam config setup (overlay + input)

1. Enable in config (`dse.ini`) file `steamHooks=1/true/yes`.
    ```ini
    [dse]
    ; Automatically toggle DSE. If DSE was already disabled
    ; patcher will not run.
    toggleDse=true

    ; Enable/disable Steam API hooks
    steamHooks=true

    ; Enable coldloader support (for games that need it)
    coldloaderhooks=false
    steam_path=<YOUR_STEAM_PATH>

    ; Forward emulator steam_api64.dll exports to the real steam_api64_o.dll next to it
    steamApiForward=false

    ; Skip warning prompts and proceed automatically
    ignoreWarnings=false

    ; Enable/disable logging output
    logging=false

    ; Its dependent on toggleDse
    ; Problematic services to stop or delete (Format: ServiceName:ACTION)
    ; STOP - just stops the service, and relaunches once game ends
    ; DELETE - deletes the service, and does not relaunch
    problematic_services=vgk:STOP, vgc:STOP, FACEIT:DELETE

    ; Problematic tasks to kill (comma separated executable names, these are not relaunched)
    problematic_tasks=vgtray.exe,
    ```
2. Add the DLL to the load list.
3. Remove `coldclient/loader.dll`.
4. Use your favorite steamtool.
5. Click Play.

### In cases where the game only works with coldclient setups
1. Enable in config (`dse.ini`) file `steamHooks=1/true/yes` and `coldloaderhooks=1/true/yes`

    1.1 Make sure that `steam_path` is set to your steam path, and you have `coldclient` near dse.dll or exe 
    ```ini
    [dse]
    ; Automatically toggle DSE. If DSE was already disabled
    ; patcher will not run.
    toggleDse=true

    ; Enable/disable Steam API hooks
    steamHooks=true

    ; Enable coldloader support (for games that need it)
    coldloaderhooks=true
    steam_path=<YOUR_STEAM_PATH>

    ; Forward emulator steam_api64.dll exports to the real steam_api64_o.dll next to it
    steamApiForward=false

    ; Skip warning prompts and proceed automatically
    ignoreWarnings=false

    ; Enable/disable logging output
    logging=false

    ; Its dependent on toggleDse
    ; Problematic services to stop or delete (Format: ServiceName:ACTION)
    ; STOP - just stops the service, and relaunches once game ends
    ; DELETE - deletes the service, and does not relaunch
    problematic_services=vgk:STOP, vgc:STOP, FACEIT:DELETE

    ; Problematic tasks to kill (comma separated executable names, these are not relaunched)
    problematic_tasks=vgtray.exe,
    ```
2. Make sure that coldclient/loader is always loaded before `dse.dll`
    Example `someone.ini`
    ```ini
    [LoadDlls]
    0=coldclient\coldloader.dll
    1=coldclient\dse.dll
    ```

> [!NOTE]
> This creates a hardlink (copy if it fails) of `steamclient64.dll` named `steamclient64_valve.dll` in your steam folder

3. Use your favorite steamtool.
4. Click Play.

### Last scenario
1. Place steam_api64_o.dll near emulated steam_api64.dll used by the game, turn on 
    ```ini
    [dse]
    ; Automatically toggle DSE. If DSE was already disabled
    ; patcher will not run.
    toggleDse=true

    ; Enable/disable Steam API hooks
    steamHooks=true

    ; Enable coldloader support (for games that need it)
    coldloaderhooks=false
    steam_path=<YOUR_STEAM_PATH>

    ; Forward emulator steam_api64.dll exports to the real steam_api64_o.dll next to it
    steamApiForward=true

    ; Skip warning prompts and proceed automatically
    ignoreWarnings=false

    ; Enable/disable logging output
    logging=false

    ; Its dependent on toggleDse
    ; Problematic services to stop or delete (Format: ServiceName:ACTION)
    ; STOP - just stops the service, and relaunches once game ends
    ; DELETE - deletes the service, and does not relaunch
    problematic_services=vgk:STOP, vgc:STOP, FACEIT:DELETE

    ; Problematic tasks to kill (comma separated executable names, these are not relaunched)
    problematic_tasks=vgtray.exe,
    ```
2. Use your favorite steamtool.
3. Click Play.

## Linking the DLL for launchers or without loaders

### CFF Explorer (GUI)
1. Open your exe/dll in [CFF Explorer](https://ntcore.com/explorer-suite/)
2. Go to Import Adder.
3. Click Add and browse to `dse.dll`.
4. Select the export `DseDll` and import by name.
5. Click Rebuild Import Table and save the exe or dll.
6. Drop `dse.dll` next to the exe/dll.

### PEFile (TUI)

1. `pe_reader.exe` is used to check which dlls are linked on the exe
2. Download `pefile.exe` in releases
3. Run pefile.exe with the correct arguments
4. Command Example
    > `pefile.exe example.exe/dll dse.dll DseDll`
5. Get the example_mod.exe/dll file
6. Rename to example.exe/dll

You can also use the pre-linked launchers from the release build.

## Currently supported launchers

- hypervisor-launcher.exe
- steamclient_loader_x64.exe

> [!NOTE]
> To add more launchers, add their configuration under `launchers.h`.

## Disclaimer 

You, as the user, you're responsible for any misuse of this software. The authors condone the use of this software for any malicious purposes.

### Issues

Before submitting any issues please make sure following:
- you are sure that the dll was loaded
- where this dll was used, and how it was loaded (e.g. from a launcher, from another dll or linked)
- you are sure that you don't have external programs interfering with dse.dll (e.g. MSI Afterburner, Rivatuner, Anti-Cheat Systems), and if they were, you are sure that `dse.dll` closed the interfering programs.
- you are sure that `dse.ini` is correctly configured, for your use case.
- you are sure that there is not any issue with the game or launcher (e.g. corrupt game files, outdated launcher)
- you are sure that the launcher is not renamed and corresponds to [launchers.h](https://codeberg.org/sounobbao/dse/src/branch/master/launchers.h#L17-L22)
- you've read the readme

If all above are true, and you still have issues, please submit an issue with all the information requested above and make sure that `logging=true` is enabled on `dse.ini` and you attach the log file

### External Programs

The files provided in `extras` come from the following places

- pe_reader > [LIEF](https://github.com/lief-project/LIEF)
- steamclient_loader_x64.exe > [gbe_fork](https://github.com/Detanup01/gbe_fork/)
- hypervisor-launcher.exe > [hypervisor-launcher](https://git.denuvosanctuary.com/andreh/hypervisor-launcher/)
- rtcore driver used is from [drvloader](https://github.com/wesmar/KernelResearchKit/blob/main/drvloader/ICON/kvc.ico) > yes the ico is the driver don't ask me why.

### Inspiration(s)

- [VXR/HV-PlugNPlay](https://codeberg.org/vrx/HV-PlugNPlay) - checks and others needed.
- [SOULX3X/HV-Launcher](https://codeberg.org/SOULX3X/HV-Launcher) - hv-admin auto escalation.
""";
}

// ─── Minimal markdown renderer ────────────────────────────────────────────────

internal static class ReadmeRenderer
{
    private static readonly MonoFont Mono = new("Consolas");

    private static readonly BodyBrush BodyBrush     = Freeze("#CCCCCC");
    private static readonly BodyBrush MutedBrush    = Freeze("#888888");
    private static readonly BodyBrush AccentBrush   = Freeze("#B56BDB");
    private static readonly BodyBrush SoftBrush     = Freeze("#F0F0F0");
    private static readonly BodyBrush CodeBrush     = Freeze("#E6C07B");
    private static readonly BodyBrush CodeBack      = Freeze("#1A1A1A");
    private static readonly BodyBrush WarningBrush  = Freeze("#E0C060");
    private static readonly BodyBrush LinkBrush     = Freeze("#6FB8FF");

    private static MediaBrush Freeze(string hex)
    {
        var brush = new MediaBrush((System.Windows.Media.Color)
            System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static readonly Regex ListPattern =
        new(@"^([-*+]|\d+\.)\s+(.*)$", RegexOptions.Compiled);

    private static readonly Regex Fence = new(@"^```", RegexOptions.Compiled);

    public static FlowDocument Build(string markdown)
    {
        var doc = new FlowDocument
        {
            FontFamily = Mono,
            FontSize   = 12,
            Foreground = BodyBrush,
            PagePadding = new Thickness(0, 0, 10, 12),
            ColumnWidth = 4000
        };

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            var trimmed = lines[i].TrimStart();

            if (trimmed.Length == 0) { i++; continue; }

            if (Fence.IsMatch(trimmed))
            {
                i++;
                var code = new List<string>();
                while (i < lines.Length && !Fence.IsMatch(lines[i].TrimStart()))
                {
                    code.Add(lines[i]);
                    i++;
                }
                i++;   // closing fence
                doc.Blocks.Add(CodeBlock(code));
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#') level++;
                doc.Blocks.Add(Heading(trimmed[level..].Trim(), level));
                i++;
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                var quote = new List<string>();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    var quoted = lines[i].TrimStart()[1..];
                    quote.Add(quoted.StartsWith(' ') ? quoted[1..] : quoted);
                    i++;
                }
                doc.Blocks.Add(Quote(quote));
                continue;
            }

            if (trimmed is "---" or "***" or "___")
            {
                doc.Blocks.Add(new Paragraph
                {
                    Margin = new Thickness(0, 6, 0, 10),
                    BorderBrush = CodeBack,
                    BorderThickness = new Thickness(0, 0, 0, 1)
                });
                i++;
                continue;
            }

            var item = ListPattern.Match(trimmed);
            if (item.Success)
            {
                // Rendered as an indented paragraph with the source marker kept verbatim, so
                // numbering survives fences and nested steps (FlowDocument list markers are
                // fiddly to align reliably inside a fixed-width column).
                var marker = item.Groups[1].Value;
                var paragraph = new Paragraph { Margin = new Thickness(16, 0, 0, 4) };
                paragraph.Inlines.Add(new Run(marker is "-" or "*" or "+" ? "•  " : marker + "  ") { Foreground = MutedBrush });
                AddInline(paragraph, item.Groups[2].Value.TrimEnd());
                doc.Blocks.Add(paragraph);
                i++;
                continue;
            }

            var body = new List<string>();
            while (i < lines.Length)
            {
                var peek = lines[i].TrimStart();
                if (peek.Length == 0 || Fence.IsMatch(peek) || peek.StartsWith('#') ||
                    peek.StartsWith('>') || ListPattern.IsMatch(peek)) break;
                body.Add(peek.TrimEnd());
                i++;
            }

            var text = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
            AddInline(text, string.Join(" ", body));
            doc.Blocks.Add(text);
        }

        return doc;
    }

    private static Paragraph Heading(string text, int level)
    {
        var paragraph = new Paragraph
        {
            FontSize = level <= 1 ? 17 : level == 2 ? 14 : 12.5,
            Foreground = level <= 2 ? AccentBrush : SoftBrush,
            Margin = new Thickness(0, level <= 1 ? 0 : 14, 0, 6)
        };
        paragraph.Inlines.Add(new Bold(new Run(text)));
        return paragraph;
    }

    private static Paragraph CodeBlock(IReadOnlyList<string> lines)
    {
        // Trim the common indentation so nested fences render flush.
        var indent = int.MaxValue;
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0) continue;
            var spaces = line.Length - line.TrimStart().Length;
            if (spaces < indent) indent = spaces;
        }
        if (indent == int.MaxValue) indent = 0;

        var content = new List<string>(lines.Count);
        foreach (var line in lines)
            content.Add(line.Length >= indent ? line[indent..] : line.TrimStart());

        return new Paragraph(new Run(string.Join("\n", content)))
        {
            FontFamily = Mono,
            Foreground = BodyBrush,
            Background = CodeBack,
            Margin = new Thickness(12, 2, 0, 12),
            Padding = new Thickness(10, 8, 10, 8)
        };
    }

    private static Paragraph Quote(IReadOnlyList<string> lines)
    {
        var warning = lines.Count > 0 && lines[0].StartsWith("[!", StringComparison.Ordinal);
        var paragraph = new Paragraph
        {
            Foreground = warning ? WarningBrush : MutedBrush,
            Margin = new Thickness(12, 2, 0, 12),
            Padding = new Thickness(10, 4, 0, 4),
            BorderBrush = warning ? WarningBrush : AccentBrush,
            BorderThickness = new Thickness(3, 0, 0, 0)
        };

        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) paragraph.Inlines.Add(new LineBreak());
            AddInline(paragraph, lines[i].TrimEnd());
        }

        return paragraph;
    }

    private static void AddInline(Paragraph paragraph, string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close > 0 && close + 1 < text.Length && text[close + 1] == '(')
                {
                    var end = text.IndexOf(')', close + 2);
                    if (end > 0)
                    {
                        paragraph.Inlines.Add(Link(text[(i + 1)..close], text[(close + 2)..end]));
                        i = end + 1;
                        continue;
                    }
                }
            }

            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    paragraph.Inlines.Add(new Run(text[(i + 1)..end])
                    {
                        FontFamily = Mono,
                        Foreground = CodeBrush,
                        Background = CodeBack
                    });
                    i = end + 1;
                    continue;
                }
            }

            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    paragraph.Inlines.Add(new Bold(new Run(text[(i + 2)..end])));
                    i = end + 2;
                    continue;
                }
            }

            var next = i + 1;
            while (next < text.Length && text[next] != '[' && text[next] != '`' &&
                   !(text[next] == '*' && next + 1 < text.Length && text[next + 1] == '*'))
                next++;

            paragraph.Inlines.Add(new Run(text[i..next]));
            i = next;
        }
    }

    private static Hyperlink Link(string label, string url)
    {
        Hyperlink link;
        try
        {
            link = new Hyperlink(new Run(label)) { NavigateUri = new Uri(url, UriKind.RelativeOrAbsolute) };
        }
        catch
        {
            return new Hyperlink(new Run(label));
        }

        link.Foreground = LinkBrush;
        link.ToolTip = url;
        link.RequestNavigate += (_, e) =>
        {
            try
            {
                if (e.Uri is not null)
                    Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch { /* ignored - the browser could not be started */ }
            e.Handled = true;
        };
        return link;
    }
}
