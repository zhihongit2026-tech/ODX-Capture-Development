using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Odx.App;

public sealed record Arma3HudInstallResult(string GameRoot, string ModPath, string? PreviousBackup);

public static class Arma3HudInstaller
{
    public static string? FindGameRoot()
    {
        foreach (var processName in new[] { "arma3_x64", "arma3" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        string? exe = process.MainModule?.FileName;
                        if (!string.IsNullOrWhiteSpace(exe))
                        {
                            string? root = Path.GetDirectoryName(exe);
                            if (IsGameRoot(root)) return root;
                        }
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                }
            }
        }

        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (!string.IsNullOrWhiteSpace(steam))
        {
            steam = steam.Replace('/', Path.DirectorySeparatorChar);
            libraries.Add(steam);
            string libraryFile = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryFile))
            {
                string text = File.ReadAllText(libraryFile);
                foreach (Match match in Regex.Matches(text, @"""path""\s+""([^""]+)"""))
                    libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\").Replace('/', Path.DirectorySeparatorChar));
            }
        }

        foreach (string library in libraries)
        {
            string candidate = Path.Combine(library, "steamapps", "common", "Arma 3");
            if (IsGameRoot(candidate)) return candidate;
        }
        return null;
    }

    public static Arma3HudInstallResult Install(string sourceModFolder, string gameRoot)
    {
        if (Arma3HudBridge.IsArma3Running())
            throw new InvalidOperationException("Hãy thoát Arma 3 hoàn toàn trước khi cài @ODX_HUD.");

        if (!IsGameRoot(gameRoot))
            throw new DirectoryNotFoundException("Thư mục đã chọn không chứa arma3_x64.exe.");

        string source = Path.GetFullPath(sourceModFolder);
        string sourcePbo = Path.Combine(source, "addons", "odx_hud.pbo");
        string sourceDll = Path.Combine(source, "ODX_HUD_x64.dll");
        if (!File.Exists(sourcePbo) || !File.Exists(sourceDll))
            throw new FileNotFoundException("Gói ODX chưa có odx_hud.pbo hoặc ODX_HUD_x64.dll. Hãy dùng bản build có kèm Arma 3 HUD Bridge.");

        string destination = Path.Combine(gameRoot, "@ODX_HUD");
        string? backup = null;
        if (Directory.Exists(destination))
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
            backup = destination + ".backup-" + stamp;
            Directory.Move(destination, backup);
        }

        try
        {
            CopyDirectory(source, destination);
            if (!File.Exists(Path.Combine(destination, "addons", "odx_hud.pbo")) ||
                !File.Exists(Path.Combine(destination, "ODX_HUD_x64.dll")))
                throw new IOException("Kiểm tra sau cài đặt @ODX_HUD không thành công.");
            return new Arma3HudInstallResult(gameRoot, destination, backup);
        }
        catch
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
            if (backup is not null && Directory.Exists(backup) && !Directory.Exists(destination))
                Directory.Move(backup, destination);
            throw;
        }
    }

    private static bool IsGameRoot(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "arma3_x64.exe"));

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (string directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
}
