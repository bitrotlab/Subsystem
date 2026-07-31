using System.Text.RegularExpressions;

namespace SubsystemPatcher;

/// <summary>
/// Best-effort discovery of the game's Data/Managed folder across the places Steam
/// installs it on Linux (native, Flatpak, Snap) and Windows.
/// </summary>
internal static partial class GameLocator
{
    private const string GameFolder = "Deserts of Kharak";
    private static readonly string[] ManagedTail = ["Data", "Managed"];

    public static string? FindManagedDirectory()
    {
        foreach (var steam in SteamRoots())
        {
            foreach (var library in LibraryFolders(steam))
            {
                var managed = Path.Combine(
                    new[] { library, "steamapps", "common", GameFolder }.Concat(ManagedTail).ToArray());

                if (File.Exists(Path.Combine(managed, "BBI.Unity.Game.dll")))
                    return managed;
            }
        }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidates = new List<string>
        {
            // Flatpak Steam — where this shows up on an immutable Fedora / Bazzite style install.
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"),
            // macOS
            Path.Combine(home, "Library", "Application Support", "Steam"),
        };

        if (OperatingSystem.IsWindows())
        {
            candidates.Add(@"C:\Program Files (x86)\Steam");
            candidates.Add(@"C:\Program Files\Steam");
            foreach (var drive in DriveInfo.GetDrives())
                candidates.Add(Path.Combine(drive.Name, "Steam"));
        }

        return candidates.Where(Directory.Exists).Distinct();
    }

    /// <summary>
    /// A Steam root plus every extra library declared in libraryfolders.vdf, so installs on a
    /// second drive are found too.
    /// </summary>
    private static IEnumerable<string> LibraryFolders(string steamRoot)
    {
        yield return steamRoot;

        foreach (var vdf in new[]
                 {
                     Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steamRoot, "config", "libraryfolders.vdf"),
                 })
        {
            if (!File.Exists(vdf)) continue;

            string text;
            try { text = File.ReadAllText(vdf); }
            catch { continue; }

            foreach (Match m in PathEntry().Matches(text))
            {
                var p = m.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(p)) yield return p;
            }
        }
    }

    [GeneratedRegex("\"path\"\\s*\"([^\"]+)\"")]
    private static partial Regex PathEntry();
}
