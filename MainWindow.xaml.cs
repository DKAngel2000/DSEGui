using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DseGui;

// ─── Models ───────────────────────────────────────────────────────────────────

public class AssetItem
{
    public string Name        { get; set; } = "";
    public string Status      { get; set; } = "Pending";
    public string StatusColor { get; set; } = "#444444";
    public string Icon        { get; set; } = "●";
    public string IconColor   { get; set; } = "#333333";
}

public class MenuEntry
{
    public string Icon   { get; set; } = "";
    public string Label  { get; set; } = "";
    public string Action { get; set; } = "";
}

// Persisted record of a folder we've set up (stored in dse_setups.json)
public class SetupEntry
{
    public string Path     { get; set; } = "";
    public string AddedUtc { get; set; } = "";
}

// UI row for the Previous Setups list
public class SetupRowItem
{
    public bool   IsSelected  { get; set; }
    public string Path        { get; set; } = "";
    public string Status      { get; set; } = "";
    public string StatusColor { get; set; } = "#555555";
    public string AddedText   { get; set; } = "";
    public bool   NeedsUpdate { get; set; }
    public bool   NeedsSetup  { get; set; }
    public bool   HasFiles    { get; set; }
}

// ─── Shared state ─────────────────────────────────────────────────────────────

internal class AppState
{
    public string DseDll     { get; set; } = "dse.dll";
    public string PdbSource  { get; set; } = "unavailable";
    public string WinBuild   { get; set; } = "";
    public string ScriptRoot { get; set; } = "";
}

// ─── Message types ────────────────────────────────────────────────────────────

internal record LogMessage(string Text, string Color);
internal record StatusMessage(string Key, string Value);
internal record EventMessage(string Name, object? Data);
internal record UiMessage(string Type, object Payload);

// ─── Main window ─────────────────────────────────────────────────────────────

public partial class MainWindow : Window
{
    // Constants
    private const string Exe1 = "hypervisor-launcher.exe";
    private const string Exe2 = "steamclient_loader_x64.exe";
    private const string FolderPrompt = "Type path, Browse, or drag and drop a folder...";

    // GitHub repository that publishes the DSE release assets.
    private const string DseRepo = "SouNobbao/dse";
    private const string DseLatestReleaseUrl = "https://api.github.com/repos/" + DseRepo + "/releases/latest";
    // Cache/tracking files live in their own "Dse" folder beside the exe.
    private static readonly string DseCacheDir =
        Path.Combine(AppContext.BaseDirectory.TrimEnd('\\', '/'), "Dse");

    // Files written directly beside the exe by older builds; moved once into DseCacheDir.
    private static readonly string[] LegacyCacheFiles =
    {
        "dse.dll", Exe1, Exe2, "pefile.exe",
        "sha256hashes.txt", "DSE_lasttag.txt", "DSE_lastdir.txt", "dse_setups.json"
    };

    private readonly string _cfgFile = Path.Combine(DseCacheDir, "DSE_lastdir.txt");
    private int _migratedCacheFiles;

    // State
    private readonly AppState _state = new();
    private string _currentGameDir = "";
    private string _iniEditGameDir = "";
    private FrameworkElement? _iniReturnView;
    private string _denuvChoiceDir = "";
    private int    _logLineNum     = 0;
    private static readonly HttpClient _http = new();

    // The latest-release JSON, fetched once per session and reused by every asset sync
    // (the unauthenticated GitHub API allows only 60 requests per hour).
    private JsonElement? _cachedRelease;

    // UI queue — background threads enqueue, dispatcher timer drains
    private readonly ConcurrentQueue<UiMessage> _uiQueue = new();
    private readonly DispatcherTimer _timer;

    // Asset list
    private readonly ObservableCollection<AssetItem> _assetItems = new();

    private static readonly string[] AssetNames =
        { "dse.dll", "hypervisor-launcher.exe", "pefile.exe", "steamclient_loader_x64.exe" };

    // Previous Setups tracking
    private readonly ObservableCollection<SetupRowItem> _setupRows = new();
    private static readonly string[] TrackedSetupAssets = { "dse.dll", Exe1, Exe2 };
    private string SetupsFile => Path.Combine(_state.ScriptRoot, "dse_setups.json");

    // Color map matching PS1 version
    private static readonly Dictionary<string, string> ColorMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Gray"]     = "#AAAAAA",
        ["DarkGray"] = "#555555",
        ["Cyan"]     = "#00BFFF",
        ["Green"]    = "#27AE60",
        ["Yellow"]   = "#E0C060",
        ["Red"]      = "#E74C3C",
        ["Magenta"]  = "#9B59B6",
        ["White"]    = "#FFFFFF",
    };

    public MainWindow()
    {
        InitializeComponent();

        _migratedCacheFiles = EnsureCacheFolder();
        _state.ScriptRoot  = DseCacheDir;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DseSyncScript/1.04");

        foreach (var name in AssetNames)
            _assetItems.Add(new AssetItem { Name = name });
        AssetStatusList.ItemsSource = _assetItems;
        SetupsList.ItemsSource       = _setupRows;

        FolderInput.Text       = FolderPrompt;
        FolderInput.Foreground = Brush("#555555");
        FolderInput.GotFocus  += FolderInput_GotFocus;
        FolderInput.LostFocus += FolderInput_LostFocus;
        FolderInput.KeyDown   += FolderInput_KeyDown;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    // ─── Startup ──────────────────────────────────────────────────────────────

    // Creates <exe dir>\Dse and moves any cache/tracking files an older build left
    // beside the exe into it. Returns how many files were moved.
    private static int EnsureCacheFolder()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        Directory.CreateDirectory(DseCacheDir);

        var moved = 0;
        foreach (var name in LegacyCacheFiles)
        {
            var source = Path.Combine(baseDir, name);
            var target = Path.Combine(DseCacheDir, name);

            if (!File.Exists(source) || File.Exists(target))
                continue;

            try
            {
                File.Move(source, target);
                moved++;
            }
            catch
            {
                // Locked or in use - leave it beside the exe; the sync re-downloads when needed.
            }
        }

        return moved;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AppendLog("DSE Setup Tool v1.1", "Magenta");
        AppendLog("DLL by SouNobbao  |  Batch by DNAngel", "DarkGray");
        AppendLog("", "Gray");
        if (_migratedCacheFiles > 0)
            AppendLog($"[*] Moved {_migratedCacheFiles} cached file(s) into the Dse folder.", "DarkGray");
#if !DEBUG
        // Check GitHub for a newer build before doing any work; a released
        // build updates itself (prompt first) and relaunches, so the startup
        // sync below would only be wasted effort.
        _ = RunUpdateGateAsync();
#else
        AppendLog("[*] Starting background tasks...", "Gray");
        Task.Run(RunStartupSequence);
#endif
    }

#if !DEBUG
    /// <summary>Prompt-first auto-update gate: check GitHub, swap in a newer
    /// build when approved, otherwise continue into the normal startup.</summary>
    private async Task RunUpdateGateAsync()
    {
        if (await Updater.CheckAndPromptAsync(this, EnqueueLog))
            return; // the new build was launched; this instance is exiting

        AppendLog("[*] Starting background tasks...", "Gray");
        _ = Task.Run(RunStartupSequence);
    }
#endif

    private void Window_Closed(object? sender, EventArgs e)
    {
        _timer.Stop();
        Environment.Exit(0);
    }

    // ─── UI pump ──────────────────────────────────────────────────────────────

    private void Timer_Tick(object? sender, EventArgs e)
    {
        while (_uiQueue.TryDequeue(out var msg))
        {
            switch (msg.Type)
            {
                case "LOG":    AppendLog(((LogMessage)msg.Payload).Text, ((LogMessage)msg.Payload).Color); break;
                case "STATUS": HandleStatus((StatusMessage)msg.Payload); break;
                case "EVENT":  HandleEvent((EventMessage)msg.Payload); break;
            }
        }
    }

    // ─── Enqueue helpers (called from background threads) ─────────────────────

    private void EnqueueLog(string text, string color = "Gray", string prefix = "[*]")
        => _uiQueue.Enqueue(new UiMessage("LOG", new LogMessage(prefix == "" ? text : $"{prefix} {text}", color)));

    private void EnqueueStatus(string key, string value)
        => _uiQueue.Enqueue(new UiMessage("STATUS", new StatusMessage(key, value)));

    private void EnqueueEvent(string name, object? data = null)
        => _uiQueue.Enqueue(new UiMessage("EVENT", new EventMessage(name, data)));

    // ─── Status handler ───────────────────────────────────────────────────────

    private void HandleStatus(StatusMessage st)
    {
        if (st.Key == "PDB")
        {
            (PdbStatusText.Text, PdbStatusText.Foreground, PdbSourceBadge.Text) = st.Value switch
            {
                "CACHED"  => ("Cached in local symbol store",   Brush("#27AE60"), "cached"),
                "SUCCESS" => ("Downloaded from Microsoft",       Brush("#27AE60"), "live"),
                _         => ("Failed - using dse.dll",         Brush("#E74C3C"), "unavailable"),
            };
        }
        else if (st.Key == "WINBUILD")
        {
            WinBuildText.Text         = st.Value;
            BuildInfoPanel.Visibility = Visibility.Visible;
        }
        else if (st.Key.StartsWith("ASSET:"))
        {
            UpdateAssetStatus(st.Key["ASSET:".Length..], st.Value);
        }
    }

    // ─── Event handler ────────────────────────────────────────────────────────

    private void HandleEvent(EventMessage ev)
    {
        switch (ev.Name)
        {
            case "STARTUP_DONE":
                DllSelectedText.Text      = _state.DseDll;
                BuildInfoPanel.Visibility = Visibility.Visible;
                ShowFolderView();
                break;

            case "STARTUP_FAIL":
                ShowDoneView(isError: true, message: ev.Data as string ?? "");
                break;

            case "SCAN_EMPTY":
                AppendLog("[!] No known files found in that directory.", "Red");
            AppendLog("    Expected: Artifact.json / illusive.toml / illusive.dll / DenuvOwO.ini / reflex.ini / veil.ini / load.txt / hypervisor-launcher.exe / steamclient_loader_x64.exe", "DarkGray");
                break;

            case "SCAN_SINGLE":
                _currentGameDir = (string)ev.Data!;
                ShowDirView(_currentGameDir);
                break;

            case "SCAN_MULTI":
                ShowMultiDirView((string[])ev.Data!);
                break;

            case "ADD_SETUP_SCAN_DONE":
                var foundDirs = (string[])ev.Data!;
                if (foundDirs.Length == 0)
                {
                    AppendLog("No existing DSE setups found under that folder.", "Yellow");
                }
                else
                {
                    int addedCount = 0;
                    foreach (var d in foundDirs)
                        if (TrackSetup(d)) addedCount++;

                    AppendLog($"Found {foundDirs.Length} folder(s) with DSE files - added {addedCount} new entr{(addedCount == 1 ? "y" : "ies")} to Previous Setups.", "Green");
                }
                ShowPreviousSetupsView();
                break;

            case "NEED_DENUV_CHOICE":
                _denuvChoiceDir = (string)ev.Data!;
                ShowView(ViewDenuvChoice);
                break;

            case "ACTION_DONE":
                var ad = (ActionDoneData)ev.Data!;
                if (ad.GoLaunch) ShowLaunchView(ad.GameDir);
                else { AppendLog("[*] Done. Returning to folder selection.", "Green"); ShowFolderView(); }
                break;

            case "ACTION_FAIL":
                ShowDoneView(isError: true, message: ev.Data as string ?? "");
                break;

            case "UNDO_DONE":
                _currentGameDir = "";
                ShowFolderView();
                break;

            case "SETUPS_UPDATED":
                ShowPreviousSetupsView();
                break;
        }
    }

    // ─── Log ──────────────────────────────────────────────────────────────────

    private void AppendLog(string text, string color)
    {
        var hexColor = ColorMap.TryGetValue(color, out var c) ? c : color;
        var para = new Paragraph { Margin = new Thickness(0, 0, 0, 1) };

        if (text != "")
            para.Inlines.Add(new Run(DateTime.Now.ToString("HH:mm:ss") + "  ") { Foreground = Brush("#444444") });

        para.Inlines.Add(new Run(text) { Foreground = Brush(hexColor) });
        LogBox.Document.Blocks.Add(para);
        LogLineCount.Text = $"{++_logLineNum} lines";
        LogScrollViewer.ScrollToEnd();
    }

    // ─── View switching ───────────────────────────────────────────────────────

    private void ShowView(FrameworkElement target)
    {
        foreach (var v in new FrameworkElement[] { ViewStartup, ViewFolder, ViewAlreadyDone, ViewMenu, ViewDenuvChoice, ViewMultiDir, ViewLaunch, ViewDone, ViewPreviousSetups, ViewEditIni, ViewUac, ViewReadme })
            v.Visibility = Visibility.Collapsed;
        target.Visibility = Visibility.Visible;
    }

    private void ShowFolderView()
    {
        if (File.Exists(_cfgFile))
        {
            var last = File.ReadAllText(_cfgFile).Trim();
            if (!string.IsNullOrEmpty(last))
            {
                LastDirText.Text        = last;
                LastDirPanel.Visibility = Visibility.Visible;
                BtnUseLast.Visibility   = Visibility.Visible;
            }
        }
        ShowView(ViewFolder);
    }

    private void ShowDirView(string dir)
    {
        var state = GetDirState(dir);

        if (state.AlreadyDone)
        {
            AlreadyDonePathText.Text = dir;
            ShowView(ViewAlreadyDone);
            return;
        }

        if (!state.HasConfig && !state.HasExe)
        {
            AppendLog("[!] No supported config files found in that folder.", "Red");
            ShowFolderView();
            return;
        }

        var menuItems = new List<MenuEntry>();
        if (state.ShowDll)                       menuItems.Add(new MenuEntry { Icon = "▶", Label = "Setup DSE DLL + Config Files",                                   Action = "DLL" });
        if (state.ShowCombined && state.HasExe1) menuItems.Add(new MenuEntry { Icon = "▶", Label = "Setup DLL + load.txt + hypervisor-launcher.exe",                 Action = "COMBINED1" });
        if (state.ShowCombined && state.HasExe2) menuItems.Add(new MenuEntry { Icon = "▶", Label = "Setup DLL + load.txt + steamclient_loader_x64.exe",              Action = "COMBINED2" });
        if (state.ShowExe && state.HasExe1)      menuItems.Add(new MenuEntry { Icon = "▶", Label = "Replace hypervisor-launcher.exe",                                Action = "EXE1" });
        if (state.ShowExe && state.HasExe2)      menuItems.Add(new MenuEntry { Icon = "▶", Label = "Replace steamclient_loader_x64.exe",                             Action = "EXE2" });
        menuItems.Add(new MenuEntry { Icon = "🚀", Label = "Launch Game", Action = "LAUNCH" });
        menuItems.Add(new MenuEntry { Icon = "✕",  Label = "Exit",        Action = "EXIT" });

        MenuPathText.Text          = dir;
        MenuButtonList.ItemsSource = menuItems;
        ShowView(ViewMenu);
    }

    private void ShowMultiDirView(string[] dirs)
    {
        MultiDirList.ItemsSource = dirs;
        ShowView(ViewMultiDir);
    }

    private void ShowLaunchView(string dir)
    {
        var exes = new List<string>();
        if (File.Exists(Path.Combine(dir, Exe1))) exes.Add(Exe1);
        if (File.Exists(Path.Combine(dir, Exe2))) exes.Add(Exe2);
        foreach (var f in Directory.GetFiles(dir, "*.exe"))
        {
            var n = Path.GetFileName(f);
            if (!exes.Contains(n, StringComparer.OrdinalIgnoreCase)) exes.Add(n);
        }
        LaunchPathText.Text = dir;
        ExeList.ItemsSource = exes;
        ShowView(ViewLaunch);
    }

    // ─── Edit dse.ini ───────────────────────────────────────────────────────

    private const string DefaultIniTemplate =
"[dse]\r\n" +
"; Automatically toggle DSE. If DSE was already disabled, dse will not run.\r\n" +
"toggleDse=true\r\n" +
"; Enable/disable Steam API hooks\r\n" +
"steamHooks=false\r\n" +
"; Enable coldloader support (for games that need it)\r\n" +
"coldloaderhooks=false\r\n" +
"steam_path=\r\n" +
"; Forward emulator steam_api64.dll exports to the real steam_api64_o.dll next to it\r\n" +
"steamApiForward=false\r\n" +
"; Skip warning prompts and proceed automatically\r\n" +
"ignoreWarnings=false\r\n" +
"; Enable/disable logging output\r\n" +
"logging=false\r\n" +
"\r\n" +
"; Problematic services to stop or delete (Format: ServiceName:ACTION, e.g. vgk:STOP, vgc:STOP, FACEIT:DELETE)\r\n" +
"problematic_services=\r\n" +
"; Problematic tasks to kill (comma separated executable names, e.g. vgtray.exe)\r\n" +
"problematic_tasks=\r\n";

    private Dictionary<string, string> ParseIniValues(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return dict;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#") || line.StartsWith("[")) continue;
            var idx = line.IndexOf('=');
            if (idx < 0) continue;
            var key = line.Substring(0, idx).Trim();
            var val = line.Substring(idx + 1).Trim();
            dict[key] = val;
        }
        return dict;
    }

    private static bool IniBool(Dictionary<string, string> vals, string key, bool def)
    {
        if (vals.TryGetValue(key, out var v) && bool.TryParse(v, out var b)) return b;
        return def;
    }

    private void OpenIniEditor(string gameDir, FrameworkElement returnView)
    {
        if (string.IsNullOrEmpty(gameDir)) return;

        _iniEditGameDir = gameDir;
        _iniReturnView  = returnView;

        var path = Path.Combine(gameDir, "dse.ini");
        var vals = ParseIniValues(path);

        IniPathText.Text            = path;
        ChkToggleDse.IsChecked      = IniBool(vals, "toggleDse", true);
        ChkSteamHooks.IsChecked     = IniBool(vals, "steamHooks", false);
        ChkColdloader.IsChecked     = IniBool(vals, "coldloaderhooks", false);
        ChkSteamApiForward.IsChecked = IniBool(vals, "steamApiForward", false);
        ChkIgnoreWarnings.IsChecked = IniBool(vals, "ignoreWarnings", false);
        ChkLogging.IsChecked        = IniBool(vals, "logging", false);
        TxtSteamPath.Text            = vals.GetValueOrDefault("steam_path", "");
        TxtProblematicServices.Text  = vals.GetValueOrDefault("problematic_services", "");
        TxtProblematicTasks.Text     = vals.GetValueOrDefault("problematic_tasks", "");

        ShowView(ViewEditIni);
    }

    private void SaveIniValues(string path, Dictionary<string, string> values)
    {
        List<string> lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : DefaultIniTemplate.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith(";") || trimmed.StartsWith("#") || trimmed.StartsWith("[")) continue;
            var idx = trimmed.IndexOf('=');
            if (idx <= 0) continue;
            var key = trimmed.Substring(0, idx).Trim();
            if (values.TryGetValue(key, out var newVal))
            {
                lines[i] = $"{key}={newVal}";
                written.Add(key);
            }
        }

        foreach (var kv in values)
            if (!written.Contains(kv.Key))
                lines.Add($"{kv.Key}={kv.Value}");

        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private void BtnEditIniLaunch_Click(object sender, RoutedEventArgs e)  => OpenIniEditor(_currentGameDir, ViewLaunch);
    private void BtnEditIniAlready_Click(object sender, RoutedEventArgs e) => OpenIniEditor(_currentGameDir, ViewAlreadyDone);
    private void BtnCancelIni_Click(object sender, RoutedEventArgs e)     => ShowView(_iniReturnView ?? ViewFolder);

    private void BtnSaveIni_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_iniEditGameDir);
            var path = Path.Combine(_iniEditGameDir, "dse.ini");
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["toggleDse"]            = (ChkToggleDse.IsChecked  == true) ? "true" : "false",
                ["steamHooks"]           = (ChkSteamHooks.IsChecked == true) ? "true" : "false",
                ["coldloaderhooks"]      = (ChkColdloader.IsChecked == true) ? "true" : "false",
                ["steamApiForward"]      = (ChkSteamApiForward.IsChecked == true) ? "true" : "false",
                ["ignoreWarnings"]       = (ChkIgnoreWarnings.IsChecked == true) ? "true" : "false",
                ["logging"]              = (ChkLogging.IsChecked    == true) ? "true" : "false",
                ["steam_path"]           = TxtSteamPath.Text.Trim(),
                ["problematic_services"] = TxtProblematicServices.Text.Trim(),
                ["problematic_tasks"]    = TxtProblematicTasks.Text.Trim(),
            };
            SaveIniValues(path, values);
            AppendLog($"Saved dse.ini: {path}", "Green");
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to save dse.ini: {ex.Message}", "Red");
        }

        ShowView(_iniReturnView ?? ViewFolder);
    }

    private void ShowDoneView(bool isError, string message)
    {
        DoneTitle.Text       = isError ? "Error" : "Done";
        DoneTitle.Foreground = isError ? Brush("#E74C3C") : Brush("#27AE60");
        DoneMessage.Text     = message;
        ShowView(ViewDone);
    }

    // ─── Skip UAC prompt (scheduled task) ─────────────────────────────────────

    // Written to %TEMP%\Vector\DseGui\uac-task.ps1 on demand. Kept as a string so the
    // single-file publish stays self-contained (nothing extra shipped or extracted).
    private const string UacTaskScript = """
param(
    [Parameter(Mandatory=$true)][ValidateSet('create','remove','check')][string]$Mode,
    [string]$Exe = '',
    [string]$User = '',
    [Parameter(Mandatory=$true)][string]$ResultPath
)

$ErrorActionPreference = 'Stop'
$result = 'ERR|Unknown failure.'

try {
    if (-not $Exe) { throw 'No executable was supplied.' }
    $exe  = [IO.Path]::GetFullPath($Exe)
    $name = 'dse_' + [IO.Path]::GetFileNameWithoutExtension($exe)

    switch ($Mode) {
        'check' {
            $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
            if (-not $task) { $result = 'NONE'; break }
            $action  = @($task.Actions)[0]
            $level   = [string]$task.Principal.RunLevel
            $command = ([string]$action.Execute).Trim('"')
            $workdir = ([string]$action.WorkingDirectory).TrimEnd('\')
            $exeOk = $command -and ([IO.Path]::GetFullPath($command) -ieq $exe)
            $dirOk = $workdir -and ($workdir -ieq ([IO.Path]::GetDirectoryName($exe)).TrimEnd('\'))
            $lvlOk = $level -like '*Highest*'
            if ($exeOk -and $dirOk -and $lvlOk) { $result = 'MATCH' }
            else { $result = "MISMATCH|$level|$command|$workdir" }
        }
        'create' {
            $action    = New-ScheduledTaskAction -Execute $exe -WorkingDirectory ([IO.Path]::GetDirectoryName($exe))
            $principal = New-ScheduledTaskPrincipal -UserId $User -RunLevel Highest -LogonType Interactive
            $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
            Register-ScheduledTask -TaskName $name -Force -Action $action -Principal $principal -Settings $settings | Out-Null
            $result = "OK|$name"
        }
        'remove' {
            $task = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
            if (-not $task) { $result = "GONE|$name"; break }
            Unregister-ScheduledTask -TaskName $name -Confirm:$false
            $result = "OK|$name"
        }
    }
}
catch { $result = "ERR|$($_.Exception.Message)" }

Set-Content -Path $ResultPath -Value $result -Encoding ASCII
""";

    private string _uacGameDir = "";
    private string _uacExe = "";
    private FrameworkElement? _uacReturnView;

    private static string UacTaskName(string exePath) =>
        "dse_" + Path.GetFileNameWithoutExtension(exePath);

    private static bool IsRunningElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string UacScriptFolder => Path.Combine(Path.GetTempPath(), "Vector", "DseGui");

    private static string QuoteArg(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// Runs the helper script. <c>create</c>/<c>remove</c> elevate through UAC when the app
    /// itself is not elevated; <c>check</c> never elevates. The script writes a one-line
    /// result file so the outcome survives the elevation boundary.
    /// </summary>
    private static (bool Ok, string Message) RunUacScript(string mode, string exePath, string user)
    {
        var elevate = mode != "check" && !IsRunningElevated();

        try
        {
            Directory.CreateDirectory(UacScriptFolder);

            var script     = Path.Combine(UacScriptFolder, "uac-task.ps1");
            var resultFile = Path.Combine(UacScriptFolder, "uac-task.result");

            File.WriteAllText(script, UacTaskScript, new UTF8Encoding(false));
            try { File.Delete(resultFile); } catch { }

            var args = new List<string>
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden",
                "-File", QuoteArg(script),
                "-Mode", mode,
                "-Exe", QuoteArg(exePath),
                "-ResultPath", QuoteArg(resultFile)
            };
            if (mode != "check")
            {
                args.Add("-User");
                args.Add(QuoteArg(user));
            }

            var psi = new ProcessStartInfo("powershell.exe")
            {
                Arguments       = string.Join(" ", args),
                UseShellExecute = elevate,
                CreateNoWindow  = true,
                WindowStyle     = ProcessWindowStyle.Hidden
            };
            if (elevate) psi.Verb = "runas";

            using var proc = Process.Start(psi);
            if (proc is null) return (false, "PowerShell could not be started.");
            proc.WaitForExit();

            if (!File.Exists(resultFile))
                return (false, "The scheduled task tool returned no result.");

            var text = File.ReadAllText(resultFile).Trim();
            if (text.StartsWith("OK|", StringComparison.Ordinal))
                return (true, text[3..]);
            if (text.StartsWith("GONE|", StringComparison.Ordinal))
                return (true, text);
            if (text.StartsWith("ERR|", StringComparison.Ordinal))
                return (false, text[4..]);

            return (true, text);   // NONE / MATCH / MISMATCH|…
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "cancelled");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static (bool Exists, bool Matches, string Detail) InspectUacTask(string exePath)
    {
        var (ok, message) = RunUacScript("check", exePath, "");
        if (!ok) return (false, false, message);

        if (message.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return (false, false, "not created");

        if (message.Equals("MATCH", StringComparison.OrdinalIgnoreCase))
            return (true, true, "");

        if (message.StartsWith("MISMATCH|", StringComparison.OrdinalIgnoreCase))
        {
            var parts = message.Split('|');
            var level = parts.Length > 1 ? parts[1] : "?";
            var exe   = parts.Length > 2 ? parts[2] : "?";
            var dir   = parts.Length > 3 ? parts[3] : "?";
            return (true, false, $"run level {level}, runs {exe} in {dir}");
        }

        return (false, false, message);
    }

    private void ShowUacView(string dir, FrameworkElement returnView)
    {
        _uacGameDir    = dir;
        _uacReturnView = returnView;

        var exes = new List<string>();
        if (File.Exists(Path.Combine(dir, Exe1))) exes.Add(Exe1);
        if (File.Exists(Path.Combine(dir, Exe2))) exes.Add(Exe2);
        foreach (var f in Directory.GetFiles(dir, "*.exe").Select(Path.GetFileName).OfType<string>()
                     .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            if (!exes.Contains(f, StringComparer.OrdinalIgnoreCase)) exes.Add(f);

        UacExeList.ItemsSource = exes;
        _uacExe = exes.Count > 0 ? Path.Combine(dir, exes[0]) : "";

        ShowView(ViewUac);
        RefreshUacView();
    }

    private void UacExe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string name && name.Length > 0)
        {
            _uacExe = Path.Combine(_uacGameDir, name);
            RefreshUacView();
        }
    }

    private void BtnUacBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title            = "Select the game executable",
            Filter           = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists  = true,
            InitialDirectory = Directory.Exists(_uacGameDir) ? _uacGameDir : ""
        };

        if (dialog.ShowDialog() != true) return;

        _uacExe = dialog.FileName;
        RefreshUacView();
    }

    private async void RefreshUacView()
    {
        UacExeBox.Text = _uacExe;

        if (string.IsNullOrEmpty(_uacExe))
        {
            UacTaskText.Text       = "";
            UacStatusText.Text     = "No executable found in this folder - use Browse.";
            BtnUacCreate.IsEnabled = false;
            BtnUacRemove.IsEnabled = false;
            return;
        }

        var exe = _uacExe;
        UacTaskText.Text       = $"Task name: {UacTaskName(exe)}";
        UacStatusText.Text     = "Checking the scheduled task...";
        UacStatusText.Foreground = Brush("#CCCCCC");
        BtnUacCreate.IsEnabled = false;
        BtnUacRemove.IsEnabled = false;

        var (exists, matches, detail) = await Task.Run(() => InspectUacTask(exe));

        if (!string.Equals(exe, _uacExe, StringComparison.OrdinalIgnoreCase)) return;  // selection changed

        // The primary button is a no-op once the task already matches this exe, so it is
        // disabled - a lit Create meant "is it set up?" was only answerable from the log.
        BtnUacCreate.IsEnabled = !(exists && matches);
        BtnUacRemove.IsEnabled = exists;

        UacStatusText.Text = !exists
            ? detail == "not created"
                ? "○ No scheduled task yet - DSE falls back to dse_elevator.exe and Windows asks for permission at launch."
                : $"○ Could not read the scheduled task ({detail}). Creating it is still available."
            : matches
                ? "✓ Scheduled task is set up - DSE can start this exe without a UAC prompt."
                : $"✗ Scheduled task exists but does not match ({detail}). Remove it and create it again.";

        UacStatusText.Foreground = !exists
            ? (detail == "not created" ? Brush("#CCCCCC") : Brush("#E0C060"))
            : matches
                ? Brush("#27AE60")
                : Brush("#E0C060");
    }

    private async void BtnUacCreate_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_uacExe)) return;

        var exe  = _uacExe;
        var name = UacTaskName(exe);
        var user = WindowsIdentity.GetCurrent().Name;

        BtnUacCreate.IsEnabled = false;
        UacStatusText.Text     = IsRunningElevated()
            ? "Creating the scheduled task..."
            : "Requesting administrator rights - Windows may ask for permission.";

        EnqueueLog($"Creating scheduled task {name}...", "Cyan", "");

        var (ok, message) = await Task.Run(() => RunUacScript("create", exe, user));

        if (ok)
        {
            EnqueueLog($"Created scheduled task {name}. DSE can skip the UAC prompt for {Path.GetFileName(exe)}.", "Green", "");
            EnqueueLog("Remove the task from this screen if the game is moved or renamed.", "DarkGray", "");
        }
        else if (message == "cancelled")
        {
            EnqueueLog("Task registration cancelled - nothing was changed.", "Yellow", "[!]");
        }
        else
        {
            EnqueueLog($"Task registration failed: {message}", "Red", "[!]");
        }

        RefreshUacView();
    }

    private async void BtnUacRemove_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_uacExe)) return;

        var exe  = _uacExe;
        var name = UacTaskName(exe);

        var confirm = System.Windows.MessageBox.Show(
            $"Remove scheduled task {name}?\r\n\r\nDSE will fall back to dse_elevator.exe and Windows will ask for permission at launch.",
            "Remove scheduled task",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);

        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        BtnUacRemove.IsEnabled = false;
        UacStatusText.Text     = IsRunningElevated()
            ? "Removing the scheduled task..."
            : "Requesting administrator rights - Windows may ask for permission.";

        EnqueueLog($"Removing scheduled task {name}...", "Cyan", "");

        var (ok, message) = await Task.Run(() => RunUacScript("remove", exe, ""));

        if (ok && message.StartsWith("GONE|", StringComparison.Ordinal))
            EnqueueLog($"Scheduled task {name} was not present - nothing to remove.", "Yellow", "");
        else if (ok)
            EnqueueLog($"Removed scheduled task {name}.", "Green", "");
        else if (message == "cancelled")
            EnqueueLog("Task removal cancelled - nothing was changed.", "Yellow", "[!]");
        else
            EnqueueLog($"Task removal failed: {message}", "Red", "[!]");

        RefreshUacView();
    }

    private async void BtnUacCheck_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_uacExe)) return;

        var exe = _uacExe;
        var (exists, matches, detail) = await Task.Run(() => InspectUacTask(exe));

        if (!exists)
            EnqueueLog($"No scheduled task {UacTaskName(exe)} found - DSE uses dse_elevator.exe.", "Yellow", "");
        else if (matches)
            EnqueueLog($"Scheduled task {UacTaskName(exe)} matches this exe (Highest, working folder OK).", "Green", "");
        else
            EnqueueLog($"Scheduled task {UacTaskName(exe)} exists but does not match: {detail}.", "Yellow", "[!]");

        RefreshUacView();
    }

    private void BtnUacFromAlready_Click(object sender, RoutedEventArgs e) => ShowUacView(_currentGameDir, ViewAlreadyDone);
    private void BtnUacFromLaunch_Click(object sender, RoutedEventArgs e)  => ShowUacView(LaunchPathText.Text, ViewLaunch);

    private void BtnUacBack_Click(object sender, RoutedEventArgs e) => ShowView(_uacReturnView ?? ViewMenu);

    // ─── Previous Setups ──────────────────────────────────────────────────────

    private void ShowPreviousSetupsView()
    {
        var entries = LoadSetups();
        var missing = entries.Where(e => !Directory.Exists(e.Path)).ToList();

        if (missing.Count > 0)
        {
            var msg = "These tracked folders can no longer be found (likely uninstalled or moved):\n\n"
                    + string.Join("\n", missing.Select(m => m.Path))
                    + "\n\nRemove them from Previous Setups? This only removes the tracked entry - nothing on disk is touched.";
            var result = System.Windows.MessageBox.Show(msg, "Missing Setups Found",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (result == System.Windows.MessageBoxResult.Yes)
            {
                entries = entries.Except(missing).ToList();
                SaveSetups(entries);
                AppendLog($"Removed {missing.Count} missing setup entr{(missing.Count == 1 ? "y" : "ies")} (tracking only - no files touched).", "Yellow");
            }
        }

        var visible = entries.Where(e => Directory.Exists(e.Path)).ToList();

        // Rows appear immediately with a placeholder; the SHA256 checks run on a background
        // thread because hashing the tracked DLLs on the UI thread froze the window.
        _setupRows.Clear();
        foreach (var e in visible)
        {
            _setupRows.Add(new SetupRowItem
            {
                Path        = e.Path,
                Status      = "Checking...",
                StatusColor = "#555555",
                AddedText   = FormatAdded(e.AddedUtc),
                NeedsUpdate = false,
                NeedsSetup  = false,
                HasFiles    = true
            });
        }

        NoSetupsText.Visibility = _setupRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowView(ViewPreviousSetups);

        if (visible.Count == 0)
            return;

        var paths = visible.Select(e => e.Path).ToList();
        Task.Run(() =>
        {
            // The hash file is read once for the whole pass, not once per row.
            var expected = LoadExpectedHashes();
            var results = new List<SetupRowItem>();
            foreach (var path in paths)
            {
                var (status, color, needsUpdate) = CheckSetupStatus(path, expected);
                bool needsSetup = status == "No DSE files found";
                results.Add(new SetupRowItem
                {
                    Path        = path,
                    Status      = status,
                    StatusColor = color,
                    AddedText   = FormatAdded(visible.First(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)).AddedUtc),
                    NeedsUpdate = needsUpdate,
                    NeedsSetup  = needsSetup,
                    HasFiles    = !needsSetup
                });
            }

            Dispatcher.BeginInvoke(() => ApplySetupStatuses(results));
        });
    }

    // Swaps the placeholder rows for the checked ones, in order. A refresh while the checks
    // were running replaces the rows wholesale, so a mismatch simply discards the stale pass.
    private void ApplySetupStatuses(List<SetupRowItem> results)
    {
        for (var i = 0; i < results.Count && i < _setupRows.Count; i++)
        {
            if (!string.Equals(_setupRows[i].Path, results[i].Path, StringComparison.OrdinalIgnoreCase))
                return;

            results[i].IsSelected = _setupRows[i].IsSelected;
            _setupRows[i] = results[i];
        }

        NoSetupsText.Visibility = _setupRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Parses sha256hashes.txt (format: "<hash> <filename>" per line) into a name -> hash lookup.
    private Dictionary<string, string> LoadExpectedHashes()
    {
        var hashFile = Path.Combine(_state.ScriptRoot, "sha256hashes.txt");
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(hashFile))
        {
            foreach (var line in File.ReadAllLines(hashFile))
            {
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2) expected[parts[1].Trim()] = parts[0].Trim().ToUpper();
            }
        }
        return expected;
    }

    private (string Status, string Color, bool NeedsUpdate) CheckSetupStatus(string dir, Dictionary<string, string> expected)
    {
        bool anyFound = false, anyMismatch = false;
        foreach (var asset in TrackedSetupAssets)
        {
            var path = Path.Combine(dir, asset);
            if (!File.Exists(path)) continue;
            anyFound = true;
            if (expected.TryGetValue(asset, out var exp) && ComputeSha256(path) != exp)
                anyMismatch = true;
        }

        if (!anyFound)   return ("No DSE files found", "#E0C060", false);
        if (anyMismatch) return ("Needs update",        "#E0C060", true);
        return ("Up to date", "#27AE60", false);
    }

    private static string FormatAdded(string utc)
    {
        if (DateTime.TryParse(utc, out var dt))
            return "Added " + dt.ToLocalTime().ToString("MMM d, yyyy");
        return "";
    }

    private List<SetupEntry> LoadSetups()
    {
        try
        {
            if (!File.Exists(SetupsFile)) return new List<SetupEntry>();
            return JsonSerializer.Deserialize<List<SetupEntry>>(File.ReadAllText(SetupsFile)) ?? new List<SetupEntry>();
        }
        catch { return new List<SetupEntry>(); }
    }

    private void SaveSetups(List<SetupEntry> list)
    {
        try { File.WriteAllText(SetupsFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true })); }
        catch { /* non-fatal - tracking is best-effort */ }
    }

    private bool TrackSetup(string gameDir)
    {
        if (string.IsNullOrEmpty(gameDir)) return false;
        try
        {
            var list = LoadSetups();
            if (list.Any(s => string.Equals(s.Path, gameDir, StringComparison.OrdinalIgnoreCase)))
                return false;

            list.Add(new SetupEntry { Path = gameDir, AddedUtc = DateTime.UtcNow.ToString("u") });
            SaveSetups(list);
            return true;
        }
        catch { return false; /* non-fatal */ }
    }

    private async Task RunUpdateSetups(List<string> dirs)
    {
        EnqueueLog("", "Gray", "");
        EnqueueLog($"Updating {dirs.Count} folder(s)...", "Cyan");
        try
        {
            foreach (var dir in dirs)
            {
                EnqueueLog($"Checking: {dir}", "Cyan");
                foreach (var asset in TrackedSetupAssets)
                {
                    var existing = Path.Combine(dir, asset);
                    if (!File.Exists(existing)) continue;
                    if (!await RunSyncAsset(asset, copyToGameDir: true, gameDir: dir))
                        EnqueueLog($"Could not sync {asset} into {dir}.", "Red", "[!]");
                }
            }
            EnqueueLog("", "Gray", "");
            EnqueueLog("Update pass complete.", "Green");
        }
        catch (Exception ex)
        {
            // A locked file or a dead network must not kill the pass silently.
            EnqueueLog($"Update pass failed: {ex.Message}", "Red", "[!]");
        }

        EnqueueEvent("SETUPS_UPDATED");
    }

    // ─── Asset status ─────────────────────────────────────────────────────────

    private void UpdateAssetStatus(string name, string status)
    {
        for (int i = 0; i < _assetItems.Count; i++)
        {
            if (!string.Equals(_assetItems[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            var item = _assetItems[i];
            (item.Status, item.StatusColor, item.Icon, item.IconColor) = status switch
            {
                "OK"          => ("OK",          "#27AE60", "✓", "#27AE60"),
                "FAIL"        => ("FAIL",         "#E74C3C", "✗", "#E74C3C"),
                "DOWNLOADING" => ("Downloading",  "#00BFFF", "↓", "#00BFFF"),
                "UPDATING"    => ("Updating",     "#E0C060", "↻", "#E0C060"),
                _             => (item.Status, item.StatusColor, item.Icon, item.IconColor),
            };
            _assetItems[i] = item;
            break;
        }
    }

    // ─── DirState ─────────────────────────────────────────────────────────────

    private record DirState(bool HasConfig, bool HasExe, bool AlreadyDone,
        bool ShowDll, bool ShowCombined, bool ShowExe, bool HasExe1, bool HasExe2);

    private DirState GetDirState(string dir)
    {
        bool Exists(string f) => File.Exists(Path.Combine(dir, f));

        bool hasConfig    = GameConfigFiles.Any(Exists);
        bool hasExe       = Exists(Exe1) || Exists(Exe2);
        bool alreadyDone  = GameConfigBakFiles.Any(Exists) || Exists(Exe1 + ".bak") || Exists(Exe2 + ".bak")
                          || LoaderBakFiles.Any(Exists);
        if (!alreadyDone && Exists("illusive.load_dlls.txt"))
            alreadyDone = IsIllusiveLoadDllFirst(Path.Combine(dir, "illusive.load_dlls.txt"), Path.GetFileName(_state.DseDll));

        bool hasIniLoadDlls = new[] { "reflex.ini", "veil.ini" }.Any(name =>
        {
            var path = Path.Combine(dir, name);
            return File.Exists(path) && File.ReadAllText(path).Contains("[load_dlls]", StringComparison.OrdinalIgnoreCase);
        });

        bool showCombined = hasExe && !hasIniLoadDlls && Exists("load.txt");
        bool showExe      = hasExe && !hasIniLoadDlls && !showCombined;
        bool hasStructuredConfig = Exists("Artifact.json") || Exists("illusive.toml");
        if (hasStructuredConfig)
        {
            showCombined = false;
            showExe = false;
        }
        bool showDll      = hasConfig && !showCombined;

        return new DirState(hasConfig, hasExe, alreadyDone, showDll, showCombined, showExe, Exists(Exe1), Exists(Exe2));
    }

    // ─── Button handlers ──────────────────────────────────────────────────────

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Document.Blocks.Clear();
        _logLineNum = 0;
        LogLineCount.Text = "0 lines";
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var current = FolderInput.Text.Trim();
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select game root folder",
            InitialDirectory = Directory.Exists(current) ? current : "shell:MyComputerFolder"
        };
        if (dlg.ShowDialog(this) == true)
        {
            FolderInput.Text       = dlg.FolderName;
            FolderInput.Foreground = Brush("#E0E0E0");
        }
    }

    private void BtnUseLast_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_cfgFile)) return;
        var last = File.ReadAllText(_cfgFile).Trim();
        if (!string.IsNullOrEmpty(last))
        {
            FolderInput.Text       = last;
            FolderInput.Foreground = Brush("#E0E0E0");
        }
    }

    private void BtnScanFolder_Click(object sender, RoutedEventArgs e) => InvokeScanUI();

    private void FolderInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Return) InvokeScanUI();

        if (e.Key == Key.V &&
            (System.Windows.Input.Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            try
            {
                var droppedPaths = System.Windows.Clipboard.GetFileDropList();
                if (droppedPaths.Count > 0)
                {
                    var folder = droppedPaths[0];
                    if (Directory.Exists(folder))
                    {
                        FolderInput.Text = folder;
                        FolderInput.Foreground = Brush("#E0E0E0");
                        AppendLog($"Folder selected from clipboard: {folder}", "Green");
                        e.Handled = true;
                    }
                    else
                    {
                        AppendLog("The copied item is not a folder.", "Yellow");
                        e.Handled = true;
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
            {
                // No file list on the clipboard; allow normal text paste.
            }
        }
    }

    private void FolderInput_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop, true)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void FolderInput_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop, true)) return;

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop, true) is string[] paths && paths.Length > 0)
        {
            var droppedPath = paths[0].Trim().Trim('"');
            if (Directory.Exists(droppedPath))
            {
                FolderInput.Text = droppedPath;
                FolderInput.Foreground = Brush("#E0E0E0");
                AppendLog($"Folder selected by drag and drop: {droppedPath}", "Green");
            }
            else
            {
                AppendLog("Please drop a folder, not a file.", "Yellow");
            }
        }

        e.Handled = true;
    }

    private void InvokeScanUI()
    {
        var path = FolderInput.Text.Trim().Trim('"').TrimEnd('\\');
        if (path == FolderPrompt || string.IsNullOrEmpty(path))
        {
            AppendLog("Please enter a folder path or press Browse.", "Yellow");
            return;
        }
        if (!Directory.Exists(path))
        {
            AppendLog($"[!] Directory not found: {path}", "Red");
            return;
        }
        File.WriteAllText(_cfgFile, path);
        _currentGameDir = path;
        AppendLog($"[*] Scanning: {path}", "Cyan");
        Task.Run(() => RunScanFolder(path));
    }

    private void FolderInput_GotFocus(object sender, RoutedEventArgs e)
    {
        if (FolderInput.Text == FolderPrompt)
        {
            FolderInput.Text       = "";
            FolderInput.Foreground = Brush("#E0E0E0");
        }
    }

    private void FolderInput_LostFocus(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FolderInput.Text))
        {
            FolderInput.Text       = FolderPrompt;
            FolderInput.Foreground = Brush("#555555");
        }
    }

    private void BtnPreviousSetups_Click(object sender, RoutedEventArgs e)   => ShowPreviousSetupsView();
    private void BtnRefreshSetups_Click(object sender, RoutedEventArgs e)    => ShowPreviousSetupsView();
    private void BtnBackFromSetups_Click(object sender, RoutedEventArgs e)   => ShowFolderView();

    // ─── dse readme ───────────────────────────────────────────────────────────

    private void BtnReadme_Click(object sender, RoutedEventArgs e)
    {
        if (ReadmeViewer.Document is null || ReadmeViewer.Document.Blocks.Count == 0)
        {
            ReadmeViewer.Document = ReadmeRenderer.Build(DseReadme.Markdown);
            ReadmeSubtitle.Text =
                $"Snapshot of the upstream readme ({DseReadme.SnapshotLabel}). Use Open on GitHub for the current version.";
        }

        ShowView(ViewReadme);
    }

    private void BtnReadmeGithub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(DseReadme.SourceUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            EnqueueLog($"Could not open the readme link: {ex.Message}", "Red", "[!]");
        }
    }

    private void BtnReadmeBack_Click(object sender, RoutedEventArgs e) => ShowFolderView();

    // FlowDocumentScrollViewer swallows wheel input even when it cannot scroll, so wheel events
    // over the readme (text, code blocks, links) are forwarded to our outer scroller.
    private void Readme_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || ReadmeScroll.ScrollableHeight <= 0)
            return;

        ReadmeScroll.ScrollToVerticalOffset(ReadmeScroll.VerticalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private void BtnAddSetup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select a folder where DSE was already set up (subfolders are searched too)",
            InitialDirectory = "shell:MyComputerFolder"
        };
        if (dlg.ShowDialog(this) != true) return;

        var root = dlg.FolderName;
        if (!Directory.Exists(root))
        {
            AppendLog("Selected folder does not exist.", "Red");
            return;
        }

        Task.Run(() => RunAddSetupScan(root));
    }

    private void BtnRemoveSetup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string path)
        {
            var list = LoadSetups();
            list.RemoveAll(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
            SaveSetups(list);
            AppendLog($"Removed setup entry (tracking only): {path}", "Yellow");
            ShowPreviousSetupsView();
        }
    }

    private void BtnUpdateSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _setupRows.Where(r => r.IsSelected).Select(r => r.Path).ToList();
        if (selected.Count == 0)
        {
            AppendLog("No folders selected to update.", "Yellow");
            return;
        }
        Task.Run(() => RunUpdateSetups(selected));
    }

    private void BtnUpdateAll_Click(object sender, RoutedEventArgs e)
    {
        var outdated = _setupRows.Where(r => r.NeedsUpdate).Select(r => r.Path).ToList();
        if (outdated.Count == 0)
        {
            AppendLog("Everything is already up to date.", "Green");
            return;
        }
        Task.Run(() => RunUpdateSetups(outdated));
    }

    private void BtnSetupRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string path)
        {
            _currentGameDir = path;
            File.WriteAllText(_cfgFile, path);
            ShowDirView(path);
        }
    }

    private void BtnEditIniSetupRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string path)
            OpenIniEditor(path, ViewPreviousSetups);
    }

    private void BtnUndoSetupRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string path)
            Task.Run(() => RunUndoSetups(new List<string> { path }));
    }

    private void BtnUndoSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _setupRows.Where(r => r.IsSelected).Select(r => r.Path).ToList();
        if (selected.Count == 0)
        {
            AppendLog("No folders selected to undo.", "Yellow");
            return;
        }

        var msg = $"Undo DSE changes in {selected.Count} folder(s)? This restores original files and removes the DSE files placed there:\n\n"
                + string.Join("\n", selected);
        var result = System.Windows.MessageBox.Show(msg, "Confirm Bulk Undo",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        Task.Run(() => RunUndoSetups(selected));
    }

    private void BtnUndo_Click(object sender, RoutedEventArgs e)           => Task.Run(() => RunUndo(_currentGameDir));
    private void BtnContinueLaunch_Click(object sender, RoutedEventArgs e) => ShowLaunchView(_currentGameDir);
    private void BtnExit_Click(object sender, RoutedEventArgs e)           => Close();

    private void BtnNextFolder_Click(object sender, RoutedEventArgs e)    => StartOver();
    private void BtnDoneStartOver_Click(object sender, RoutedEventArgs e) => StartOver();

    private void StartOver()
    {
        ExeList.ItemsSource = null;
        _currentGameDir = "";
        ShowFolderView();
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string action)
            HandleMenuAction(action, _currentGameDir);
    }

    private void HandleMenuAction(string action, string gameDir)
    {
        switch (action)
        {
            case "DLL":       Task.Run(() => RunDoDll(gameDir));              break;
            case "COMBINED1": Task.Run(() => RunDoCombined(gameDir, Exe1));   break;
            case "COMBINED2": Task.Run(() => RunDoCombined(gameDir, Exe2));   break;
            case "EXE1":      Task.Run(() => RunDoExe(gameDir, Exe1));        break;
            case "EXE2":      Task.Run(() => RunDoExe(gameDir, Exe2));        break;
            case "LAUNCH":    ShowLaunchView(gameDir);                        break;
            case "EXIT":      Close();                                        break;
        }
    }

    private void MultiDirButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string dir)
        {
            _currentGameDir = dir;
            File.WriteAllText(_cfgFile, dir);
            AppendLog($"[*] Using: {dir}", "Green");
            ShowDirView(dir);
        }
    }

    private void ExeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string exeName)
        {
            RunLaunchExe(_currentGameDir, exeName);
            Close();
        }
    }

    private void BtnDenuvAppend_Click(object sender, RoutedEventArgs e) => Task.Run(() => RunDenuvAppend(_denuvChoiceDir));
    private void BtnDenuvPefile_Click(object sender, RoutedEventArgs e) => Task.Run(() => RunDenuvPefile(_denuvChoiceDir));

    // ─── Background tasks ─────────────────────────────────────────────────────

    private async Task RunStartupSequence()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            int    major    = (int)(key?.GetValue("CurrentMajorVersionNumber") ?? 0);
            int    build    = int.Parse(key?.GetValue("CurrentBuildNumber")?.ToString() ?? "0");
            var    ubrObj   = key?.GetValue("UBR");
            string buildRev = ubrObj != null ? $"{build}.{ubrObj}" : $"{build}";

            if (major != 10)
            {
                EnqueueLog($"Unsupported Windows version (major: {major}).", "Red", "[!]");
                EnqueueEvent("STARTUP_FAIL", "Unsupported Windows version. Requires Windows 10 build 1909 or later, or Windows 11.");
                return;
            }
            if (build < 18363)
            {
                EnqueueLog($"Unsupported build {build}. Requires 18363+.", "Red", "[!]");
                EnqueueEvent("STARTUP_FAIL", $"Unsupported Windows build ({build}). Requires Windows 10 1909 (18363) or later.");
                return;
            }

            _state.WinBuild = buildRev;
            EnqueueStatus("WINBUILD", buildRev);

            await RunCheckPdb();

            EnqueueLog("", "Gray", "");
            EnqueueLog("Initializing asset verification...", "Cyan");
            EnqueueLog("Syncing all required files into local cache.", "DarkGray");
            EnqueueLog("", "Gray", "");

            var sw = Stopwatch.StartNew();
            await RunSyncHashFile();

            bool failed = false;
            foreach (var a in AssetNames)
                if (!await RunSyncAsset(a, copyToGameDir: false)) failed = true;
            sw.Stop();

            if (failed)
            {
                EnqueueLog("", "Gray", "");
                EnqueueLog("Pre-download failed for one or more files.", "Red", "[!]");
                EnqueueLog("Check your internet connection and try again.", "DarkGray");
                EnqueueEvent("STARTUP_FAIL", "Asset sync failed. Check your internet connection and try again.");
                return;
            }

            EnqueueLog("", "Gray", "");
            EnqueueLog($"All core assets downloaded/verified. ({sw.Elapsed.TotalSeconds:F0}s)", "Green");
            EnqueueLog("", "Gray", "");
            EnqueueLog($"DLL selected by PDB: {_state.DseDll}", "Magenta");
            EnqueueLog($"Windows Build: {_state.WinBuild}", "DarkGray");
            EnqueueLog($"PDB Source: {_state.PdbSource}", "DarkGray");
            EnqueueLog("", "Gray", "");
            EnqueueLog("Standard methods will be used for this system if the PDB check fails.", "Yellow", "[!]");
            EnqueueLog("", "Gray", "");
            EnqueueEvent("STARTUP_DONE");
        }
        catch (Exception ex)
        {
            EnqueueEvent("STARTUP_FAIL", $"Startup error: {ex.Message}");
        }
    }

    private async Task RunCheckPdb()
    {
        EnqueueLog("Checking PDB symbols for ntoskrnl.exe...", "Cyan");
        EnqueueLog("Checks local symbol store first - only queries Microsoft if not found.", "DarkGray");

        const string pe = @"C:\Windows\System32\ntoskrnl.exe";
        if (!File.Exists(pe))
        {
            EnqueueLog("ntoskrnl.exe not found. Using dse.dll.", "Yellow", "[!]");
            _state.DseDll = "dse.dll"; _state.PdbSource = "unavailable";
            EnqueueStatus("PDB", "FAIL"); return;
        }

        try
        {
            var bytes  = File.ReadAllBytes(pe);
            var rsds   = "RSDS"u8.ToArray();
            int offset = -1;
            for (int i = 0; i < bytes.Length - 4; i++)
                if (bytes[i] == rsds[0] && bytes[i+1] == rsds[1] && bytes[i+2] == rsds[2] && bytes[i+3] == rsds[3])
                    { offset = i; break; }

            if (offset < 0) throw new Exception("RSDS signature not found");

            var gBytes = new byte[16];
            Array.Copy(bytes, offset + 4, gBytes, 0, 16);
            var guid = new Guid(gBytes).ToString("N").ToUpper();
            var age  = BitConverter.ToUInt32(bytes, offset + 20);
            var id   = guid + age.ToString("X");

            var url        = $"https://msdl.microsoft.com/download/symbols/ntkrnlmp.pdb/{id}/ntkrnlmp.pdb";
            var targetDir  = $@"C:\ProgramData\dbg\sym\ntkrnlmp.pdb\{id}";
            var targetFile = Path.Combine(targetDir, "ntkrnlmp.pdb");

            Directory.CreateDirectory(targetDir);

            if (File.Exists(targetFile))
            {
                EnqueueLog("PDB found in local symbol store.", "Green");
                _state.DseDll = "dse.dll"; _state.PdbSource = "cached";
                EnqueueStatus("PDB", "CACHED");
            }
            else
            {
                EnqueueLog("Downloading PDB from Microsoft symbol server...", "Cyan");
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.UserAgent.ParseAdd("Microsoft-Symbol-Server/10.0.0.0");
                    using var resp = await _http.SendAsync(req);
                    resp.EnsureSuccessStatusCode();
                    File.WriteAllBytes(targetFile, await resp.Content.ReadAsByteArrayAsync());
                    EnqueueLog("PDB downloaded successfully.", "Green");
                    _state.DseDll = "dse.dll"; _state.PdbSource = "live";
                    EnqueueStatus("PDB", "SUCCESS");
                }
                catch (Exception ex)
                {
                    EnqueueLog($"Symbol download failed: {ex.Message}", "Yellow", "[!]");
                    EnqueueLog("Using dse.dll - it will offer the g_CiOptions fallback.", "Yellow", "[!]");
                    _state.DseDll = "dse.dll"; _state.PdbSource = "unavailable";
                    EnqueueStatus("PDB", "FAIL");
                }
            }
        }
        catch (Exception ex)
        {
            EnqueueLog($"PDB check error: {ex.Message}", "Red", "[!]");
            _state.DseDll = "dse.dll"; _state.PdbSource = "unavailable";
            EnqueueStatus("PDB", "FAIL");
        }
    }

    // Fetches the latest release once; later calls reuse the cached payload.
    private async Task<JsonElement?> GetLatestReleaseAsync()
    {
        if (_cachedRelease is { } cached)
            return cached;

        try
        {
            var json = await _http.GetStringAsync(DseLatestReleaseUrl);
            var api  = JsonSerializer.Deserialize<JsonElement>(json);
            _cachedRelease = api;
            return api;
        }
        catch
        {
            return null;
        }
    }

    private async Task RunSyncHashFile()
    {
        var tagCache = Path.Combine(_state.ScriptRoot, "DSE_lasttag.txt");
        var hashFile = Path.Combine(_state.ScriptRoot, "sha256hashes.txt");

        var api = await GetLatestReleaseAsync();
        if (api is null)
        {
            EnqueueLog("Could not fetch release tag. Using cached files if available.", "Yellow", "[!]");
            return;
        }

        var newTag    = api.Value.GetProperty("tag_name").GetString() ?? "";
        var cachedTag = File.Exists(tagCache) ? File.ReadAllText(tagCache).Trim() : "";

        if (cachedTag == newTag && File.Exists(hashFile))
        {
            EnqueueLog($"Hash file up-to-date (tag: {newTag}).", "DarkGray");
            return;
        }

        EnqueueLog($"New release detected ({newTag}). Fetching sha256hashes.txt...", "Cyan");
        try
        {
            foreach (var asset in api.Value.GetProperty("assets").EnumerateArray())
            {
                if (!string.Equals(asset.GetProperty("name").GetString(), "sha256hashes.txt", StringComparison.OrdinalIgnoreCase)) continue;
                File.WriteAllBytes(hashFile, await _http.GetByteArrayAsync(asset.GetProperty("browser_download_url").GetString()!));
                File.WriteAllText(tagCache, newTag);
                EnqueueLog("sha256hashes.txt updated.", "Green");
                return;
            }
            EnqueueLog("sha256hashes.txt not found in release assets.", "Yellow", "[!]");
        }
        catch (Exception ex)
        {
            EnqueueLog($"Failed to fetch sha256hashes.txt: {ex.Message}", "Yellow", "[!]");
        }
    }

    private async Task<bool> RunSyncAsset(string assetName, bool copyToGameDir, string gameDir = "")
    {
        var cachedFile = Path.Combine(_state.ScriptRoot, assetName);
        string? expectedHash = LoadExpectedHashes().GetValueOrDefault(assetName);

        bool needDownload;
        if (expectedHash != null)
        {
            if (File.Exists(cachedFile) && ComputeSha256(cachedFile) == expectedHash)
            {
                EnqueueLog($"{assetName} is up-to-date.", "Green");
                EnqueueStatus($"ASSET:{assetName}", "OK");
                needDownload = false;
            }
            else
            {
                EnqueueLog(File.Exists(cachedFile) ? $"Hash mismatch for {assetName}. Re-downloading..." : $"{assetName} not in cache. Downloading...", "Cyan");
                EnqueueStatus($"ASSET:{assetName}", File.Exists(cachedFile) ? "UPDATING" : "DOWNLOADING");
                needDownload = true;
            }
        }
        else
        {
            if (File.Exists(cachedFile))
            {
                EnqueueLog($"No hash entry for {assetName}. Using cached file.", "Yellow", "[!]");
                EnqueueStatus($"ASSET:{assetName}", "OK");
                needDownload = false;
            }
            else
            {
                EnqueueLog($"{assetName} not cached. Downloading...", "Cyan");
                EnqueueStatus($"ASSET:{assetName}", "DOWNLOADING");
                needDownload = true;
            }
        }

        if (needDownload)
        {
            var api = await GetLatestReleaseAsync();
            if (api is null)
            {
                EnqueueLog($"Could not fetch release information for {assetName}.", "Red", "[!]");
                EnqueueStatus($"ASSET:{assetName}", "FAIL");
                return false;
            }

            try
            {
                string? dlUrl = null;
                foreach (var asset in api.Value.GetProperty("assets").EnumerateArray())
                    if (string.Equals(asset.GetProperty("name").GetString(), assetName, StringComparison.OrdinalIgnoreCase))
                        { dlUrl = asset.GetProperty("browser_download_url").GetString(); break; }

                if (dlUrl == null)
                {
                    EnqueueLog($"{assetName} not found in release assets.", "Red", "[!]");
                    EnqueueStatus($"ASSET:{assetName}", "FAIL");
                    return false;
                }

                File.WriteAllBytes(cachedFile, await _http.GetByteArrayAsync(dlUrl));
                EnqueueLog($"Downloaded {assetName}.", "Green");
                EnqueueStatus($"ASSET:{assetName}", "OK");
            }
            catch (Exception ex)
            {
                EnqueueLog($"Sync failed for {assetName}: {ex.Message}", "Red", "[!]");
                EnqueueStatus($"ASSET:{assetName}", "FAIL");
                return false;
            }
        }

        if (copyToGameDir && !string.IsNullOrEmpty(gameDir))
        {
            try
            {
                var dest = Path.Combine(gameDir, assetName);
                if ((string.Equals(assetName, Exe1, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(assetName, Exe2, StringComparison.OrdinalIgnoreCase)) && File.Exists(dest))
                {
                    File.Copy(dest, dest + ".bak", overwrite: true);
                    EnqueueLog($"Backed up {assetName}", "Green");
                }
                File.Copy(cachedFile, dest, overwrite: true);
                EnqueueLog($"Copied {assetName} to game folder.", "Green");
            }
            catch (Exception ex)
            {
                // The game is probably running and holds the file - report it instead of dying.
                EnqueueLog($"Could not copy {assetName} into the game folder: {ex.Message}", "Red", "[!]");
                EnqueueStatus($"ASSET:{assetName}", "FAIL");
                return false;
            }
        }

        return true;
    }

    // Known DSE game files: the config family (scan targets) and their .bak backups,
    // plus the loader backups. One source for GetDirState, the folder scans and the
    // Previous Setups evidence list.
    private static readonly string[] GameConfigFiles =
    {
        "DenuvOwO.ini", "reflex.ini", "veil.ini", "load.txt",
        "Artifact.json", "illusive.toml", "illusive.dll", "illusive.load_dlls.txt"
    };

    private static readonly string[] GameConfigBakFiles =
    {
        "DenuvOwO.ini.bak", "reflex.ini.bak", "veil.ini.bak", "load.txt.bak",
        "Artifact.json.bak", "illusive.toml.bak", "illusive.load_dlls.txt.bak"
    };

    private static readonly string[] LoaderBakFiles = { "winmm.dll.bak", "version.dll.bak" };

    private static readonly string[] SetupEvidenceFiles =
        new[] { "dse.dll", Exe1, Exe2, "dse.ini" }
        .Concat(GameConfigBakFiles)
        .Concat(new[] { Exe1 + ".bak", Exe2 + ".bak" })
        .Concat(LoaderBakFiles)
        .ToArray();

    private void RunAddSetupScan(string root)
    {
        EnqueueLog("", "Gray", "");
        EnqueueLog($"Searching for existing DSE setups under: {root}", "Cyan");

        var found = new List<string>();
        foreach (var t in SetupEvidenceFiles)
        {
            try
            {
                foreach (var f in Directory.GetFiles(root, t, SearchOption.AllDirectories))
                {
                    var parent = Path.GetDirectoryName(f)!;
                    if (!found.Contains(parent, StringComparer.OrdinalIgnoreCase)) found.Add(parent);
                }
            }
            catch { /* skip inaccessible subfolders */ }
        }

        EnqueueEvent("ADD_SETUP_SCAN_DONE", found.ToArray());
    }

    private void RunScanFolder(string gameRoot)
    {
        EnqueueLog("", "Gray", "");
        EnqueueLog($"Scanning for known files under: {gameRoot}", "Cyan");

        var targets = GameConfigFiles
            .Concat(new[] { Exe1, Exe2 })
            .Concat(GameConfigBakFiles)
            .Concat(new[] { Exe1 + ".bak", Exe2 + ".bak" })
            .Concat(LoaderBakFiles)
            .ToArray();

        var dirs = new List<string>();
        foreach (var t in targets)
        {
            try
            {
                foreach (var f in Directory.GetFiles(gameRoot, t, SearchOption.AllDirectories))
                {
                    var parent = Path.GetDirectoryName(f)!;
                    if (!dirs.Contains(parent)) dirs.Add(parent);
                }
            }
            catch { /* skip inaccessible dirs */ }
        }

        if (dirs.Count == 0)
        {
            EnqueueLog($"No known config files or EXEs found under: {gameRoot}", "Red", "[!]");
            EnqueueEvent("SCAN_EMPTY");
        }
        else if (dirs.Count == 1)
        {
            EnqueueLog($"Found files in: {dirs[0]}", "Green");
            EnqueueEvent("SCAN_SINGLE", dirs[0]);
        }
        else
        {
            EnqueueLog($"Files found in {dirs.Count} locations.", "Yellow");
            EnqueueEvent("SCAN_MULTI", dirs.ToArray());
        }
    }

    private async Task RunDoDll(string gameDir)
    {
        try
        {
            EnqueueLog("", "Gray", "");
            var dll = _state.DseDll;
            if (!await RunSyncAsset(dll, copyToGameDir: true, gameDir: gameDir))
            {
                EnqueueEvent("ACTION_FAIL", $"Could not sync {dll}. Check your internet connection.");
                return;
            }

            EnqueueLog("", "Gray", "");
            EnqueueLog("Checking config files...", "Cyan");

            var denuvPath = Path.Combine(gameDir, "DenuvOwO.ini");
            if (File.Exists(denuvPath))
            {
                var content = File.ReadAllText(denuvPath);
                if (!content.Contains(dll, StringComparison.OrdinalIgnoreCase) &&
                    !content.Contains("[LoadDlls]", StringComparison.OrdinalIgnoreCase))
                {
                    EnqueueEvent("NEED_DENUV_CHOICE", gameDir);
                    return;
                }
            }

            RunProcessConfigs(gameDir);
        }
        catch (Exception ex) { EnqueueEvent("ACTION_FAIL", $"Error: {ex.Message}"); }
    }

    private void RunProcessConfigs(string gameDir)
    {
        var dll      = _state.DseDll;
        bool modified = false;

        // DenuvOwO.ini
        var denuvPath = Path.Combine(gameDir, "DenuvOwO.ini");
        if (File.Exists(denuvPath))
        {
            EnqueueLog("Found DenuvOwO.ini. Processing...", "Cyan");
            var content = File.ReadAllText(denuvPath);
            if (content.Contains(dll, StringComparison.OrdinalIgnoreCase))
            {
                EnqueueLog("  DSE entry already present. Skipping.", "Yellow", "[!]");
            }
            else if (content.Contains("[LoadDlls]", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(denuvPath, denuvPath + ".bak", overwrite: true);
                EnqueueLog("  Backup saved: DenuvOwO.ini.bak", "Green");
                if (ProcessIni(denuvPath, denuvPath + ".tmp", "[LoadDlls]", dll))
                {
                    File.Move(denuvPath + ".tmp", denuvPath, overwrite: true);
                    EnqueueLog($"  Added {dll} under [LoadDlls].", "Green");
                    modified = true;
                }
                else
                {
                    TryDelete(denuvPath + ".tmp");
                    EnqueueLog("  Could not insert into [LoadDlls].", "Red", "[!]");
                }
            }
        }

        // reflex.ini / veil.ini follow the same [load_dlls] rules.
        foreach (var iniName in new[] { "reflex.ini", "veil.ini" })
        {
            var iniPath = Path.Combine(gameDir, iniName);
            if (!File.Exists(iniPath))
                continue;

            EnqueueLog($"Found {iniName}. Checking for [load_dlls] section...", "Cyan");
            var content     = File.ReadAllText(iniPath);
            bool hasSection = content.Contains("[load_dlls]", StringComparison.OrdinalIgnoreCase);
            if (!hasSection)
            {
                if (File.Exists(Path.Combine(gameDir, "load.txt")))
                {
                    EnqueueLog($"  No [load_dlls] and load.txt is present. Skipping {iniName}.", "Yellow", "[!]");
                }
                else if (!content.Contains(dll, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(iniPath, iniPath + ".bak", overwrite: true);
                    EnqueueLog($"  Backup saved: {iniName}.bak", "Green");
                    File.AppendAllText(iniPath, $"\n[load_dlls]\n0 = {dll}");
                    EnqueueLog($"  Added [load_dlls] section with {dll}.", "Green");
                    modified = true;
                }
                else
                {
                    EnqueueLog("  DSE entry already present. Skipping.", "Yellow", "[!]");
                }
            }
            else
            {
                File.Copy(iniPath, iniPath + ".bak", overwrite: true);
                EnqueueLog($"  Backup saved: {iniName}.bak", "Green");
                if (content.Contains(dll, StringComparison.OrdinalIgnoreCase))
                {
                    EnqueueLog("  DSE entry already present. Skipping.", "Yellow", "[!]");
                }
                else if (ProcessIni(iniPath, iniPath + ".tmp", "[load_dlls]", dll))
                {
                    File.Move(iniPath + ".tmp", iniPath, overwrite: true);
                    EnqueueLog($"  Added {dll} under [load_dlls].", "Green");
                    modified = true;
                }
                else
                {
                    TryDelete(iniPath + ".tmp");
                }
            }
        }

        // load.txt
        var loadPath = Path.Combine(gameDir, "load.txt");
        if (File.Exists(loadPath))
        {
            EnqueueLog("Found load.txt. Processing...", "Cyan");
            File.Copy(loadPath, loadPath + ".bak", overwrite: true);
            EnqueueLog("  Backup saved: load.txt.bak", "Green");
            var content = File.ReadAllText(loadPath);
            if (content.Contains(dll, StringComparison.OrdinalIgnoreCase))
            {
                EnqueueLog("  DSE entry already present. Skipping.", "Yellow", "[!]");
            }
            else if (ProcessTxt(loadPath, loadPath + ".tmp", dll))
            {
                File.Move(loadPath + ".tmp", loadPath, overwrite: true);
                EnqueueLog($"  Added {dll} to top of load.txt.", "Green");
                modified = true;
            }
            else
            {
                TryDelete(loadPath + ".tmp");
                EnqueueLog("  Could not insert into load.txt.", "Red", "[!]");
            }
        }

        // Artifact.json uses a JSON array to define DLL load order. Always put DSE first.
        var artifactPath = Path.Combine(gameDir, "Artifact.json");
        if (File.Exists(artifactPath))
        {
            EnqueueLog("Found Artifact.json. Processing load_dlls...", "Cyan");
            try
            {
                var originalJson = File.ReadAllText(artifactPath);
                var json = JsonNode.Parse(originalJson) as JsonObject
                    ?? throw new JsonException("The JSON root must be an object.");

                var loadDllsProperty = json.FirstOrDefault(property =>
                    string.Equals(property.Key, "load_dlls", StringComparison.OrdinalIgnoreCase));
                bool hasArray = loadDllsProperty.Value is JsonArray;
                if (!hasArray)
                {
                    var loadDlls = new JsonArray();
                    var autoload = json.Select((property, index) => (property, index))
                        .FirstOrDefault(x => string.Equals(x.property.Key, "autoload", StringComparison.OrdinalIgnoreCase));
                    if (loadDllsProperty.Key is not null)
                        json[loadDllsProperty.Key] = loadDlls;
                    else if (autoload.property.Key is not null)
                        json.Insert(autoload.index + 1, "load_dlls", loadDlls);
                    else
                        json["load_dlls"] = loadDlls;
                }

                var arrayKey = loadDllsProperty.Key ?? "load_dlls";
                var array = (JsonArray)json[arrayKey]!;
                var originalDlls = array.Select(node => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null)
                    .Where(value => value is not null)
                    .Cast<string>()
                    .ToList();
                var desiredDlls = new List<string> { dll };
                desiredDlls.AddRange(originalDlls.Where(value => !string.Equals(value, dll, StringComparison.OrdinalIgnoreCase)));

                bool unchanged = originalDlls.Count == desiredDlls.Count &&
                    originalDlls.SequenceEqual(desiredDlls, StringComparer.Ordinal);
                if (unchanged && hasArray)
                {
                    EnqueueLog("  DSE is already first in load_dlls. Skipping.", "Yellow", "[!]");
                }
                else
                {
                    array.Clear();
                    foreach (var entry in desiredDlls) array.Add(entry);
                    File.Copy(artifactPath, artifactPath + ".bak", overwrite: true);
                    var formattedJson = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    var compactDlls = JsonSerializer.Serialize(desiredDlls);
                    var loadDllsPattern = "(?s)(\"" + System.Text.RegularExpressions.Regex.Escape(arrayKey) + "\"\\s*:\\s*)\\[[^\\]]*\\]";
                    formattedJson = System.Text.RegularExpressions.Regex.Replace(
                        formattedJson,
                        loadDllsPattern,
                        match => match.Groups[1].Value + compactDlls);
                    File.WriteAllText(artifactPath, formattedJson + Environment.NewLine, new UTF8Encoding(false));
                    EnqueueLog("  Ensured DSE is first in load_dlls. Backup saved: Artifact.json.bak", "Green");
                    modified = true;
                }
            }
            catch (Exception ex)
            {
                EnqueueLog($"  Could not process Artifact.json: {ex.Message}", "Red", "[!]");
            }
        }

        // illusive.toml uses a TOML load_dlls array, with DSE first like Artifact.json.
        var illusivePath = Path.Combine(gameDir, "illusive.toml");
        if (File.Exists(illusivePath))
        {
            EnqueueLog("Found illusive.toml. Processing load_dlls...", "Cyan");
            try
            {
                if (EnsureIllusiveTomlLoadDlls(illusivePath, dll))
                {
                    EnqueueLog("  Ensured DSE is first in load_dlls. Backup saved: illusive.toml.bak", "Green");
                    modified = true;
                }
                else
                {
                    EnqueueLog("  DSE is already first in load_dlls. Skipping.", "Yellow", "[!]");
                }
            }
            catch (Exception ex)
            {
                EnqueueLog($"  Could not process illusive.toml: {ex.Message}", "Red", "[!]");
            }
        }

        // Update an existing Illusive load list, or create one when Illusive is
        // present without either its TOML config or a load list.
        var illusiveDllPath = Path.Combine(gameDir, "illusive.dll");
        var illusiveLoadListPath = Path.Combine(gameDir, "illusive.load_dlls.txt");
        if (File.Exists(illusiveLoadListPath))
        {
            try
            {
                if (EnsureIllusiveLoadList(illusiveLoadListPath, dll))
                {
                    EnqueueLog("Updated illusive.load_dlls.txt with DSE first. Backup saved: illusive.load_dlls.txt.bak", "Green");
                    modified = true;
                }
                else
                {
                    EnqueueLog("DSE is already first in illusive.load_dlls.txt. Skipping.", "Yellow", "[!]");
                }
            }
            catch (Exception ex)
            {
                EnqueueLog($"Could not process illusive.load_dlls.txt: {ex.Message}", "Red", "[!]");
            }
        }
        else if (File.Exists(illusiveDllPath) && !File.Exists(illusivePath))
        {
            File.WriteAllText(illusiveLoadListPath, $"#OPTIONAL{Environment.NewLine}{dll}{Environment.NewLine}", new UTF8Encoding(false));
            EnqueueLog("Found illusive.dll without illusive.toml. Created illusive.load_dlls.txt with DSE enabled.", "Green");
            modified = true;
        }

        if (!modified) EnqueueLog("Config files already up to date.", "Green");

        TrackSetup(gameDir);
        EnqueueLog("", "Gray", "");
        EnqueueEvent("ACTION_DONE", new ActionDoneData(gameDir, GoLaunch: true));
    }

    private void RunDenuvAppend(string gameDir)
    {
        try
        {
            var dll       = _state.DseDll;
            var denuvPath = Path.Combine(gameDir, "DenuvOwO.ini");
            File.Copy(denuvPath, denuvPath + ".bak", overwrite: true);
            EnqueueLog("Backup saved: DenuvOwO.ini.bak", "Green");
            File.AppendAllText(denuvPath, $"\n[LoadDlls]\n0={dll}");
            EnqueueLog($"Appended [LoadDlls] with 0={dll} to DenuvOwO.ini.", "Green");
            RunProcessConfigs(gameDir);
        }
        catch (Exception ex) { EnqueueEvent("ACTION_FAIL", $"Error: {ex.Message}"); }
    }

    private async Task RunDenuvPefile(string gameDir)
    {
        try
        {
            string? target = null, outFile = null;
            if      (File.Exists(Path.Combine(gameDir, "winmm.dll")))   { target = "winmm.dll";   outFile = "winmm_mod.dll"; }
            else if (File.Exists(Path.Combine(gameDir, "version.dll"))) { target = "version.dll"; outFile = "version_mod.dll"; }

            if (target == null)
            {
                EnqueueLog("No winmm.dll or version.dll found. Cannot patch.", "Red", "[!]");
                EnqueueEvent("ACTION_FAIL", "No winmm.dll or version.dll found in the game folder.");
                return;
            }

            EnqueueLog($"Found {target}. Processing pefile.exe...", "Cyan");
            if (!await RunSyncAsset("pefile.exe", copyToGameDir: true, gameDir: gameDir)) { EnqueueEvent("ACTION_FAIL", "Failed to sync pefile.exe."); return; }

            var targetPath = Path.Combine(gameDir, target);
            File.Copy(targetPath, targetPath + ".bak", overwrite: true);
            EnqueueLog($"Backup saved: {target}.bak", "Green");

            var dll = _state.DseDll;
            EnqueueLog($"Running pefile.exe {target} {dll} DseDll...", "Cyan");
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = Path.Combine(gameDir, "pefile.exe"),
                Arguments        = $"\"{targetPath}\" \"{Path.Combine(gameDir, dll)}\" DseDll",
                WorkingDirectory = gameDir,
                CreateNoWindow   = true
            })!;
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
            {
                EnqueueLog("pefile.exe failed. Restoring backup.", "Red", "[!]");
                File.Move(targetPath + ".bak", targetPath, overwrite: true);
                EnqueueEvent("ACTION_FAIL", "pefile.exe failed. Backup restored.");
                return;
            }

            var outPath = Path.Combine(gameDir, outFile!);
            if (!File.Exists(outPath))
            {
                EnqueueLog($"Expected output {outFile} not found. Restoring backup.", "Red", "[!]");
                File.Move(targetPath + ".bak", targetPath, overwrite: true);
                EnqueueEvent("ACTION_FAIL", "pefile.exe output not found. Backup restored.");
                return;
            }

            File.Move(outPath, targetPath, overwrite: true);
            EnqueueLog($"Renamed {outFile} -> {target}", "Green");
            TrackSetup(gameDir);
            EnqueueLog("", "Gray", "");
            EnqueueEvent("ACTION_DONE", new ActionDoneData(gameDir, GoLaunch: true));
        }
        catch (Exception ex) { EnqueueEvent("ACTION_FAIL", $"Error: {ex.Message}"); }
    }

    private async Task RunDoCombined(string gameDir, string exeName)
    {
        try
        {
            var dll = _state.DseDll;
            EnqueueLog("", "Gray", "");
            EnqueueLog("Copying files to game folder...", "Cyan");
            if (!await RunSyncAsset(dll, copyToGameDir: true, gameDir: gameDir)) { EnqueueEvent("ACTION_FAIL", "Asset sync failed."); return; }
            if (!await RunSyncAsset(exeName, copyToGameDir: true, gameDir: gameDir)) { EnqueueEvent("ACTION_FAIL", "Asset sync failed."); return; }

            var loadPath = Path.Combine(gameDir, "load.txt");
            if (File.Exists(loadPath))
            {
                EnqueueLog("Found load.txt. Processing...", "Cyan");
                File.Copy(loadPath, loadPath + ".bak", overwrite: true);
                EnqueueLog("  Backup saved: load.txt.bak", "Green");
                var content = File.ReadAllText(loadPath);
                if (content.Contains(dll, StringComparison.OrdinalIgnoreCase))
                    EnqueueLog("  DSE entry already present. Skipping.", "Yellow", "[!]");
                else if (ProcessTxt(loadPath, loadPath + ".tmp", dll))
                {
                    File.Move(loadPath + ".tmp", loadPath, overwrite: true);
                    EnqueueLog($"  Added {dll} to top of load.txt.", "Green");
                }
                else
                {
                    TryDelete(loadPath + ".tmp");
                    EnqueueLog("  Could not insert into load.txt.", "Red", "[!]");
                }
            }

            TrackSetup(gameDir);
            EnqueueLog("", "Gray", "");
            EnqueueEvent("ACTION_DONE", new ActionDoneData(gameDir, GoLaunch: true));
        }
        catch (Exception ex) { EnqueueEvent("ACTION_FAIL", $"Error: {ex.Message}"); }
    }

    private async Task RunDoExe(string gameDir, string exeName)
    {
        try
        {
            EnqueueLog("", "Gray", "");
            if (!await RunSyncAsset(_state.DseDll, copyToGameDir: true, gameDir: gameDir)) { EnqueueEvent("ACTION_FAIL", "Asset sync failed."); return; }
            if (!await RunSyncAsset(exeName, copyToGameDir: true, gameDir: gameDir)) { EnqueueEvent("ACTION_FAIL", "Asset sync failed."); return; }
            TrackSetup(gameDir);
            EnqueueLog("", "Gray", "");
            EnqueueEvent("ACTION_DONE", new ActionDoneData(gameDir, GoLaunch: true));
        }
        catch (Exception ex) { EnqueueEvent("ACTION_FAIL", $"Error: {ex.Message}"); }
    }

    private void UndoDir(string gameDir)
    {
        var dll = _state.DseDll;

        // Remove only the exact file shape this feature creates, preserving any
        // subsequently edited load list.
        var illusiveLoadListPath = Path.Combine(gameDir, "illusive.load_dlls.txt");
        var generatedIllusiveLoadList = $"#OPTIONAL{Environment.NewLine}{dll}{Environment.NewLine}";
        if (!File.Exists(illusiveLoadListPath + ".bak") && File.Exists(illusiveLoadListPath) &&
            string.Equals(File.ReadAllText(illusiveLoadListPath), generatedIllusiveLoadList, StringComparison.Ordinal))
        {
            File.Delete(illusiveLoadListPath);
            EnqueueLog("Deleted: illusive.load_dlls.txt", "Green");
        }

        var restores = new[]
        {
            ("DenuvOwO.ini.bak", "DenuvOwO.ini", false),
            ("reflex.ini.bak",   "reflex.ini",   false),
            ("veil.ini.bak",     "veil.ini",     false),
            ("load.txt.bak",     "load.txt",     false),
            ("Artifact.json.bak", "Artifact.json", false),
            ("illusive.toml.bak", "illusive.toml", false),
            ("illusive.load_dlls.txt.bak", "illusive.load_dlls.txt", false),
            ("winmm.dll.bak",    "winmm.dll",    true),
            ("version.dll.bak",  "version.dll",  true),
            (Exe1 + ".bak",       Exe1,           false),
            (Exe2 + ".bak",       Exe2,           false),
        };

        foreach (var (bak, orig, deleteFirst) in restores)
        {
            var bakPath  = Path.Combine(gameDir, bak);
            var origPath = Path.Combine(gameDir, orig);
            if (!File.Exists(bakPath)) continue;
            if (deleteFirst) TryDelete(origPath);
            File.Move(bakPath, origPath, overwrite: true);
            EnqueueLog($"Restored: {orig}", "Green");
        }

        foreach (var f in new[] { Path.Combine(gameDir, dll), Path.Combine(gameDir, "dse.ini"), Path.Combine(gameDir, "pefile.exe") })
            if (File.Exists(f)) { File.Delete(f); EnqueueLog($"Deleted: {Path.GetFileName(f)}", "Green"); }
    }

    private void RunUndo(string gameDir)
    {
        try
        {
            EnqueueLog("", "Gray", "");
            EnqueueLog("Undoing changes...", "Yellow");
            UndoDir(gameDir);
            EnqueueLog("", "Gray", "");
            EnqueueLog("Undo complete. All original files restored.", "Green");
            EnqueueLog("", "Gray", "");
            EnqueueEvent("UNDO_DONE");
        }
        catch (Exception ex) { EnqueueEvent("ACTION_FAIL", $"Undo error: {ex.Message}"); }
    }

    private void RunUndoSetups(List<string> dirs)
    {
        EnqueueLog("", "Gray", "");
        EnqueueLog($"Undoing {dirs.Count} folder(s)...", "Yellow");
        foreach (var dir in dirs)
        {
            try
            {
                EnqueueLog($"Undoing: {dir}", "Yellow");
                UndoDir(dir);
            }
            catch (Exception ex)
            {
                EnqueueLog($"Undo failed for {dir}: {ex.Message}", "Red", "[!]");
            }
        }
        EnqueueLog("", "Gray", "");
        EnqueueLog("Undo pass complete.", "Green");
        EnqueueEvent("SETUPS_UPDATED");
    }

    private void RunLaunchExe(string gameDir, string exeName)
    {
        try
        {
            EnqueueLog($"Launching: {exeName}", "Green");
            bool needsAdmin = string.Equals(exeName, Exe1, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(exeName, Exe2, StringComparison.OrdinalIgnoreCase);
            Process.Start(new ProcessStartInfo
            {
                FileName         = Path.Combine(gameDir, exeName),
                WorkingDirectory = gameDir,
                UseShellExecute  = true,
                Verb             = needsAdmin ? "runas" : ""
            });
        }
        catch (Exception ex) { EnqueueLog($"Launch error: {ex.Message}", "Red", "[!]"); }
    }

    // ─── File processing ──────────────────────────────────────────────────────

    private static bool EnsureIllusiveTomlLoadDlls(string path, string dll)
    {
        var content = File.ReadAllText(path);
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var tableMatch = System.Text.RegularExpressions.Regex.Match(
            content, @"(?m)^[ \t]*\[[^\]\r\n]+\][ \t]*(?:#.*)?$");
        int rootEnd = tableMatch.Success ? tableMatch.Index : content.Length;
        var root = content[..rootEnd];
        var propertyMatch = System.Text.RegularExpressions.Regex.Match(
            root, @"(?im)^[ \t]*load_dlls[ \t]*=[ \t]*");

        var existingDlls = new List<string>();
        int valueStart = -1;
        int valueEnd = -1;
        bool existingValueWasArray = false;

        if (propertyMatch.Success)
        {
            valueStart = propertyMatch.Index + propertyMatch.Length;
            while (valueStart < rootEnd && (content[valueStart] == ' ' || content[valueStart] == '\t'))
                valueStart++;

            if (valueStart < rootEnd && content[valueStart] == '[')
            {
                valueEnd = FindTomlArrayEnd(content, valueStart);
                if (valueEnd < 0 || valueEnd >= rootEnd)
                    throw new FormatException("The load_dlls array is not closed.");

                existingDlls = ParseTomlStringArray(content, valueStart + 1, valueEnd);
                valueEnd++;
                existingValueWasArray = true;
            }
            else
            {
                int lineEnd = content.IndexOfAny(new[] { '\r', '\n' }, valueStart);
                if (lineEnd < 0 || lineEnd > rootEnd) lineEnd = rootEnd;
                int comment = content.IndexOf('#', valueStart, lineEnd - valueStart);
                valueEnd = comment >= 0 ? comment : lineEnd;
            }
        }

        var desiredDlls = new List<string> { dll };
        desiredDlls.AddRange(existingDlls.Where(value =>
            !string.Equals(value, dll, StringComparison.OrdinalIgnoreCase)));

        if (propertyMatch.Success)
        {
            bool unchanged = existingValueWasArray &&
                existingDlls.SequenceEqual(desiredDlls, StringComparer.Ordinal);
            if (unchanged) return false;

            string serializedArray = JsonSerializer.Serialize(desiredDlls);
            string suffix = valueEnd < content.Length && content[valueEnd] == '#' ? " " : "";
            var updated = content[..valueStart] + serializedArray + suffix + content[valueEnd..];
            File.Copy(path, path + ".bak", overwrite: true);
            File.WriteAllText(path, updated, new UTF8Encoding(false));
            return true;
        }

        string newProperty = "load_dlls = " + JsonSerializer.Serialize(desiredDlls);
        var autoloadMatch = System.Text.RegularExpressions.Regex.Match(
            root, @"(?im)^[ \t]*autoload[ \t]*=[^\r\n]*");
        int insertionPoint;
        string insertion;
        if (autoloadMatch.Success)
        {
            insertionPoint = autoloadMatch.Index + autoloadMatch.Length;
            insertion = newline + newProperty;
            if (insertionPoint == content.Length) insertion += newline;
        }
        else
        {
            insertionPoint = rootEnd;
            string prefix = insertionPoint > 0 && content[insertionPoint - 1] != '\r' && content[insertionPoint - 1] != '\n'
                ? newline
                : "";
            insertion = prefix + newProperty + newline;
        }

        File.Copy(path, path + ".bak", overwrite: true);
        File.WriteAllText(path, content.Insert(insertionPoint, insertion), new UTF8Encoding(false));
        return true;
    }

    private static bool EnsureIllusiveLoadList(string path, string dll)
    {
        var original = File.ReadAllText(path);
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        var hasTrailingNewline = lines.Count > 0 && lines[^1].Length == 0;
        if (hasTrailingNewline) lines.RemoveAt(lines.Count - 1);

        var optionalHeaderIndex = lines.FindIndex(line =>
            line.Trim().Equals("#OPTIONAL", StringComparison.OrdinalIgnoreCase) ||
            line.Trim().Equals("OPTIONAL", StringComparison.OrdinalIgnoreCase));

        if (optionalHeaderIndex >= 0)
        {
            lines[optionalHeaderIndex] = "#OPTIONAL";
            for (var i = lines.Count - 1; i >= 0; i--)
                if (i != optionalHeaderIndex && IsDllEntry(lines[i], dll)) lines.RemoveAt(i);

            var insertionIndex = optionalHeaderIndex + 1;
            while (insertionIndex < lines.Count && string.IsNullOrWhiteSpace(lines[insertionIndex])) insertionIndex++;
            lines.Insert(insertionIndex, dll);
        }
        else
        {
            for (var i = lines.Count - 1; i >= 0; i--)
                if (IsDllEntry(lines[i], dll)) lines.RemoveAt(i);
            lines.Insert(0, dll);
        }

        var updated = string.Join(newline, lines) + (hasTrailingNewline ? newline : string.Empty);
        if (string.Equals(original, updated, StringComparison.Ordinal)) return false;

        var backupPath = path + ".bak";
        if (!File.Exists(backupPath)) File.Copy(path, backupPath);
        File.WriteAllText(path, updated, new UTF8Encoding(false));
        return true;
    }

    private static bool IsDllEntry(string line, string dll)
    {
        var value = line.Trim();
        if (value.StartsWith('#')) return false;
        value = value.Trim('"', '\'');
        value = value.Replace('/', Path.DirectorySeparatorChar);
        return string.Equals(Path.GetFileName(value), dll, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIllusiveLoadDllFirst(string path, string dll)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            var index = 0;
            while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index])) index++;
            if (index < lines.Length && (lines[index].Trim().Equals("#OPTIONAL", StringComparison.OrdinalIgnoreCase)
                || lines[index].Trim().Equals("OPTIONAL", StringComparison.OrdinalIgnoreCase))) index++;
            while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index])) index++;
            return index < lines.Length && IsDllEntry(lines[index], dll);
        }
        catch
        {
            return false;
        }
    }

    private static int FindTomlArrayEnd(string text, int openingBracket)
    {
        int depth = 0;
        char quote = '\0';
        bool escaped = false;
        bool inComment = false;

        for (int i = openingBracket; i < text.Length; i++)
        {
            char c = text[i];
            if (inComment)
            {
                if (c is '\r' or '\n') inComment = false;
                continue;
            }
            if (quote != '\0')
            {
                if (quote == '"' && escaped) { escaped = false; continue; }
                if (quote == '"' && c == '\\') { escaped = true; continue; }
                if (c == quote) quote = '\0';
                continue;
            }

            if (c == '#') { inComment = true; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '[') depth++;
            else if (c == ']' && --depth == 0) return i;
        }

        return -1;
    }

    private static List<string> ParseTomlStringArray(string text, int start, int end)
    {
        var values = new List<string>();
        for (int i = start; i < end;)
        {
            if (char.IsWhiteSpace(text[i]) || text[i] == ',') { i++; continue; }
            if (text[i] == '#')
            {
                while (i < end && text[i] is not ('\r' or '\n')) i++;
                continue;
            }

            char quote = text[i];
            if (quote is not ('"' or '\''))
            {
                while (i < end && text[i] is not (',' or '#')) i++;
                continue;
            }

            int tokenStart = i++;
            bool escaped = false;
            while (i < end)
            {
                char c = text[i++];
                if (quote == '"' && escaped) { escaped = false; continue; }
                if (quote == '"' && c == '\\') { escaped = true; continue; }
                if (c == quote) break;
            }

            string token = text[tokenStart..i];
            if (quote == '"')
            {
                var value = JsonSerializer.Deserialize<string>(token);
                if (value is not null) values.Add(value);
            }
            else if (token.Length >= 2)
            {
                values.Add(token[1..^1]);
            }
        }

        return values;
    }

    private static bool ProcessIni(string src, string dst, string header, string dll)
    {
        var lines    = File.ReadAllLines(src);
        var output   = new List<string>();
        bool inSection = false, inserted = false, useSpaces = false;
        var secVals  = new List<string>();

        foreach (var line in lines)
        {
            if (line.Trim().StartsWith("["))
            {
                if (inSection)
                {
                    int idx = 0;
                    output.Add(useSpaces ? $"{idx} = {dll}" : $"{idx}={dll}"); idx++;
                    foreach (var v in secVals) output.Add(useSpaces ? $"{idx++} = {v}" : $"{idx++}={v}");
                    inserted = true; inSection = false;
                }
                output.Add(line);
                if (string.Equals(line.Trim(), header, StringComparison.OrdinalIgnoreCase))
                    { inSection = true; secVals.Clear(); }
            }
            else if (inSection)
            {
                var m = System.Text.RegularExpressions.Regex.Match(line.Trim(), @"^(\d+)\s*=\s*(.*)");
                if (m.Success)
                {
                    var val = m.Groups[2].Value.Trim();
                    if (line.Contains(" = ")) useSpaces = true;
                    if (!string.Equals(val, dll, StringComparison.OrdinalIgnoreCase)) secVals.Add(val);
                }
            }
            else output.Add(line);
        }

        if (inSection && !inserted)
        {
            int idx = 0;
            output.Add(useSpaces ? $"{idx} = {dll}" : $"{idx}={dll}"); idx++;
            foreach (var v in secVals) output.Add(useSpaces ? $"{idx++} = {v}" : $"{idx++}={v}");
            inserted = true;
        }

        File.WriteAllLines(dst, output, new UTF8Encoding(false));
        return inserted;
    }

    private static bool ProcessTxt(string src, string dst, string dll)
    {
        var lines  = File.ReadAllLines(src);
        var output = new List<string> { dll };
        foreach (var l in lines)
            if (!string.Equals(l.Trim(), dll, StringComparison.OrdinalIgnoreCase) && l.Trim() != "")
                output.Add(l);
        File.WriteAllLines(dst, output, new UTF8Encoding(false));
        return true;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var fs  = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToUpper();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static SolidColorBrush Brush(string hex)
        => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
}

// ─── Helper records ───────────────────────────────────────────────────────────

internal record ActionDoneData(string GameDir, bool GoLaunch);
