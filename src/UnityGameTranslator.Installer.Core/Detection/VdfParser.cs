using System.Text;

namespace UnityGameTranslator.Installer.Core.Detection;

/// <summary>
/// A node in a Valve KeyValues document: either a leaf holding a string, or a block of children.
/// </summary>
public sealed class VdfNode
{
    public string? Value { get; init; }

    public Dictionary<string, VdfNode> Children { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public bool IsLeaf => Value is not null;

    public VdfNode? this[string key] =>
        Children.TryGetValue(key, out var child) ? child : null;

    /// <summary>Reads a leaf value at a path, e.g. Get("AppState", "installdir").</summary>
    public string? GetString(params string[] path)
    {
        VdfNode? node = this;
        foreach (var key in path)
        {
            node = node?[key];
            if (node is null) return null;
        }
        return node.Value;
    }
}

/// <summary>
/// Minimal reader for Valve KeyValues (.vdf / .acf).
///
/// Written here rather than pulled in as a dependency: we read exactly two shapes of file
/// (libraryfolders.vdf and appmanifest_*.acf), the grammar is four tokens wide, and owning it
/// means owning its failure modes — a malformed manifest must be skipped, never crash a scan.
/// </summary>
public static class VdfParser
{
    public static VdfNode? ParseFile(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }
        catch
        {
            // A single unreadable manifest must not abort the whole library scan.
            return null;
        }
    }

    public static VdfNode? Parse(string text)
    {
        var pos = 0;
        var root = new VdfNode();
        try
        {
            ParseInto(text, ref pos, root, depth: 0);
            return root;
        }
        catch
        {
            return null;
        }
    }

    private const int MaxDepth = 64;

    private static void ParseInto(string text, ref int pos, VdfNode parent, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("VDF nesting too deep");

        while (true)
        {
            SkipTrivia(text, ref pos);
            if (pos >= text.Length) return;

            if (text[pos] == '}')
            {
                pos++;
                return;
            }

            var key = ReadToken(text, ref pos);
            if (key is null) return;

            SkipTrivia(text, ref pos);
            if (pos >= text.Length) return;

            if (text[pos] == '{')
            {
                pos++;
                var block = new VdfNode();
                ParseInto(text, ref pos, block, depth + 1);
                // Duplicate keys: last one wins, which matches Steam's own behaviour.
                parent.Children[key] = block;
            }
            else
            {
                var value = ReadToken(text, ref pos);
                if (value is null) return;
                parent.Children[key] = new VdfNode { Value = value };

                // Conditional suffix such as [$WIN32] — read and drop it.
                SkipSpacesOnly(text, ref pos);
                if (pos < text.Length && text[pos] == '[')
                {
                    while (pos < text.Length && text[pos] != ']') pos++;
                    if (pos < text.Length) pos++;
                }
            }
        }
    }

    private static void SkipTrivia(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            var c = text[pos];
            if (char.IsWhiteSpace(c)) { pos++; continue; }

            if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }
            return;
        }
    }

    private static void SkipSpacesOnly(string text, ref int pos)
    {
        while (pos < text.Length && (text[pos] == ' ' || text[pos] == '\t')) pos++;
    }

    private static string? ReadToken(string text, ref int pos)
    {
        SkipTrivia(text, ref pos);
        if (pos >= text.Length) return null;

        if (text[pos] == '"')
        {
            pos++;
            var sb = new StringBuilder();
            while (pos < text.Length && text[pos] != '"')
            {
                if (text[pos] == '\\' && pos + 1 < text.Length)
                {
                    pos++;
                    sb.Append(text[pos] switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        var other => other,
                    });
                }
                else
                {
                    sb.Append(text[pos]);
                }
                pos++;
            }
            if (pos < text.Length) pos++; // closing quote
            return sb.ToString();
        }

        // Unquoted token: run until whitespace or a structural character.
        var start = pos;
        while (pos < text.Length && !char.IsWhiteSpace(text[pos])
               && text[pos] != '{' && text[pos] != '}' && text[pos] != '"')
        {
            pos++;
        }
        return pos > start ? text[start..pos] : null;
    }
}
