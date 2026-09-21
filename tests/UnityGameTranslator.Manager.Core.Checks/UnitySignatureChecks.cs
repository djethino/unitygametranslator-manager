using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using UnityGameTranslator.Manager.Core.Install;

namespace UnityGameTranslator.Manager.Core.Checks;

/// <summary>
/// Whether a file is taken for one Unity signed — the gate every engine module passes before it is
/// copied into a game from anywhere on this machine.
///
/// ⚠ **Measured against Windows before these cases were written** (2026-09-21): every DLL of 42
/// installed games, the Unity editors' modules and thirty .NET runtime files — 8 309 files — got the
/// same answer from <see cref="UnitySignature"/> as from WinVerifyTrust (3 453 signed by Unity, 108
/// by another publisher, 4 747 unsigned, one altered on purpose). Three differences had to be fixed
/// on the way, each now a comment in the code: old Symantec timestamps signing a bare digest, the
/// timestamp authority's intermediates carried inside the token, and tokens .NET's own decoder
/// refuses.
///
/// ⚠ **What cannot be held here is a genuine Unity signature**: Unity's binaries are theirs and do
/// not belong in this repository. So the cases build their own signed files, and the one that
/// matters most is a forgery — a certificate that NAMES Unity, chained to nothing.
/// </summary>
internal static class UnitySignatureChecks
{
    public static void WhatCountsAsSignedByUnity()
    {
        Program.Section("Unity signature: what counts as signed by Unity");

        using var fakeUnity = SelfSigned("CN=Unity Technologies ApS, O=Unity Technologies ApS, C=DK");
        var plain = File.ReadAllBytes(typeof(UnitySignatureChecks).Assembly.Location);

        Program.Check(UnitySignature.Check(plain).Verdict == UnitySignature.Verdict.NotSigned,
            "an unsigned assembly", "no certificate table: NotSigned");

        var forged = Sign(plain, fakeUnity);
        var forgedResult = UnitySignature.Check(forged);
        Program.Check(forgedResult.Verdict == UnitySignature.Verdict.Untrusted,
            "a certificate that names Unity, chained to nothing", $"refused as Untrusted, got {forgedResult.Verdict} ({forgedResult.Detail})");

        // The same file, one byte changed after signing — the digest recomputed over the image
        // must no longer be the signed one, whoever signed it.
        var altered = (byte[])forged.Clone();
        altered[altered.Length / 3] ^= 0x01;
        Program.Check(UnitySignature.Check(altered).Verdict == UnitySignature.Verdict.Tampered,
            "a signed file altered afterwards", "the recomputed image digest differs: Tampered");

        // The checksum field is outside the image digest by definition: rewriting it must not
        // break a signature, or every file a tool re-checksums would read as altered.
        var rechecksummed = (byte[])forged.Clone();
        var checksum = ChecksumOffset(rechecksummed);
        rechecksummed[checksum] ^= 0xFF;
        Program.Check(UnitySignature.Check(rechecksummed).Verdict == UnitySignature.Verdict.Untrusted,
            "the PE checksum rewritten", "not part of the digest: still the forgery's verdict");

        var truncatedTable = (byte[])forged.Clone();
        var entry = CertificateEntryOffset(truncatedTable);
        BinaryPrimitives.WriteInt32LittleEndian(truncatedTable.AsSpan(entry + 4), truncatedTable.Length);
        Program.Check(UnitySignature.Check(truncatedTable).Verdict == UnitySignature.Verdict.Unreadable,
            "a certificate table pointing past the end", "Unreadable, never an exception");

        Program.Check(UnitySignature.Check(new byte[] { 1, 2, 3, 4, 5 }).Verdict == UnitySignature.Verdict.Unreadable,
            "five bytes of nothing", "not a PE file: Unreadable");

        var runtime = File.ReadAllBytes(typeof(object).Assembly.Location);
        var runtimeResult = UnitySignature.Check(runtime);
        Program.Check(runtimeResult.Verdict is UnitySignature.Verdict.OtherPublisher or UnitySignature.Verdict.NotSigned,
            ".NET's own runtime library", $"never Unity's, got {runtimeResult.Verdict} ({runtimeResult.Signer})");
    }

    public static void WhoIsUnity()
    {
        Program.Section("Unity signature: whose name counts");

        foreach (var (subject, expected, why) in new[]
        {
            ("CN=Unity Technologies ApS, O=Unity Technologies ApS, C=DK", true, "the Danish company, 2020.3 to 2022.3"),
            ("CN=Unity Technologies Aps, O=Unity Technologies Aps, C=DK", true, "the same, spelt as older certificates spell it"),
            ("CN=Unity Technologies SF, O=Unity Technologies SF, C=US", true, "the San Francisco company, Unity 6"),
            ("CN=Unity Technologies ApS, O=Somebody Else, C=DK", false, "the name in CN alone proves nothing"),
            ("CN=Unity, O=Unity Technologies ApS Holdings, C=DK", false, "a longer organisation is another one"),
        })
        {
            using var certificate = SelfSigned(subject);
            Program.Check(UnitySignature.IsUnity(certificate) == expected, subject, why);
        }
    }

    // ── Building signed files ────────────────────────────────────────────────────────────────

    private static X509Certificate2 SelfSigned(string subject)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, critical: false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>
    /// An Authenticode signature built the way signing tools build one: the image digest, wrapped in
    /// SpcIndirectDataContent, signed as PKCS#7, appended as a WIN_CERTIFICATE the data directory
    /// points at. Written independently of the code under test, from the PE/COFF specification.
    /// </summary>
    private static byte[] Sign(byte[] image, X509Certificate2 signer)
    {
        // The table goes at the end, 8-byte aligned, as the specification requires.
        var padded = new byte[(image.Length + 7) & ~7];
        image.CopyTo(padded, 0);

        var checksum = ChecksumOffset(padded);
        var entry = CertificateEntryOffset(padded);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(padded, 0, checksum);
        sha.AppendData(padded, checksum + 4, entry - (checksum + 4));
        sha.AppendData(padded, entry + 8, padded.Length - (entry + 8));
        var digest = sha.GetHashAndReset();

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("1.3.6.1.4.1.311.2.1.15"); // SPC_PE_IMAGE_DATAOBJ
            }

            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
                    writer.WriteNull();
                }

                writer.WriteOctetString(digest);
            }
        }

        var cms = new SignedCms(new ContentInfo(new Oid("1.3.6.1.4.1.311.2.1.4"), writer.Encode()), detached: false);
        cms.ComputeSignature(new CmsSigner(signer) { IncludeOption = X509IncludeOption.EndCertOnly });
        var pkcs7 = cms.Encode();

        var length = 8 + pkcs7.Length;
        var table = new byte[(length + 7) & ~7];
        BinaryPrimitives.WriteInt32LittleEndian(table, length);
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(4), 0x0200);
        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(6), 0x0002);
        pkcs7.CopyTo(table, 8);

        var signed = new byte[padded.Length + table.Length];
        padded.CopyTo(signed, 0);
        table.CopyTo(signed, padded.Length);
        BinaryPrimitives.WriteInt32LittleEndian(signed.AsSpan(entry), padded.Length);
        BinaryPrimitives.WriteInt32LittleEndian(signed.AsSpan(entry + 4), table.Length);
        return signed;
    }

    private static int OptionalHeader(byte[] file) => BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x3C)) + 24;

    private static int ChecksumOffset(byte[] file) => OptionalHeader(file) + 64;

    private static int CertificateEntryOffset(byte[] file)
    {
        var optional = OptionalHeader(file);
        var directories = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(optional)) == 0x20B ? optional + 112 : optional + 96;
        return directories + 4 * 8;
    }
}
