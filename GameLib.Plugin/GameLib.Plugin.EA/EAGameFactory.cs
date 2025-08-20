using Gamelib.Core.Util;
using GameLib.Core;
using GameLib.Plugin.EA.Model;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using static GameLib.Plugin.EA.Model.EAGameManifest;

namespace GameLib.Plugin.EA;

internal static class EAGameFactory
{
    private static readonly string Os = GetOs();
    private static readonly uint OsArch = GetOsArch();

    /// <summary>
    /// Get games installed for the EA App launcher
    /// </summary>
    public static IEnumerable<EAGame> GetGames(ILauncher launcher, CancellationToken cancellationToken = default)
    {
        return GetInstallerDataXmlPaths()
            .AsParallel()
            .WithCancellation(cancellationToken)
            .Select(DeserializeManifest)
            .Where(g => g is not null)
            .Select(g => AddLauncherId(launcher, g!))
            .Select(AddLocalCatalogData)
            // .Select(g => AddExecutables(launcher, g))
            .ToList();
    }

    private static EAGame? DeserializeManifest(string installerXmlPath)
    {
        if (string.IsNullOrWhiteSpace(installerXmlPath) || !File.Exists(installerXmlPath))
            return null;

        // For EA we can already infer InstallDir from the xml path
        var installDir = PathUtil.Sanitize(Directory.GetParent(installerXmlPath)?.Parent?.FullName) ?? string.Empty;
        if (string.IsNullOrEmpty(installDir))
            return null;

        // We’ll parse contentIDs + basic locale quickly to build the minimal object
        try
        {
            var xdoc = XDocument.Load(installerXmlPath);
            var root = xdoc.Root ?? throw new InvalidDataException("Missing root element");

            var manifest = new EAGameManifest(root);

            var id = manifest.ContentIds.FirstOrDefault() ?? string.Empty;
            if (string.IsNullOrEmpty(id))
                return null;

            var game = new EAGame
            {
                Id = id,
                InstallDir = installDir,
                LaunchString = $"origin2://game/launch?offerIds={id}",
                InstallDate = PathUtil.GetCreationTime(installDir) ?? DateTime.MinValue,
                // Name/Executable/Executables will be filled in AddLocalCatalogData
            };

            return game;
        }
        catch
        {
            return null;
        }
    }

    private static EAGame AddLauncherId(ILauncher launcher, EAGame game)
    {
        game.LauncherId = launcher.Id;
        return game;
    }

    private static EAGame AddLocalCatalogData(EAGame game)
    {
        var installerXmlPath = Path.Combine(game.InstallDir, "__Installer", "installerdata.xml");
        if (!File.Exists(installerXmlPath))
            return game;

        try
        {
            var xdoc = XDocument.Load(installerXmlPath);
            var root = xdoc.Root ?? throw new InvalidDataException("Missing root element");
            var manifest = new EAGameManifest(root);

            // Try to read Locale from the same registry key as the launcher Install Dir
            if (string.IsNullOrEmpty(game.Locale) && manifest.Launchers.Count > 0)
            {
                var first = manifest.Launchers[0];
                _ = EAGameRegistryUtil.TrimBracketPrefix(first.FilePath, out var bracket);
                if (!string.IsNullOrEmpty(bracket))
                {
                    var (_, regLocale) = EAGameRegistryUtil.TryGetInstallDirAndLocale(bracket);
                    if (!string.IsNullOrWhiteSpace(regLocale))
                    {
                        game.Locale = regLocale;
                        if (manifest.GameTitles.TryGetValue(regLocale, out string Name))
                            game.Name = Name;
                    }
                }
            }

            // Fallback to manifest locales if registry didn't provide one
            if (string.IsNullOrEmpty(game.Name))
                game.Name = manifest.GameTitles.Values.FirstOrDefault() ?? game.Name;

            // Fallback to manifest locales if registry didn't provide one
            if (string.IsNullOrEmpty(game.Locale))
                game.Locale = manifest.Locales.FirstOrDefault() ?? game.Locale;

            // Resolve all launcher file paths
            List<string> resolved = new List<string>();
            foreach (var ln in manifest.Launchers)
            {
                string? abs = EAGameRegistryUtil.ResolveBracketOrAbsolute(
                    ln.FilePath,
                    baseInstallDir: game.InstallDir,
                    onlyExisting: true,
                    stripArgs: true
                );
                if (!string.IsNullOrEmpty(abs))
                    resolved.Add(abs);
            }

            // Pick a “best” executable (like Origin: prefer non-trial & 64-bit match)
            if (resolved.Count == 0)
            {
                // No luck resolving via registry expressions — try lazy tail + base
                foreach (Launcher ln in manifest.Launchers)
                {
                    string tail = EAGameRegistryUtil.TrimBracketPrefix(ln.FilePath, out _);
                    if (!string.IsNullOrEmpty(tail))
                    {
                        string? cand = PathUtil.Sanitize(Path.Combine(game.InstallDir, tail));
                        if (!string.IsNullOrEmpty(cand) && File.Exists(cand) && PathUtil.IsExecutable(cand))
                            resolved.Add(cand);
                    }
                }
            }

            // Merge into game.Executables
            if (resolved.Count > 0)
            {
                List<string> existing = game.Executables?.ToList() ?? new List<string>();
                existing.AddRange(resolved);
                game.Executables = existing.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                // Prefer 64-bit, non-trial first if we can map back to a launcher; else first found
                string? pick = PickBestExecutable(manifest, game.Executables);
                if (!string.IsNullOrEmpty(pick))
                {
                    game.Executable = pick;
                    game.WorkingDir = Path.GetDirectoryName(pick) ?? string.Empty;
                }
                else
                {
                    game.Executable = game.Executables.FirstOrDefault() ?? string.Empty;
                    game.WorkingDir = string.IsNullOrEmpty(game.Executable) ? string.Empty
                                     : Path.GetDirectoryName(game.Executable) ?? string.Empty;
                }
            }
        }
        catch
        {
            // ignore and keep whatever we had
        }

        return game;
    }

    private static string? PickBestExecutable(EAGameManifest manifest, IEnumerable<string> candidates)
    {
        // Try to match a candidate that corresponds to a non-trial, 64-bit launcher first
        var list = candidates.ToList();
        foreach (var ln in manifest.Launchers)
        {
            var resolved = EAGameRegistryUtil.ResolveBracketOrAbsolute(ln.FilePath, baseInstallDir: null, onlyExisting: true);
            if (!string.IsNullOrEmpty(resolved) && list.Contains(resolved, StringComparer.OrdinalIgnoreCase))
            {
                var archOk = !ln.Requires64BitOS || (OsArch == 64);
                var trialOk = !ln.Trial;
                if (archOk && trialOk)
                    return resolved;
            }
        }

        // Next: any non-trial
        foreach (var ln in manifest.Launchers.Where(l => !l.Trial))
        {
            var resolved = EAGameRegistryUtil.ResolveBracketOrAbsolute(ln.FilePath, baseInstallDir: null, onlyExisting: true);
            if (!string.IsNullOrEmpty(resolved) && list.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                return resolved;
        }

        // Fallback: first candidate
        return list.FirstOrDefault();
    }

    public static string TrimBracketPrefix(string? value, out string bracket)
    {
        bracket = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string s = value.Trim();

        if (s.Length > 1 && s[0] == '[')
        {
            int close = s.IndexOf(']');
            if (close > 0)
            {
                bracket = s.Substring(1, close - 1).Trim();  // content inside [   ]
                string tail = s.Substring(close + 1).Trim().Trim('"');
                return tail;
            }
        }

        return s;
    }

    private static EAGame AddExecutables(ILauncher launcher, EAGame game)
    {
        if (!launcher.LauncherOptions.SearchExecutables)
            return game;

        var scanned = PathUtil.GetExecutables(game.InstallDir);
        var merged = new List<string>();
        if (game.Executables != null) merged.AddRange(game.Executables);
        merged.AddRange(scanned);

        game.Executables = merged.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Keep current primary executable; if empty, set to first scanned
        if (string.IsNullOrEmpty(game.Executable))
        {
            game.Executable = game.Executables.FirstOrDefault() ?? string.Empty;
            game.WorkingDir = string.IsNullOrEmpty(game.Executable) ? string.Empty
                              : Path.GetDirectoryName(game.Executable) ?? string.Empty;
        }

        return game;
    }

    public static List<string> GetInstallerDataXmlPaths(bool onlyExisting = true)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null) continue;

                    foreach (var subName in SafeGetSubKeyNames(uninstall))
                    {
                        try
                        {
                            using var appKey = uninstall.OpenSubKey(subName);
                            if (appKey is null) continue;

                            var installLocation = appKey.GetValue("InstallLocation") as string;
                            if (string.IsNullOrWhiteSpace(installLocation)) continue;

                            installLocation = NormalizePath(installLocation);
                            if (string.IsNullOrWhiteSpace(installLocation)) continue;

                            var installerXml = Path.Combine(installLocation, @"__Installer\installerdata.xml");
                            if (!onlyExisting || File.Exists(installerXml))
                                results.Add(installerXml);
                        }
                        catch { /* skip subkey */ }
                    }
                }
                catch { /* skip hive/view */ }
            }

        return new List<string>(results);
    }

    private static string[] SafeGetSubKeyNames(RegistryKey key)
    {
        try { return key.GetSubKeyNames(); }
        catch { return Array.Empty<string>(); }
    }

    private static string NormalizePath(string path)
    {
        try { path = Environment.ExpandEnvironmentVariables(path ?? string.Empty); } catch { }
        return path.Trim().Trim('"');
    }

    private static string GetOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "PCWIN";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "LINUX";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "MAC";
        return string.Empty;
    }

    private static uint GetOsArch() => (uint)(RuntimeInformation.OSArchitecture == Architecture.X64 ? 64 : 32);
}