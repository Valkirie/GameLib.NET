using System.Globalization;
using System.Xml.Linq;
using System.Xml.Serialization;

namespace GameLib.Plugin.EA.Model;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "Only used for deserialization")]
[XmlRoot("game")]
internal class EAGameManifest
{
    public string? Version { get; set; }
    public Dictionary<string, bool> BuildFeatureFlags { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? GameVersion { get; set; }
    public RequirementsInfo Requirements { get; set; } = new();
    public List<string> ContentIds { get; set; } = new();
    public Dictionary<string, string> GameTitles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? UninstallPath { get; set; }
    public List<Launcher> Launchers { get; set; } = new();
    public List<Chunk> Chunks { get; set; } = new();
    public List<string> Locales { get; set; } = new();
    public Dictionary<string, List<string>> LocaleIncludes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<EulaItem>> Eulas { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public TouchupInfo Touchup { get; set; } = new();
    public string? InstallManifestPath { get; set; }

    public EAGameManifest(XElement root)
    {
        Version = Attr(root, "version");
        BuildFeatureFlags = ParseFeatureFlags(root.Element("buildMetaData")?.Element("featureFlags"));
        GameVersion = root.Element("buildMetaData")?.Element("gameVersion")?.Attribute("version")?.Value;
        Requirements = new RequirementsInfo
        {
            OsMinVersion = root.Element("buildMetaData")?.Element("requirements")?.Attribute("osMinVersion")?.Value,
            OsReqs64Bit = ToBool(root.Element("buildMetaData")?.Element("requirements")?.Attribute("osReqs64Bit")?.Value)
        };
        ContentIds = root.Element("contentIDs")?.Elements("contentID").Select(e => e.Value.Trim()).Where(s => s.Length > 0).ToList()
                      ?? new List<string>();
        GameTitles = root.Element("gameTitles")?.Elements("gameTitle")
                         .GroupBy(e => Attr(e, "locale") ?? "")
                         .ToDictionary(g => g.Key, g => g.Last().Value.Trim())
                         ?? new Dictionary<string, string>();
        UninstallPath = root.Element("uninstall")?.Element("path")?.Value?.Trim();
        Launchers = ParseLaunchers(root.Element("runtime"));
        Chunks = ParseChunks(root.Element("installMetaData")?.Element("progressive"));
        Locales = (root.Element("installMetaData")?.Element("locales")?.Value ?? "")
                  .Split(new[] { ',', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                  .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        LocaleIncludes = ParseLocaleIncludes(root.Element("installMetaData")?.Element("localeFilters"));
        Eulas = ParseEulas(root.Element("installMetaData"));
        Touchup = ParseTouchup(root.Element("touchup"));
        InstallManifestPath = root.Element("installManifest")?.Element("filePath")?.Value?.Trim();
    }

    // ----- Parsing helpers -----

    private static Dictionary<string, bool> ParseFeatureFlags(XElement? ff)
    {
        var dict = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (ff == null) return dict;
        foreach (var a in ff.Attributes())
        {
            dict[a.Name.LocalName] = ToBool(a.Value);
        }
        return dict;
    }

    private static List<Launcher> ParseLaunchers(XElement? runtime)
    {
        var list = new List<Launcher>();
        if (runtime == null) return list;

        foreach (var ln in runtime.Elements("launcher"))
        {
            var names = ln.Elements("name")
                          .Where(n => n.Attribute("locale") != null)
                          .ToDictionary(n => n.Attribute("locale")!.Value, n => (n.Value ?? "").Trim(),
                                        StringComparer.OrdinalIgnoreCase);

            list.Add(new Launcher
            {
                Uid = Attr(ln, "uid"),
                Names = names,
                FilePath = ln.Element("filePath")?.Value?.Trim(),
                Parameters = ln.Element("parameters")?.Value?.Trim(),
                ExecuteElevated = ToBool(ln.Element("executeElevated")?.Value),
                Requires64BitOS = ToBool(ln.Element("requires64BitOS")?.Value),
                Trial = ToBool(ln.Element("trial")?.Value)
            });
        }
        return list;
    }

    private static List<Chunk> ParseChunks(XElement? progressive)
    {
        var list = new List<Chunk>();
        if (progressive == null) return list;

        foreach (var ch in progressive.Elements("chunk"))
        {
            var includes = ch.Elements("include").Select(e => (e.Value ?? "").Trim())
                             .Where(s => s.Length > 0).ToList();

            list.Add(new Chunk
            {
                Index = ToInt(Attr(ch, "index")),
                Name = Attr(ch, "name"),
                RequiredTag = Attr(ch, "required"), // "required" | "recommended" etc.
                Includes = includes
            });
        }
        return list;
    }

    private static Dictionary<string, List<string>> ParseLocaleIncludes(XElement? localeFilters)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (localeFilters == null) return map;

        foreach (var inc in localeFilters.Elements("includes"))
        {
            var locale = Attr(inc, "locale") ?? "";
            var files = inc.Elements("include").Select(e => (e.Value ?? "").Trim())
                           .Where(s => s.Length > 0).ToList();
            map[locale] = files;
        }
        return map;
    }

    private static Dictionary<string, List<EulaItem>> ParseEulas(XElement? installMetaData)
    {
        var map = new Dictionary<string, List<EulaItem>>(StringComparer.OrdinalIgnoreCase);
        if (installMetaData == null) return map;

        foreach (var eulas in installMetaData.Elements("eulas"))
        {
            var locale = Attr(eulas, "locale") ?? "";
            var items = new List<EulaItem>();
            foreach (var e in eulas.Elements("eula"))
            {
                items.Add(new EulaItem
                {
                    Path = (e.Value ?? "").Trim(),
                    Name = Attr(e, "name"),
                    Flag = Attr(e, "flag"),
                    InstallName = Attr(e, "installName"),
                    InstalledSize = ToLong(Attr(e, "installedSize")),
                    ToolTip = Attr(e, "toolTip")
                });
            }
            map[locale] = items;
        }
        return map;
    }

    private static TouchupInfo ParseTouchup(XElement? touchup)
    {
        if (touchup == null) return new TouchupInfo();
        return new TouchupInfo
        {
            FilePath = touchup.Element("filePath")?.Value?.Trim(),
            Parameters = touchup.Element("parameters")?.Value?.Trim(),
            UpdateParameters = touchup.Element("updateParameters")?.Value?.Trim(),
            RepairParameters = touchup.Element("repairParameters")?.Value?.Trim(),
        };
    }

    // ----- Utility -----
    private static string? Attr(XElement? e, string name) => e?.Attribute(name)?.Value;

    private static bool ToBool(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s == "1") return true;
        if (s == "0") return false;
        if (bool.TryParse(s, out var b)) return b;
        // Accept "True"/"False" with varied casing
        return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int ToInt(string? s)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static long? ToLong(string? s)
        => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public sealed class RequirementsInfo
    {
        public string? OsMinVersion { get; set; }
        public bool OsReqs64Bit { get; set; }
    }

    public sealed class Launcher
    {
        public string? Uid { get; set; }
        public Dictionary<string, string> Names { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? FilePath { get; set; }           // may be a registry-expression like [HKLM\...\Install Dir]Game.exe
        public string? Parameters { get; set; }
        public bool ExecuteElevated { get; set; }
        public bool Requires64BitOS { get; set; }
        public bool Trial { get; set; }
    }

    public sealed class Chunk
    {
        public int Index { get; set; }
        public string? Name { get; set; }
        public string? RequiredTag { get; set; }        // "required" / "recommended"
        public List<string> Includes { get; set; } = new();
    }

    public sealed class EulaItem
    {
        public string? Name { get; set; }
        public string? Flag { get; set; }               // e.g. "-Req:vc2015"
        public string? InstallName { get; set; }
        public long? InstalledSize { get; set; }
        public string? ToolTip { get; set; }
        public string? Path { get; set; }               // relative path inside game dir
    }

    public sealed class TouchupInfo
    {
        public string? FilePath { get; set; }
        public string? Parameters { get; set; }
        public string? UpdateParameters { get; set; }
        public string? RepairParameters { get; set; }
    }
}