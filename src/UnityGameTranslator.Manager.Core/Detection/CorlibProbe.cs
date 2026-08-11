using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// Tells whether a Mono game's runtime library still holds what mod loaders need.
///
/// Some games ship a stripped mscorlib: Unity's managed stripping removes members nothing in the
/// game calls, and takes with it members loaders call. The failure is spectacular and deeply
/// misleading — the loader dies inside its own preloader with a MissingMethodException, which
/// reads as "this mod is broken" rather than "this game cannot host this loader".
///
/// A verified case: one game ships a 2.5 MB mscorlib without Module.GetPEKind and no loader
/// starts; another ships 4.4 MB with it and all of them work. So the size is not the test; the
/// missing members are, and they are read from the metadata without loading or running anything.
///
/// Every entry is checked rather than the first miss, so the report can name what is missing and
/// for which loader. That detail is explanatory ONLY: a corlib missing something this
/// fundamental is evidence of aggressive stripping, and the field verdict on such a game was
/// that every loader executing managed code and using reflection fails — BepInEx 5, BepInEx 6
/// and MelonLoader were each tried and each failed. So one miss refuses the game.
///
/// A per-family pass was attempted and reverted: matching MelonLoader's failure to one specific
/// metadata member was an inference from its error message, that member turned out to be present
/// on the game in question, and the game was declared installable. It is not. Refusing on the
/// evidence we can read beats passing on an inference we cannot.
/// </summary>
public static class CorlibProbe
{
    private sealed record RequiredMember(
        string Family, string Namespace, string Type, string Member, string Symptom);

    /// <summary>
    /// Members whose absence was observed to stop a loader dead, one entry per real failure.
    ///
    /// Deliberately short and empirical: every line here is something someone actually hit, not a
    /// guess about what might matter. Adding speculative entries would refuse games that work.
    /// When a new stripped game turns up, the failure it produces is what belongs here — the
    /// missing symbol is in the loader's own exception message.
    /// </summary>
    private static readonly RequiredMember[] Required =
    {
        // BepInEx 5 and 6 both die on this, inside PreloaderPreMain.
        new("bepinex", "System.Reflection", "Module", "GetPEKind",
            "MissingMethodException: Module.GetPEKind"),

        // MelonLoader gets further — its console opens — then fails reading this attribute field.
        new("melonloader", "System.Runtime.InteropServices", "DllImportAttribute", "CharSet",
            "CustomAttributeFormatException: could not find a field named CharSet"),
    };

    /// <summary>A loader family that cannot start here, and why.</summary>
    public readonly record struct BrokenFamily(string Family, string MissingMember, string Symptom);

    public readonly record struct Result(IReadOnlyList<BrokenFamily> Broken)
    {
        /// <summary>
        /// Any miss is a refusal. Not a per-family judgement: see the type comment — passing a
        /// game because only one of the two markers was missing let an install through on a game
        /// where every loader had been tried and every loader had failed.
        /// </summary>
        public bool IsStripped => Broken.Count > 0;

        public static Result Fine => new(Array.Empty<BrokenFamily>());
    }

    /// <summary>
    /// Checks the game's mscorlib for every member we know a loader needs.
    ///
    /// Reports "fine" whenever the answer cannot be established — an unreadable file is not
    /// evidence of stripping, and refusing on a guess would be worse than the problem.
    /// </summary>
    public static Result Check(string? dataDirectory)
    {
        if (dataDirectory is null) return Result.Fine;

        var corlib = Path.Combine(dataDirectory, "Managed", "mscorlib.dll");
        if (!File.Exists(corlib)) return Result.Fine;

        try
        {
            using var stream = File.OpenRead(corlib);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return Result.Fine;

            var reader = pe.GetMetadataReader();
            var broken = new List<BrokenFamily>();

            // Every entry is checked, not just the first miss: stopping early would name one
            // loader and leave the reader assuming the others are fine.
            foreach (var required in Required)
            {
                if (!HasMember(reader, required))
                    broken.Add(new BrokenFamily(required.Family,
                                                $"{required.Type}.{required.Member}",
                                                required.Symptom));
            }

            return new Result(broken);
        }
        catch
        {
            return Result.Fine;
        }
    }

    private static bool HasMember(MetadataReader reader, RequiredMember required)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);

            if (!reader.StringComparer.Equals(type.Name, required.Type)) continue;
            if (!reader.StringComparer.Equals(type.Namespace, required.Namespace)) continue;

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (reader.StringComparer.Equals(method.Name, required.Member)) return true;
            }

            // Attribute members such as DllImportAttribute.CharSet are fields, not methods.
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if (reader.StringComparer.Equals(field.Name, required.Member)) return true;
            }

            // The type is present but the member is not: that is the stripped case.
            return false;
        }

        // The type itself is absent — stripping again, and just as fatal.
        return false;
    }

    public static string DisplayName(string family) => family switch
    {
        "bepinex" => "BepInEx",
        "melonloader" => "MelonLoader",
        _ => family,
    };

    /// <summary>Plain sentence naming what is missing, and which loader is known to need it.</summary>
    public static string Describe(IReadOnlyList<BrokenFamily> broken)
    {
        if (broken.Count == 0) return "";

        var parts = broken.Select(b => $"{b.MissingMember}, which {DisplayName(b.Family)} calls");
        return string.Join("; ", parts);
    }
}
