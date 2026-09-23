namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// Every dropdown in the window is the program's own SearchPicker — never Avalonia's ComboBox.
///
/// 🔴 **The rule existed and was broken anyway** (2026-09-21: two ComboBoxes slipped into the source
/// choice, and the user found the wheel did not scroll them — « on a créé les nôtres, il ne faut plus
/// jamais utiliser l'ancien, l'UX doit être la même partout »). The ComboBox's dropdown ignores the
/// mouse wheel on Windows (see PopupWheel); SearchPicker carries the wheel, the search field and the
/// sizing every other list here has. A rule in a comment is obeyed by whoever reads it; this one is
/// read by the build.
///
/// ⚠ Lexical, like PluginWriteChecks: the window cannot be built here, its source can be read.
/// Comments are skipped, since several explain why a ComboBox is NOT used.
/// </summary>
internal static class DropdownChecks
{
    public static void OnlyTheProgramsOwnDropdown()
    {
        Program.Section("Dropdowns: the program's own, everywhere");

        var gui = FindDirectory("src", "UnityGameTranslator.Manager.Gui");
        Program.Check(gui is not null, "the window's sources are found", "this check reads them; without them, it proves nothing");
        if (gui is null) return;

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(gui, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal))
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("<!--", StringComparison.Ordinal)) continue;

                if (code.Contains("new ComboBox", StringComparison.Ordinal)
                    || code.Contains("<ComboBox", StringComparison.Ordinal)
                    || code.Contains("(ComboBox ", StringComparison.Ordinal)
                    || code.Contains(" ComboBox ", StringComparison.Ordinal))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{number}");
                }
            }
        }

        Program.Check(offenders.Count == 0,
            "no ComboBox in the window: every dropdown is a SearchPicker",
            offenders.Count == 0 ? "the wheel, the search field and the sizing are the same in every list"
                                 : "found at " + string.Join(", ", offenders));
    }

    /// <summary>
    /// 🔴 The list refill must never hand the list a NEW row template (Manager issue #1, 2026-09-23).
    /// Replacing a template clears every row shown, and a cleared row is drawn once more with no
    /// content: the default row read it as an item and the process died without a word, on every
    /// short list opened a second time. Lexical, like the check above.
    /// </summary>
    public static void RowsAreNeverRebuiltOnRefill()
    {
        Program.Section("Dropdowns: a refill never replaces the row template");

        var gui = FindDirectory("src", "UnityGameTranslator.Manager.Gui");
        var file = gui is null ? null : Path.Combine(gui, "SearchPicker.cs");
        Program.Check(file is not null && File.Exists(file), "SearchPicker's source is found", "this check reads it");
        if (file is null || !File.Exists(file)) return;

        var source = File.ReadAllText(file);
        var start = source.IndexOf("private void Refill()", StringComparison.Ordinal);
        var end = start < 0 ? -1 : source.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        var refill = start < 0 ? "" : end < 0 ? source[start..] : source[start..end];

        Program.Check(refill.Length > 0 && !refill.Contains("new FuncDataTemplate", StringComparison.Ordinal),
            "Refill builds no template", "a new template on every refill cleared the rows and crashed the window");
        Program.Check(source.Contains("item is null ? \"\"", StringComparison.Ordinal),
            "the default row draws nothing for an empty content", "Avalonia draws a recycled row with no content");
    }

    private static string? FindDirectory(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
