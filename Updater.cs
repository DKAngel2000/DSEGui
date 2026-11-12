// ─── Auto-update (GitHub releases, prompt-first) ─────────────────────────────
//
// On startup the app asks api.github.com for the latest release of
// DKAngel2000/DSEGui. If its tag (v<Version>) is newer than the running
// assembly's informational version, the user is asked once; on "Yes" the
// DseGui.exe asset is downloaded beside the current exe, validated, and
// swapped in:
//
//   DseGui.exe  → DseGui.exe.old      (NTFS allows renaming a running image)
//   DseGui.exe.update → DseGui.exe
//
// then the new build is started and this instance exits. The leftover
// .old file is deleted at the start of the next run.
//
// Debug builds never self-update (#if !DEBUG at the hook site).

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace DseGui;

internal static class Updater
{
    private const string LatestUrl = "https://api.github.com/repos/DKAngel2000/DSEGui/releases/latest";
    private const string AssetName = "DseGui.exe";
    private const string UpdateSuffix = ".update";
    private const string OldSuffix = ".old";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// Checks GitHub for a newer release and, with the user's approval,
    /// installs it and starts the new build. Returns true when the caller
    /// must exit (an update was applied and the new build was launched).
    /// Every other outcome — up to date, offline, declined, failed — logs
    /// why and returns false so the app continues normally.
    /// </summary>
    public static async Task<bool> CheckAndPromptAsync(System.Windows.Window? owner, Action<string, string, string> log)
    {
        try
        {
            DeleteLeftoverOldFile(log);

            string current = CurrentVersion();
            string tag;
            string assetUrl;

            _http.DefaultRequestHeaders.UserAgent.ParseAdd("DseGui-Updater/1.0");
            using (var resp = await _http.GetAsync(LatestUrl))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    log($"Update check skipped - GitHub returned {resp.StatusCode}.", "DarkGray", "");
                    return false;
                }
                using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
                tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                assetUrl = doc.RootElement.TryGetProperty("assets", out var assets)
                    ? assets.EnumerateArray()
                        .FirstOrDefault(a => a.TryGetProperty("name", out var n) && n.GetString() == AssetName)
                        .TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : ""
                    : "";
            }

            if (tag == "" || assetUrl == "")
            {
                log("Update check skipped - the latest release has no DseGui.exe asset.", "DarkGray", "");
                return false;
            }

            string remote = Normalize(tag);
            if (Compare(remote, current) <= 0)
            {
                log($"App is up to date ({current}).", "DarkGray", "");
                return false;
            }

            var choice = System.Windows.MessageBox.Show(
                owner,
                $"A new version of DSE Setup Tool is available.\n\n" +
                $"Installed version:  {current}\n" +
                $"New version:        {tag}\n\n" +
                "Download and install now?",
                "Update available",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question,
                System.Windows.MessageBoxResult.Yes);

            if (choice != System.Windows.MessageBoxResult.Yes)
            {
                log("Update declined - continuing with the installed version.", "Yellow", "");
                return false;
            }

            string exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (exePath == "" || !File.Exists(exePath))
                throw new IOException("Could not locate the running executable.");

            log($"Downloading update {tag}...", "Cyan", "");
            string exeDir = Path.GetDirectoryName(exePath)!;
            string updatePath = Path.Combine(exeDir, AssetName + UpdateSuffix);
            using (var dl = await _http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                dl.EnsureSuccessStatusCode();
                await using var src = dl.Content.ReadAsStream();
                await using var dst = new FileStream(updatePath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                await src.CopyToAsync(dst);
            }

            await using (var fs = new FileStream(updatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length < 2 || fs.ReadByte() != 'M' || fs.ReadByte() != 'Z')
                    throw new InvalidDataException("The downloaded file is not a valid executable.");
            }

            string oldPath = exePath + OldSuffix;
            File.Move(exePath, oldPath);
            try
            {
                File.Move(updatePath, exePath);
            }
            catch
            {
                // Put the original back so the running app is not orphaned.
                File.Move(oldPath, exePath);
                throw;
            }

            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
            log($"Update installed - restarting as {tag}.", "Green", "");
            return true;
        }
        catch (Exception ex)
        {
            log($"Auto-update failed: {ex.Message} - continuing with the installed version.", "Yellow", "");
            return false;
        }
    }

    private static void DeleteLeftoverOldFile(Action<string, string, string> log)
    {
        try
        {
            string exePath = Environment.ProcessPath ?? "";
            if (exePath == "") return;
            string oldPath = exePath + OldSuffix;
            if (File.Exists(oldPath))
            {
                File.Delete(oldPath);
                log("Removed the leftover DseGui.exe.old from the previous update.", "DarkGray", "");
            }
        }
        catch
        {
            // A locked .old is not fatal; the swap below will fail loudly if it matters.
        }
    }

    private static string CurrentVersion()
    {
        string? raw = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return Normalize(raw);
    }

    /// <summary>The running build's version for display, e.g. "1.1.2".</summary>
    internal static string DisplayVersion() => CurrentVersion();

    /// <summary>Trims a leading v and any -suffix/+build part: "v1.2.0-beta" → "1.2.0".</summary>
    private static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "0.0.0";
        string s = version.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(new[] { '+', '-' });
        if (cut > 0) s = s[..cut];
        return s.Length == 0 ? "0.0.0" : s;
    }

    private static int Compare(string a, string b)
    {
        var va = TryParse(a);
        var vb = TryParse(b);
        if (va != null && vb != null) return va.CompareTo(vb);
        return string.CompareOrdinal(a, b);
    }

    private static Version? TryParse(string s)
    {
        var parts = s.Split('.');
        if (parts.Length == 0 || parts.Length > 4 || parts.Any(p => p.Length == 0 || !int.TryParse(p, out _)))
            return null;
        return new Version(string.Join(".", parts));
    }
}
