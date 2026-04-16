using SourceUnpack.Core.Models;

namespace SourceUnpack.Core.Formats.Vmt;

/// <summary>
/// Reads Valve Material Type (VMT) files — KeyValues v1 text format.
/// Extracts texture references and material properties.
/// </summary>
public class VmtReader
{
    /// <summary>
    /// Parse a VMT file from text content and return material info.
    /// </summary>
    public static MaterialInfo Parse(string vmtContent)
    {
        var info = new MaterialInfo();
        var lines = vmtContent.Split('\n');
        int depth = 0;
        bool inProxy = false;

        foreach (var rawLine in lines)
        {
            string line = rawLine.Trim().TrimEnd('\r');

            // Strip comments
            int commentIndex = line.IndexOf("//");
            if (commentIndex >= 0)
                line = line.Substring(0, commentIndex).Trim();

            if (string.IsNullOrEmpty(line))
                continue;

            if (line == "{")
            {
                depth++;
                continue;
            }
            if (line == "}")
            {
                if (inProxy && depth <= 2) inProxy = false;
                depth--;
                continue;
            }

            // Shader name is the first non-brace token
            if (depth == 0 && string.IsNullOrEmpty(info.ShaderName))
            {
                info.ShaderName = StripQuotes(line);
                continue;
            }

            // Skip proxy blocks
            if (line.Equals("\"Proxies\"", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("Proxies", StringComparison.OrdinalIgnoreCase))
            {
                inProxy = true;
                continue;
            }
            if (inProxy) continue;

            // Parse key-value pairs
            var kv = ParseKeyValue(line);
            if (kv == null) continue;

            string key = kv.Value.Key.ToLowerInvariant();
            string value = kv.Value.Value;

            info.AllProperties[key] = value;

            switch (key)
            {
                case "$basetexture":
                    info.BaseTexture = NormalizePath(value);
                    break;
                case "$bumpmap":
                    info.BumpMap = NormalizePath(value);
                    break;
                case "$normalmap":
                    info.NormalMap = NormalizePath(value);
                    break;
                case "$envmapmask":
                    info.EnvMapMask = NormalizePath(value);
                    break;
                case "$phongexponenttexture":
                    info.PhongExponentTexture = NormalizePath(value);
                    break;
                case "$detail":
                    info.DetailTexture = NormalizePath(value);
                    break;
                case "$selfillummask":
                    info.SelfIllumMask = NormalizePath(value);
                    break;
                case "include":
                    info.Include = NormalizePath(value);
                    break;
            }
        }

        return info;
    }

    /// <summary>
    /// Parse a VMT file from raw bytes.
    /// </summary>
    public static MaterialInfo Parse(byte[] data)
    {
        string content = System.Text.Encoding.UTF8.GetString(data);
        return Parse(content);
    }

    private static KeyValuePair<string, string>? ParseKeyValue(string line)
    {
        // Handles both: "$key" "value"  and  $key value
        line = line.Trim();

        if (line.StartsWith("\""))
        {
            int endKey = line.IndexOf('"', 1);
            if (endKey < 0) return null;

            string key = line.Substring(1, endKey - 1);
            string rest = line.Substring(endKey + 1).Trim();

            if (rest.StartsWith("\""))
            {
                int endVal = rest.IndexOf('"', 1);
                if (endVal < 0) return null;
                string val = rest.Substring(1, endVal - 1);
                return new KeyValuePair<string, string>(key, val);
            }

            return new KeyValuePair<string, string>(key, rest);
        }

        // Unquoted format: $key value
        var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        return new KeyValuePair<string, string>(parts[0], StripQuotes(parts[1]));
    }

    private static string StripQuotes(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            return s[1..^1];
        return s;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').Trim().ToLowerInvariant();
    }
}
