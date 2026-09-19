using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Model;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// <see cref="LocalTranslationProbe.Read"/> remembers what it read, and must never answer with a
/// file that has since moved.
///
/// 🔴 A sequence, not a rule: the answer is right or wrong depending on WHEN it is asked. The mod
/// rewrites the translation while somebody plays and the ancestor at every sync; a memory keyed on
/// the wrong thing would go on showing the line count and the "changed since last sync" of a file
/// that no longer says that — compiling, passing every pure check, and wrong on screen.
/// </summary>
internal static class ProbeMemoryChecks
{
    internal static void WhatARememberedReadAnswers()
    {
        Program.Section("A translation read, remembered — and forgotten when its files move");

        var descriptor = new LoaderDescriptor { Id = "bepinex5", UserDataDir = "BepInEx/plugins/UnityGameTranslator" };
        var gamePath = Path.Combine(Path.GetTempPath(), "ugt-probe-memory-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(gamePath, "BepInEx", "plugins", "UnityGameTranslator");
        var file = Path.Combine(folder, LocalTranslationProbe.TranslationFileName);
        var ancestor = Path.Combine(folder, LocalTranslationProbe.AncestorFileName);

        try
        {
            Directory.CreateDirectory(folder);

            const string two = "{\"_uuid\":\"u\",\"a\":{\"v\":\"A\",\"t\":\"A\"},\"b\":{\"v\":\"B\",\"t\":\"A\"}}";
            const string three = "{\"_uuid\":\"u\",\"a\":{\"v\":\"A\",\"t\":\"A\"},\"b\":{\"v\":\"B\",\"t\":\"A\"},\"c\":{\"v\":\"C\",\"t\":\"H\"}}";

            File.WriteAllText(file, two);
            File.WriteAllText(ancestor, two);

            var first = LocalTranslationProbe.Read(gamePath, descriptor);
            Program.Check(first is { EntryCount: 2, ChangedSinceAncestor: 0 },
                "a file in step with its ancestor: 2 lines, 0 changed",
                "the starting point every later answer is compared with");

            var again = LocalTranslationProbe.Read(gamePath, descriptor);
            Program.Check(ReferenceEquals(first, again), "asked again untouched: the same answer, not a re-read",
                "the whole point — the list asks this for every game on every answer the site sends");

            File.WriteAllText(file, three);
            var grown = LocalTranslationProbe.Read(gamePath, descriptor);
            Program.Check(grown is { EntryCount: 3, ChangedSinceAncestor: 1 },
                "the mod writes a line: 3 lines, 1 changed",
                "a translation rewritten while somebody plays must be read again");

            File.WriteAllText(ancestor, three);
            var synced = LocalTranslationProbe.Read(gamePath, descriptor);
            Program.Check(synced is { EntryCount: 3, ChangedSinceAncestor: 0 },
                "only the ancestor moves (a sync): 0 changed",
                "keyed on the translation alone, this would still claim unpublished work");

            File.Delete(ancestor);
            var orphan = LocalTranslationProbe.Read(gamePath, descriptor);
            Program.Check(orphan is { EntryCount: 3, ChangedSinceAncestor: null },
                "the ancestor is gone: changed is unknown, never 0",
                "0 is a claim nothing was changed, and acting on it offers to overwrite work");

            File.Delete(file);
            Program.Check(LocalTranslationProbe.Read(gamePath, descriptor) is null,
                "the translation is gone: no translation",
                "a remembered answer about a deleted file would offer to sync a file that is not there");
        }
        finally
        {
            try { Directory.Delete(gamePath, recursive: true); } catch { /* a temp folder left behind proves nothing */ }
        }
    }
}
