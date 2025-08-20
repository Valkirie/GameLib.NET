using Gamelib.Core.Util;
using Microsoft.Win32;

namespace GameLib.Plugin.EA.Model
{
    public static class EAGameRegistryUtil
    {
        /// <summary>     
        /// Removes a leading "[...]" prefix and returns the remaining tail.
        /// Also returns the bracket content via <paramref name="bracket"/>
        /// </summary>
        public static string TrimBracketPrefix(string? value, out string bracket)
        {
            bracket = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var s = value.Trim();
            if (s.Length > 1 && s[0] == '[')
            {
                var close = s.IndexOf(']');
                if (close > 0)
                {
                    bracket = s.Substring(1, close - 1).Trim();
                    return s.Substring(close + 1).Trim().Trim('"');
                }
            }
            return s;
        }

        /// <summary>
        /// Parse bracket content like "HKLM\SOFTWARE\Vendor\App\Install Dir"
        /// → hive, keyPath (without value), valueName ("" = default).
        /// Accepts long hive names too.
        /// </summary>
        public static bool TryParseBracket(string? bracketContent,
                                           out RegistryHive hive,
                                           out string keyPath,
                                           out string valueName)
        {
            hive = default;
            keyPath = valueName = string.Empty;
            if (string.IsNullOrWhiteSpace(bracketContent)) return false;

            var s = bracketContent.Trim().Trim('\\');
            var firstSlash = s.IndexOf('\\');
            if (firstSlash < 0) return false;

            var hiveToken = s[..firstSlash];
            if (!RegistryUtil.TryMapHive(hiveToken, out hive)) return false;

            var rest = s[(firstSlash + 1)..];
            var lastSlash = rest.LastIndexOf('\\');

            if (lastSlash >= 0)
            {
                keyPath = rest[..lastSlash];
                valueName = rest[(lastSlash + 1)..];
            }
            else
            {
                keyPath = rest;
                valueName = ""; // default value
            }

            if (valueName is "@" or "(Default)") valueName = "";
            return true;
        }

        /// <summary>
        /// Returns the RegistryKey for the key referenced by bracket content (value is ignored).
        /// Uses GetKey (tries 32/64-bit views).
        /// </summary>
        public static RegistryKey? GetKeyFromBracket(string? bracketContent)
        {
            if (!TryParseBracket(bracketContent, out var hive, out var keyPath, out _))
                return null;
            return RegistryUtil.GetKey(hive, keyPath);
        }

        /// <summary>
        /// Reads the string value referenced by bracket content. Returns null if not found.
        /// </summary>
        public static string? GetValueFromBracket(string? bracketContent)
        {
            if (!TryParseBracket(bracketContent, out var hive, out var keyPath, out var valueName))
                return null;
            var val = RegistryUtil.GetValue(hive, keyPath, string.IsNullOrEmpty(valueName) ? null : valueName);
            // optional env expansion (REG_EXPAND_SZ or plain %VAR% patterns)
            try { return string.IsNullOrEmpty(val) ? val : Environment.ExpandEnvironmentVariables(val); }
            catch { return val; }
        }

        /// <summary>
        /// Full resolution: if input is "[HIVE\Key\Value]tail", read the registry value and combine with tail.
        /// If not bracketed: absolute → return; relative → combine with baseInstallDir (if provided).
        /// Optionally strips args when the registry value is a full command line (e.g., UninstallString).
        /// </summary>
        public static string? ResolveBracketOrAbsolute(string? value, string? baseInstallDir = null, bool onlyExisting = false, bool stripArgs = true)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var s = value.Trim();
            if (s.StartsWith("["))
            {
                _ = TrimBracketPrefix(s, out var bracket);
                var test = GetKeyFromBracket(bracket);
                var baseFromReg = GetValueFromBracket(bracket);
                if (string.IsNullOrWhiteSpace(baseFromReg)) return null;

                if (stripArgs)
                    baseFromReg = PathUtil.RemoveArgsFromExecutable(baseFromReg);

                var tail = TrimBracketPrefix(s, out _); // get tail again (without brackets)
                var combined = string.IsNullOrEmpty(tail) ? baseFromReg! : Path.Combine(baseFromReg!, tail);
                combined = Normalize(combined);

                if (onlyExisting && !(File.Exists(combined) || Directory.Exists(combined)))
                    return null;

                return combined;
            }

            // Not bracketed
            if (Path.IsPathRooted(s))
            {
                s = Normalize(s);
                if (stripArgs) s = PathUtil.RemoveArgsFromExecutable(s);
                return onlyExisting ? (File.Exists(s) || Directory.Exists(s) ? s : null) : s;
            }

            if (!string.IsNullOrWhiteSpace(baseInstallDir))
            {
                var combined = Normalize(Path.Combine(baseInstallDir!, s));
                return onlyExisting ? (File.Exists(combined) || Directory.Exists(combined) ? combined : null) : combined;
            }

            return s;
        }

        /// <summary>
        /// Read another value (e.g. "Locale") from the same key referenced by the bracket content.
        /// Example bracket: HKEY_LOCAL_MACHINE\SOFTWARE\Respawn\Jedi Survivor\Install Dir
        /// </summary>
        public static string? GetSiblingValueFromBracket(string? bracketContent, string siblingValueName)
        {
            if (!TryParseBracket(bracketContent, out var hive, out var keyPath, out _))
                return null;
            return RegistryUtil.GetValue(hive, keyPath, siblingValueName, null);
        }

        /// <summary>
        /// Convenience: try to read install dir and locale from the same key.
        /// Will probe common install value names if the bracket points at a different value.
        /// </summary>
        public static (string? installDir, string? locale) TryGetInstallDirAndLocale(string? bracketContent)
        {
            if (!TryParseBracket(bracketContent, out var hive, out var keyPath, out var valueName))
                return (null, null);

            // install dir: prefer the exact value referenced by the bracket, otherwise probe common names
            string? install = RegistryUtil.GetValue(hive, keyPath, string.IsNullOrEmpty(valueName) ? "Install Dir" : valueName);
            if (!string.IsNullOrWhiteSpace(install))
            {
                try { install = Environment.ExpandEnvironmentVariables(install).Trim().Trim('"'); } catch { }
            }

            // locale / language
            string? locale = RegistryUtil.GetValue(hive, keyPath, "Locale");
            locale ??= RegistryUtil.GetValue(hive, keyPath, "Language");

            return (string.IsNullOrWhiteSpace(install) ? null : install,
                    string.IsNullOrWhiteSpace(locale) ? null : locale);
        }

        private static string Normalize(string path)
            => path.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
    }
}
