using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace UnityGameTranslator.Manager.Core.Detection;

/// <summary>
/// One reference an assembly makes to something in ANOTHER assembly: a type, or a member of one.
/// </summary>
/// <param name="Assembly">The assembly the reference names, by simple name ("netstandard").</param>
/// <param name="Type">Full name, nested types joined with '+' ("System.Net.Http.HttpClient").</param>
/// <param name="Member">A method or field name (".ctor", "get_Count"), or null for the type itself.</param>
public sealed record TypeUse(string Assembly, string Type, string? Member = null);

/// <summary>A type named from inside an assembly: in that same assembly when Assembly is null.</summary>
public sealed record TypeName(string? Assembly, string FullName);

/// <summary>What one type holds that a reference can ask for: its member names, and what it derives from.</summary>
public sealed record TypeShape(IReadOnlySet<string> Members, TypeName? Base);

/// <summary>
/// What an assembly offers and what it asks of others, read out of its metadata and nothing else.
///
/// ⚠ **A plain record, so the rules over it can be checked without a file.** Reading is
/// <see cref="Read"/>; everything that decides — which library a game lacks, which copy may replace
/// it — works on these values, and the checks build them by hand.
///
/// ⚠ **Names, never signatures.** A member is found by name on its type or on a type it derives
/// from. That is what tells a stripped library from a complete one — stripping removes whole
/// members — and it is what the field failures were (a type gone, a method gone). An overload whose
/// parameters changed between two Unity versions is not caught, and nothing claims it is.
/// </summary>
public sealed class AssemblyShape
{
    public AssemblyShape(string name,
                         IReadOnlyDictionary<string, TypeShape> types,
                         IReadOnlyDictionary<string, string>? forwards = null,
                         IReadOnlyCollection<string>? nativeImports = null,
                         IReadOnlyList<TypeUse>? uses = null,
                         IReadOnlyCollection<string>? internalCalls = null)
    {
        Name = name;
        Types = types;
        Forwards = forwards ?? new Dictionary<string, string>(StringComparer.Ordinal);
        NativeImports = nativeImports ?? Array.Empty<string>();
        Uses = uses ?? Array.Empty<TypeUse>();
        InternalCalls = internalCalls ?? Array.Empty<string>();
    }

    /// <summary>Simple name, as references name it ("System.Net.Http").</summary>
    public string Name { get; }

    /// <summary>Every type defined here, by full name, nested types included.</summary>
    public IReadOnlyDictionary<string, TypeShape> Types { get; }

    /// <summary>Types this assembly hands on to another one (type forwarders), full name → assembly.</summary>
    public IReadOnlyDictionary<string, string> Forwards { get; }

    /// <summary>The native libraries its P/Invokes name ("kernel32.dll", "System.Native").</summary>
    public IReadOnlyCollection<string> NativeImports { get; }

    /// <summary>What it asks of other assemblies — the list a missing library would break.</summary>
    public IReadOnlyList<TypeUse> Uses { get; }

    /// <summary>
    /// Its methods implemented inside the engine rather than in IL, as "Type::Method" (nested types
    /// joined with '+') — the calls a Unity module makes into the native player, which that player
    /// must carry under the same name (see <see cref="EngineModules"/>).
    /// </summary>
    public IReadOnlyCollection<string> InternalCalls { get; }

    /// <summary>
    /// Reads an assembly's shape from disk.
    ///
    /// ⚠ **Read into memory, then released.** A PEReader over the file would hold it open while
    /// anything kept a reference to it — and these are a game's own files, which Steam must be able
    /// to replace in an update. The bytes are read once and the file is let go immediately.
    /// </summary>
    public static AssemblyShape Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var pe = new PEReader(ImmutableArray.Create(bytes));

        if (!pe.HasMetadata)
            throw new BadImageFormatException($"{Path.GetFileName(path)} carries no .NET metadata.");

        var r = pe.GetMetadataReader();
        var name = r.IsAssembly ? r.GetString(r.GetAssemblyDefinition().Name) : Path.GetFileNameWithoutExtension(path);

        var types = new Dictionary<string, TypeShape>(StringComparer.Ordinal);
        var internalCalls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in r.TypeDefinitions)
        {
            var definition = r.GetTypeDefinition(handle);
            var typeName = DefinitionName(r, handle);

            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in definition.GetMethods())
            {
                var methodDefinition = r.GetMethodDefinition(method);
                var methodName = r.GetString(methodDefinition.Name);
                members.Add(methodName);

                if ((methodDefinition.ImplAttributes & System.Reflection.MethodImplAttributes.InternalCall) != 0)
                    internalCalls.Add(typeName + "::" + methodName);
            }

            foreach (var field in definition.GetFields()) members.Add(r.GetString(r.GetFieldDefinition(field).Name));

            types[typeName] = new TypeShape(members, NameOf(r, definition.BaseType));
        }

        var forwards = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handle in r.ExportedTypes)
        {
            if (ForwardOf(r, handle) is { } forward) forwards[forward.FullName] = forward.Assembly!;
        }

        var natives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moduleRefs = r.GetTableRowCount(TableIndex.ModuleRef);
        for (var row = 1; row <= moduleRefs; row++)
        {
            natives.Add(r.GetString(r.GetModuleReference(MetadataTokens.ModuleReferenceHandle(row)).Name));
        }

        var uses = new HashSet<TypeUse>();
        foreach (var handle in r.TypeReferences)
        {
            if (ReferenceName(r, handle) is { Assembly: { } assembly } type)
                uses.Add(new TypeUse(assembly, type.FullName));
        }

        foreach (var handle in r.MemberReferences)
        {
            var reference = r.GetMemberReference(handle);

            // ⚠ The generic case is the common one: a call on Dictionary<string, int> is parented to
            // a TYPE SPECIFICATION of Dictionary`2, not to Dictionary`2 itself. Skipping those
            // would check every member except the ones on the collections this mod uses most.
            var owner = reference.Parent.Kind switch
            {
                HandleKind.TypeReference => ReferenceName(r, (TypeReferenceHandle)reference.Parent),
                HandleKind.TypeSpecification => NameOf(r, reference.Parent),
                _ => null,
            };

            if (owner is { Assembly: { } assembly })
                uses.Add(new TypeUse(assembly, owner.FullName, r.GetString(reference.Name)));
        }

        return new AssemblyShape(name, types, forwards, natives, uses.ToList(), internalCalls);
    }

    /// <summary>A type definition's full name, nested types joined to their parent with '+'.</summary>
    private static string DefinitionName(MetadataReader r, TypeDefinitionHandle handle)
    {
        var definition = r.GetTypeDefinition(handle);
        var own = r.GetString(definition.Name);

        var parent = definition.GetDeclaringType();
        if (!parent.IsNil) return DefinitionName(r, parent) + "+" + own;

        var ns = r.GetString(definition.Namespace);
        return ns.Length > 0 ? ns + "." + own : own;
    }

    /// <summary>
    /// A type reference's full name and the assembly it lives in; null Assembly when it names
    /// something in this very module.
    /// </summary>
    private static TypeName ReferenceName(MetadataReader r, TypeReferenceHandle handle)
    {
        var reference = r.GetTypeReference(handle);
        var own = r.GetString(reference.Name);
        var scope = reference.ResolutionScope;

        switch (scope.Kind)
        {
            case HandleKind.TypeReference:
                var outer = ReferenceName(r, (TypeReferenceHandle)scope);
                return new TypeName(outer.Assembly, outer.FullName + "+" + own);

            case HandleKind.AssemblyReference:
                var ns = r.GetString(reference.Namespace);
                return new TypeName(r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
                                    ns.Length > 0 ? ns + "." + own : own);

            default:
                var local = r.GetString(reference.Namespace);
                return new TypeName(null, local.Length > 0 ? local + "." + own : own);
        }
    }

    /// <summary>
    /// The type an entity names — a definition, a reference, or the generic type behind an
    /// instantiation. Null for anything else (arrays, pointers, a missing base).
    /// </summary>
    private static TypeName? NameOf(MetadataReader r, EntityHandle handle)
    {
        if (handle.IsNil) return null;

        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                return new TypeName(null, DefinitionName(r, (TypeDefinitionHandle)handle));

            case HandleKind.TypeReference:
                return ReferenceName(r, (TypeReferenceHandle)handle);

            case HandleKind.TypeSpecification:
                var blob = r.GetBlobReader(r.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;

                // ReadSignatureTypeCode folds CLASS and VALUETYPE into TypeHandle.
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.TypeHandle) return null;

                var generic = blob.ReadTypeHandle();
                return generic.Kind == HandleKind.TypeSpecification ? null : NameOf(r, generic);

            default:
                return null;
        }
    }

    /// <summary>A forwarder's full name and target assembly; nested ones follow their outer type.</summary>
    private static TypeName? ForwardOf(MetadataReader r, ExportedTypeHandle handle)
    {
        var exported = r.GetExportedType(handle);
        var own = r.GetString(exported.Name);

        switch (exported.Implementation.Kind)
        {
            case HandleKind.AssemblyReference:
                var ns = r.GetString(exported.Namespace);
                return new TypeName(
                    r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation).Name),
                    ns.Length > 0 ? ns + "." + own : own);

            case HandleKind.ExportedType:
                return ForwardOf(r, (ExportedTypeHandle)exported.Implementation) is { } outer
                    ? new TypeName(outer.Assembly, outer.FullName + "+" + own)
                    : null;

            default:
                // Implemented in another module of the same assembly: not a forward to anywhere.
                return null;
        }
    }
}
