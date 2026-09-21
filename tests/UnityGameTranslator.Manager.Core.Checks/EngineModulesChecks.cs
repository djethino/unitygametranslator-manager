using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using UnityGameTranslator.Manager.Core.Detection;
using UnityGameTranslator.Manager.Core.Install;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// Unity's engine modules: telling a stripped one from an older one, holding a donor to the game,
/// and reading Unity's own package.
///
/// ⚠ **Measured before these cases were written** (2026-09-21, 42 games): engine names missing from
/// the modules — zero on the 40 unstripped games from 2017.3 to 6000.5, about four thousand on the
/// two stripped ones; the mod's references unresolved on every 2017–2019 game WITHOUT stripping
/// (newer APIs), which is why the second alone decides nothing. On two stripped games the real
/// sources came out as expected: the same release (an editor, another game) usable, a newer patch
/// refused on the player's missing functions, an older one refused on `InvokePanicFunction` — and
/// Unity's download read 2 to 3 MB of a 173 to 525 MB package and passed every check.
/// </summary>
internal static class EngineModulesChecks
{
    private static TypeShape Type(params string[] members) => new(new HashSet<string>(members, StringComparer.Ordinal), null);

    private static AssemblyShape Module(string name, Dictionary<string, TypeShape> types, string[]? internalCalls = null) =>
        new(name, types, internalCalls: internalCalls);

    public static void WhatTheEngineTells()
    {
        Program.Section("Engine modules: what the player binary tells");

        var binary = Encoding.ASCII.GetBytes("\0junk::\0UnityEngine.Application::get_productName\0"
                                             + "UnityEngine.Canvas/Inner::Tick\0std::vector\0::nothing\01abc::x\0");
        var names = EngineModules.ReadNativeNames(binary);
        Program.Check(names.Contains("UnityEngine.Application::get_productName") && names.Contains("UnityEngine.Canvas+Inner::Tick"),
            "names read around '::', nested types joined with '+'", "the player writes '/', metadata '+'");
        Program.Check(!names.Any(n => n.StartsWith("::", StringComparison.Ordinal) || n.StartsWith('1')),
            "a name must start like a type", "'::nothing' and '1abc::x' are not names");

        var complete = Module("UnityEngine.CoreModule", new()
        {
            ["UnityEngine.Application"] = Type("get_productName", "get_companyName"),
        });
        var stripped = Module("UnityEngine.CoreModule", new()
        {
            ["UnityEngine.Application"] = Type("get_companyName"),
        });
        var player = new HashSet<string> { "UnityEngine.Application::get_productName", "UnityEngine.Application::get_companyName", "Other.Type::X" };

        Program.Check(EngineModules.Stripped(_ => complete, new[] { "UnityEngine.CoreModule" }, player).Count == 0,
            "a module built with its player lacks nothing it names", "zero on 40 unstripped games");
        Program.Check(EngineModules.Stripped(_ => stripped, new[] { "UnityEngine.CoreModule" }, player)
                          .TryGetValue("UnityEngine.CoreModule", out var lacking) && lacking == 1,
            "a stripped module lacks what the player still names", "Application.productName, measured on a stripped game");

        var donor = Module("UnityEngine.CoreModule", new Dictionary<string, TypeShape>(complete.Types),
                           new[] { "UnityEngine.Application::get_productName", "UnityEngine.Jobs::New", "UnityEngine.Old::Dead" });
        var donorPlayer = new HashSet<string>(player) { "UnityEngine.Jobs::New" };
        Program.Check(EngineModules.MissingFromPlayer(new[] { donor }, player, donorPlayer).SequenceEqual(new[] { "UnityEngine.Jobs::New" }),
            "a native call the donor's engine has and the game's lacks is found", "a newer donor crashed a game at start on exactly this");
        Program.Check(!EngineModules.MissingFromPlayer(new[] { donor }, player, donorPlayer).Contains("UnityEngine.Old::Dead"),
            "...but not one neither engine registers", "every intact 2021.3 game declares two such calls of its own");
        Program.Check(EngineModules.MissingFromPlayer(new[] { donor }, player, null).Count == 2,
            "without the donor's engine, every absent call counts", "stricter, never looser");

        var gameOwn = Module("UnityEngine.CoreModule", new()
        {
            ["UnityEngine.Application"] = Type("get_productName"),
            ["Unity.Jobs.JobsUtility"] = Type("InvokePanicFunction"),
            ["<PrivateImplementationDetails>"] = Type("x"),
        });
        var olderDonor = Module("UnityEngine.CoreModule", new()
        {
            ["UnityEngine.Application"] = Type("get_productName"),
            ["Unity.Jobs.JobsUtility"] = Type(),
        });
        Program.Check(EngineModules.Lacks(gameOwn, olderDonor).SequenceEqual(new[] { "Unity.Jobs.JobsUtility.InvokePanicFunction" }),
            "what the game's stripped copy kept, the donor must have",
            "a callback the player makes by name, caught without its list");

        Program.Check(EngineModules.IsEngineModule("UnityEngine") && EngineModules.IsEngineModule("UnityEngine.CoreModule")
                      && !EngineModules.IsEngineModule("UnityEngine.UI") && !EngineModules.IsEngineModule("Unity.TextMeshPro"),
            "engine modules only, never the packages a project compiles", "UnityEngine.UI is the game's own build");
    }

    public static void WhichReleasesFit()
    {
        Program.Section("Engine modules: which releases fit");

        var game = UnityVersions.Parse("2022.3.62f2")!;
        Program.Check(UnityVersions.SameRelease(UnityVersions.Parse("2022.3.62f3")!, game),
            "f2 and f3 of one patch: the same release", "measured: a game ran with the other's modules");
        Program.Check(UnityVersions.OlderInBranch(UnityVersions.Parse("2022.3.40f1")!, game)
                      && !UnityVersions.OlderInBranch(UnityVersions.Parse("2022.3.63f1")!, game)
                      && !UnityVersions.OlderInBranch(UnityVersions.Parse("2021.3.40f1")!, game),
            "older in the branch; never newer, never another branch", "a newer patch crashed a game at start");
        Program.Check(RuntimeLibraries.ArchiveName("2021.2.0a10") == "2021.2.0a10" && RuntimeLibraries.ArchiveName("2018.4.36f1") == "2018.4.36"
                      && RuntimeLibraries.ArchiveName("nonsense") is null,
            "one reader of versions for both batches", "the archive files alphas under their suffix");
    }

    public static void WhatUnitysIndexSays()
    {
        Program.Section("Engine modules: Unity's list of downloads");

        const string index = "[Unity]\nurl=LinuxEditorInstaller/Unity.tar.xz\n[Windows-Mono]\ntitle=Windows Build Support (Mono)\n"
                             + "url=MacEditorTargetInstaller/UnitySetup-Windows-Mono.pkg\nmd5=x\nsize=550692869\n[Other]\nurl=y\n";
        Program.Check(UnityBuildIndex.Section(index, "Windows-Mono") is { Url: "MacEditorTargetInstaller/UnitySetup-Windows-Mono.pkg", Size: 550692869 },
            "the package and its size, from its own section", "");
        Program.Check(UnityBuildIndex.Section("[Windows-Mono]\nurl=https://elsewhere.example/x.pkg\n", "Windows-Mono") is null
                      && UnityBuildIndex.Section("[Windows-Mono]\nurl=../../x.pkg\n", "Windows-Mono") is null,
            "an address that leaves Unity's folder is refused", "the index can never send a download elsewhere");
    }

    /// <summary>A package built here to Unity's layout, so the reader is held to its rules on bytes.</summary>
    public static void HowUnitysPackageIsRead()
    {
        Program.Section("Engine modules: reading Unity's package");

        var destination = Path.Combine(Path.GetTempPath(), "ugt-unity-package-" + Guid.NewGuid().ToString("N"));

        try
        {
            var package = Package(new (string, byte[])[]
            {
                ("./Variations", Array.Empty<byte>()),
                ("./Variations/mono/../../../ugt-escape/UnityEngine.CoreModule.dll", Encoding.ASCII.GetBytes("escape")),
                ("./Variations/mono/Managed/UnityEngine.CoreModule.dll", Encoding.ASCII.GetBytes("core")),
                ("./Variations/mono/Managed/UnityEngine.CoreModule.pdb", Encoding.ASCII.GetBytes("symbols")),
                ("./Variations/mono/Managed/UnityEngine.dll", Encoding.ASCII.GetBytes("facade")),
                // What follows the modules in a real package — hundreds of megabytes of players.
                // Incompressible, so reading through it would show in what was consumed.
                ("./Variations/win64_player_development_mono/UnityPlayer.dll", RandomNumberGenerator(4 << 20)),
            });

            var read = new CountingStream(new MemoryStream(package));
            var names = UnityPackage.ExtractEngineModules(read, destination);

            Program.Check(names.OrderBy(n => n).SequenceEqual(new[] { "UnityEngine", "UnityEngine.CoreModule" }),
                "the engine modules of the first module folder, and only them", "symbols and a path with '..' are read past");
            Program.Check(File.ReadAllText(Path.Combine(destination, "UnityEngine.CoreModule.dll")) == "core"
                          && Directory.GetFiles(destination).Length == 2
                          && !Directory.Exists(Path.Combine(Path.GetTempPath(), "ugt-escape")),
                "written by file name alone, inside the destination", "nothing in the archive decides where a file goes");
            Program.Check(read.Consumed < 1 << 20,
                "reading stops once the modules have gone by", $"read {read.Consumed / 1024} KB of {package.Length / 1024} KB");
        }
        finally
        {
            try { Directory.Delete(destination, recursive: true); } catch { /* a temp folder left behind proves nothing */ }
        }
    }

    // ── Building a package the way Unity's is built ─────────────────────────────────────────

    private static byte[] RandomNumberGenerator(int length) =>
        System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);

    private static byte[] Package((string Name, byte[] Content)[] entries)
    {
        var cpio = new MemoryStream();
        foreach (var (name, content) in entries.Append(("TRAILER!!!", Array.Empty<byte>())))
        {
            var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
            var header = "070707" + new string('0', 6 * 7) + new string('0', 11)
                       + Convert.ToString(nameBytes.Length, 8).PadLeft(6, '0')
                       + Convert.ToString(content.Length, 8).PadLeft(11, '0');
            cpio.Write(Encoding.ASCII.GetBytes(header));
            cpio.Write(nameBytes);
            cpio.Write(content);
        }

        var payload = new MemoryStream();
        using (var gzip = new GZipStream(payload, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(cpio.ToArray());
        // Padding after the payload: the rest of a real package, which must never be read.
        var tail = new byte[1 << 20];

        var toc = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\"?><xar><toc><file id=\"1\"><name>TargetSupport.pkg.tmp</name>"
            + $"<file id=\"2\"><data><length>{payload.Length}</length><offset>0</offset><size>{payload.Length}</size>"
            + "<encoding style=\"application/octet-stream\"/></data><name>Payload</name></file></file></toc></xar>");
        var compressedToc = new MemoryStream();
        using (var zlib = new ZLibStream(compressedToc, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(toc);

        var header28 = new byte[28];
        Encoding.ASCII.GetBytes("xar!").CopyTo(header28, 0);
        BinaryPrimitives.WriteUInt16BigEndian(header28.AsSpan(4), 28);
        BinaryPrimitives.WriteUInt16BigEndian(header28.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt64BigEndian(header28.AsSpan(8), (ulong)compressedToc.Length);
        BinaryPrimitives.WriteUInt64BigEndian(header28.AsSpan(16), (ulong)toc.Length);

        var package = new MemoryStream();
        package.Write(header28);
        package.Write(compressedToc.ToArray());
        package.Write(payload.ToArray());
        package.Write(tail);
        return package.ToArray();
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Consumed { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Consumed += read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Consumed; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
