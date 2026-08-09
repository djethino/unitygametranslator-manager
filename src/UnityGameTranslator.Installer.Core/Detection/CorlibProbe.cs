using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace UnityGameTranslator.Installer.Core.Detection;

/// <summary>
/// Tells whether a Mono game's runtime library still holds what mod loaders need.
///
/// Some games ship a stripped mscorlib: Unity's managed stripping removes members nothing in the
/// game calls, and takes with it members every loader calls. The failure is spectacular and
/// deeply misleading — the loader dies inside its own preloader with a MissingMethodException,
/// which reads as "this mod is broken" rather than "this game cannot host any mod".
///
/// A real case, verified by comparing two games: one ships a 2.5 MB mscorlib without
/// Module.GetPEKind and no loader starts; another ships 4.4 MB with it and all of them work.
/// Every route was tried on the broken one — BepInEx 5, BepInEx 6, MelonLoader, and swapping in
/// unstripped corlibs — and all of them failed.
///
/// So the size is not the test; the missing members are. This reads the metadata without loading
/// or running anything, and asks for the two members whose absence was actually observed to
/// break loaders.
/// </summary>
public static class CorlibProbe
{
    /// <summary>What a check found, and what it means for the user.</summary>
    public readonly record struct Result(bool IsStripped, string? MissingMember)
    {
        public static Result Fine => new(false, null);
    }

    private sealed record RequiredMember(string Namespace, string Type, string Member, string NeededBy);

    /// <summary>
    /// Members whose absence was observed to stop a loader dead. Deliberately short: each entry
    /// is a real failure someone hit, not a guess about what might matter.
    /// </summary>
    private static readonly RequiredMember[] Required =
    {
        // BepInEx 5 and 6 both die here, in PreloaderPreMain.
        new("System.Reflection", "Module", "GetPEKind", "BepInEx"),

        // MelonLoader gets further, then fails reading this attribute field.
        new("System.Runtime.InteropServices", "DllImportAttribute", "CharSet", "MelonLoader"),
    };

    /// <summary>
    /// Checks the game's mscorlib. Returns "fine" whenever the answer cannot be established —
    /// an unreadable file is not evidence of stripping, and refusing to install on a guess would
    /// be worse than the problem.
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

            foreach (var required in Required)
            {
                if (!HasMember(reader, required)) return new Result(true, $"{required.Type}.{required.Member}");
            }
        }
        catch
        {
            // Encrypted, packed or simply unreadable: say nothing rather than something wrong.
            return Result.Fine;
        }

        return Result.Fine;
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

            // The type exists but the member does not: that is the stripped case.
            return false;
        }

        // The type itself is absent — stripping again, and just as fatal.
        return false;
    }

    /// <summary>Which loader each required member belongs to, for the explanation shown.</summary>
    public static string NeededBy(string missingMember) =>
        Required.FirstOrDefault(r => $"{r.Type}.{r.Member}" == missingMember)?.NeededBy ?? "mod loaders";
}
