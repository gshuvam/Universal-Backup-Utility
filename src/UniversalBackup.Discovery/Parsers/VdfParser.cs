using System.Text;

namespace UniversalBackup.Discovery.Parsers;

/// <summary>
/// Represents a parsed Valve Data Format (Key-Values) table/object node.
/// </summary>
public sealed class VdfTable
{
    public string Name { get; set; }
    public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<VdfTable> SubTables { get; } = [];

    public VdfTable(string name)
    {
        Name = name;
    }

    public string? GetString(string key, string? defaultValue = null)
    {
        return Properties.TryGetValue(key, out var val) ? val : defaultValue;
    }

    public long GetInt64(string key, long defaultValue = 0)
    {
        return Properties.TryGetValue(key, out var val) && long.TryParse(val, out var parsed)
            ? parsed
            : defaultValue;
    }

    public VdfTable? GetTable(string name)
    {
        return SubTables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<VdfTable> GetTables(string? name = null)
    {
        return name == null
            ? SubTables
            : SubTables.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Lightweight, zero-dependency parser for Valve Data Format (VDF/ACF).
/// </summary>
public static class VdfParser
{
    public static VdfTable Parse(string vdfText)
    {
        ArgumentNullException.ThrowIfNull(vdfText);

        var tokens = Tokenize(vdfText);
        int index = 0;

        if (tokens.Count == 0)
        {
            return new VdfTable("root");
        }

        // If root has a named object: "AppState" { ... } or "libraryfolders" { ... }
        string rootName = tokens[index];
        index++;

        if (index < tokens.Count && tokens[index] == "{")
        {
            index++; // consume "{"
            var rootTable = new VdfTable(rootName);
            ParseTableBody(tokens, ref index, rootTable);
            return rootTable;
        }

        // Otherwise create an anonymous root table
        index = 0;
        var anonRoot = new VdfTable("root");
        ParseTableBody(tokens, ref index, anonRoot);
        return anonRoot;
    }

    private static void ParseTableBody(List<string> tokens, ref int index, VdfTable parent)
    {
        while (index < tokens.Count)
        {
            var token = tokens[index];

            if (token == "}")
            {
                index++; // consume closing brace
                return;
            }

            string key = token;
            index++;

            if (index >= tokens.Count)
            {
                break;
            }

            var nextToken = tokens[index];

            if (nextToken == "{")
            {
                index++; // consume "{"
                var childTable = new VdfTable(key);
                ParseTableBody(tokens, ref index, childTable);
                parent.SubTables.Add(childTable);
            }
            else if (nextToken == "}")
            {
                // Key with no value before closing brace
                parent.Properties[key] = string.Empty;
                index++;
                return;
            }
            else
            {
                // Key-value pair
                parent.Properties[key] = nextToken;
                index++;
            }
        }
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            char c = text[i];

            // Skip whitespace
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // Skip single-line comments //
            if (c == '/' && i + 1 < len && text[i + 1] == '/')
            {
                i += 2;
                while (i < len && text[i] != '\n' && text[i] != '\r')
                {
                    i++;
                }
                continue;
            }

            // Structural braces
            if (c == '{' || c == '}')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }

            // Quoted string
            if (c == '"')
            {
                i++; // skip opening quote
                var sb = new StringBuilder();
                while (i < len)
                {
                    char qc = text[i];
                    if (qc == '\\' && i + 1 < len)
                    {
                        char next = text[i + 1];
                        if (next == '"' || next == '\\')
                        {
                            sb.Append(next);
                            i += 2;
                            continue;
                        }
                        if (next == 'n')
                        {
                            sb.Append('\n');
                            i += 2;
                            continue;
                        }
                        if (next == 't')
                        {
                            sb.Append('\t');
                            i += 2;
                            continue;
                        }
                    }

                    if (qc == '"')
                    {
                        i++; // skip closing quote
                        break;
                    }

                    sb.Append(qc);
                    i++;
                }

                tokens.Add(sb.ToString());
                continue;
            }

            // Unquoted token
            var unquoted = new StringBuilder();
            while (i < len && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}' && text[i] != '"')
            {
                unquoted.Append(text[i]);
                i++;
            }

            if (unquoted.Length > 0)
            {
                tokens.Add(unquoted.ToString());
            }
        }

        return tokens;
    }
}
