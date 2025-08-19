using Gamelib.Core.Util;
using GameLib.Core;
using GameLib.Plugin.EA.Model;
using Microsoft.Win32;
using Newtonsoft.Json;
using System.Runtime.InteropServices;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace GameLib.Plugin.EA;

internal static class EAGameFactory
{
    private static readonly string Os = GetOs();
    private static readonly uint OsArch = GetOsArch();

    /// <summary>
    /// Get games installed for the Origin launcher
    /// </summary>
    public static IEnumerable<EAGame> GetGames(ILauncher launcher, CancellationToken cancellationToken = default)
    {
        return GetInstallerDataXmlPaths()
            .AsParallel()
            .WithCancellation(cancellationToken)
            .Select(manifestFile => DeserializeManifest(manifestFile))
            .Where(game => game is not null)
            .Select(game => AddLauncherId(launcher, game!))
            .ToList();
    }

    /// <summary>
    /// Add launcher ID to Game
    /// </summary>
    private static EAGame AddLauncherId(ILauncher launcher, EAGame game)
    {
        game.LauncherId = launcher.Id;
        return game;
    }

    /// <summary>
    /// Enumerate "<InstallLocation>\__Installer\installerdata.xml" from all Uninstall keys.
    /// </summary>
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
                            {
                                results.Add(installerXml);
                            }
                        }
                        catch
                        {
                            // skip problematic subkey
                        }
                    }
                }
                catch
                {
                    // skip inaccessible hive/view
                }
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

    /// <summary>
    /// Deserialize the Origin Game manifest file into a <see cref="EAGame"/> object
    /// </summary>
    private static EAGame? DeserializeManifest(string manifestFile)
    {
        if (string.IsNullOrWhiteSpace(manifestFile)) throw new ArgumentNullException(nameof(manifestFile));
        if (!File.Exists(manifestFile)) throw new FileNotFoundException("installerdata.xml not found", manifestFile);

        XDocument xdoc = XDocument.Load(manifestFile);
        XElement root = xdoc.Root ?? throw new InvalidDataException("Missing root element");

        EAGameManifest manifest = new EAGameManifest(root);

        EAGame game = new EAGame()
        {
            Id = manifest.ContentIds.FirstOrDefault(),
            InstallDir = PathUtil.Sanitize(Directory.GetParent(manifestFile)?.Parent?.FullName) ?? string.Empty,
            Locale = manifest.Locales.FirstOrDefault() ?? string.Empty,
        };

        if (string.IsNullOrEmpty(game.Id) || string.IsNullOrEmpty(game.InstallDir))
        {
            return null;
        }

        game.LaunchString = $"origin2://game/launch?offerIds={game.Id}";
        game.InstallDate = PathUtil.GetCreationTime(game.InstallDir) ?? DateTime.MinValue;
        game.Name = manifest.GameTitles.Values.FirstOrDefault() ?? string.Empty;
        game.Executables = manifest.Launchers.Select(path => Path.Combine(game.InstallDir, TrimBracketPrefix(path.FilePath)));
        // game.Executable = game.Executables.FirstOrDefault() ?? string.Empty;

        return game;
    }

    public static string TrimBracketPrefix(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value.Trim();

        if (value.Length > 1 && value[0] == '[')
        {
            int close = value.IndexOf(']');
            if (close >= 0)
                return value.Substring(close + 1).Trim().Trim('"');
        }

        return value;
    }

    /// <summary>
    /// Query manifest JSON string from the Origin API URL
    /// </summary>
    public static string GetManifestFromUrl(string gameId, TimeSpan? queryTimeout = null)
    {
        using var client = new HttpClient();
        if (queryTimeout is not null)
        {
            client.Timeout = queryTimeout.Value;
        }

        var url = $"https://api1.origin.com/ecommerce2/public/{gameId}/en_US";
        using var webRequest = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = client.Send(webRequest);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("Response is not OK", null, response.StatusCode);
        }

        using var reader = new StreamReader(response.Content.ReadAsStream());

        return reader.ReadToEnd();
    }

    /// <summary>
    /// Return the OS as a valid Origin string (as per manifest)
    /// </summary>
    private static string GetOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "PCWIN";
        }

        // TODO: haven't seen a Origin Linux game yet, this line might need to be adjusted
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "LINUX";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "MAC";
        }

        return string.Empty;
    }

    /// <summary>
    /// Returns the OS architecture as an integer
    /// </summary>
    private static uint GetOsArch() => (uint)(RuntimeInformation.OSArchitecture == Architecture.X64 ? 64 : 32);
}