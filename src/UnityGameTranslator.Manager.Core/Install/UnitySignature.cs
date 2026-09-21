using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace UnityGameTranslator.Manager.Core.Install;

/// <summary>
/// Whether a file carries a valid Authenticode signature by Unity — the condition for copying one
/// of Unity's engine modules from anywhere on this machine into a game.
///
/// 🔴 **Why a signature and nothing weaker.** The copies come from other games on the same computer,
/// and nothing says where those came from: a cracked game can carry a module with anything in it,
/// and putting it in a second game would spread it without the player knowing (user's requirement,
/// 2026-09-21). Unity signs the engine modules it ships; a module that still verifies is one Unity
/// built, whichever folder it sits in. A module a game's build STRIPPED is rewritten and loses the
/// signature — it is never a donor, and that is correct: it is incomplete anyway.
///
/// ⚠ **One implementation on every system**, not WinVerifyTrust here and something else on Linux:
/// the verdict on a file must not depend on where the tool runs. Read from the PE file itself:
///   1. the certificate table (data directory 4) → the first PKCS#7 `SignedData`;
///   2. its content, `SpcIndirectDataContent`, names a hash algorithm and a digest;
///   3. that digest is recomputed over the file — everything except the checksum field, the
///      certificate-table entry and the table itself (the Authenticode image hash);
///   4. the signature over that content is checked;
///   5. the signer's chain is built against the system's trusted roots, at the time a trusted
///      timestamp proves the file was signed — Unity's older certificates have long expired, and a
///      signature made while they were valid stays valid, exactly as Windows judges it;
///   6. the signer's organisation must be Unity's.
///
/// ⚠ Revocation is not checked: that needs the network at every scan, and Windows does not check it
/// for timestamped signatures either. The checks (`UnitySignatureChecks`) compare this verdict with
/// Windows' own on real files.
///
/// ⚠ **The system's roots are what they are.** Signatures from the Symantec era chain to a root that
/// Linux distributions have dropped; there they are refused. That is the safe direction, and the
/// card then offers the next source.
/// </summary>
public static class UnitySignature
{
    /// <summary>
    /// The organisations Unity signs its engine with, read on real files (2026-09-21): the Danish
    /// company on 2020.3 to 2022.3 builds (spelt "ApS" and "Aps"), the San Francisco one on Unity 6.
    /// </summary>
    public static readonly IReadOnlyList<string> Publishers = new[] { "Unity Technologies ApS", "Unity Technologies SF" };

    public enum Verdict
    {
        /// <summary>Signed by Unity, intact, with a chain to a trusted root.</summary>
        Valid,

        /// <summary>No signature at all — older Unity builds, or a module a game's build stripped.</summary>
        NotSigned,

        /// <summary>A signature is there, and the file no longer matches it.</summary>
        Tampered,

        /// <summary>Valid signature, but not by Unity.</summary>
        OtherPublisher,

        /// <summary>The signature does not lead to a trusted root on this system.</summary>
        Untrusted,

        /// <summary>The file or its signature could not be read as one.</summary>
        Unreadable,
    }

    /// <summary>What was found, and the signer when one could be read.</summary>
    public sealed record Result(Verdict Verdict, string? Signer, string Detail)
    {
        public bool IsValid => Verdict == Verdict.Valid;
    }

    private const string SpcIndirectData = "1.3.6.1.4.1.311.2.1.4";
    private const string Rfc3161Timestamp = "1.3.6.1.4.1.311.3.3.1";
    private const string CounterSignature = "1.2.840.113549.1.9.6";
    private const string CodeSigning = "1.3.6.1.5.5.7.3.3";
    private const string TimeStamping = "1.3.6.1.5.5.7.3.8";

    /// <summary>Checks one file. Never throws for what the file contains; an unreadable file is a verdict.</summary>
    public static Result Check(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Result(Verdict.Unreadable, null, e.Message);
        }

        return Check(bytes);
    }

    /// <summary>Checks a file already in memory.</summary>
    public static Result Check(byte[] file)
    {
        if (!TryLocate(file, out var layout)) return new Result(Verdict.Unreadable, null, "not a PE file");
        if (layout.CertificateSize == 0) return new Result(Verdict.NotSigned, null, "no signature");

        if (layout.CertificateOffset < 0 || layout.CertificateOffset + (long)layout.CertificateSize > file.Length)
            return new Result(Verdict.Unreadable, null, "the signature table lies outside the file");

        byte[]? pkcs7 = FirstSignedData(file, layout);
        if (pkcs7 is null) return new Result(Verdict.Unreadable, null, "no PKCS#7 signature in the table");

        var cms = new SignedCms();
        try { cms.Decode(pkcs7); }
        catch (CryptographicException e) { return new Result(Verdict.Unreadable, null, e.Message); }

        if (cms.ContentInfo.ContentType.Value != SpcIndirectData || cms.SignerInfos.Count != 1)
            return new Result(Verdict.Unreadable, null, "not an Authenticode signature");

        var signer = cms.SignerInfos[0];
        var name = signer.Certificate?.GetNameInfo(X509NameType.SimpleName, false);

        if (!TryReadDigest(cms.ContentInfo.Content, out var algorithm, out var expected))
            return new Result(Verdict.Unreadable, name, "the signed digest could not be read");

        var actual = ImageHash(file, layout, algorithm);
        if (actual is null) return new Result(Verdict.Unreadable, name, $"unsupported hash {algorithm}");
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            return new Result(Verdict.Tampered, name, "the file does not match its signature");

        try { signer.CheckSignature(verifySignatureOnly: true); }
        catch (CryptographicException) { return new Result(Verdict.Tampered, name, "the signature does not verify"); }

        if (signer.Certificate is not { } certificate)
            return new Result(Verdict.Unreadable, null, "the signer's certificate is not included");

        var signedAt = TrustedSigningTime(signer, cms.Certificates);
        if (!ChainsToTrustedRoot(certificate, cms.Certificates, signedAt ?? DateTime.UtcNow, CodeSigning))
        {
            return new Result(Verdict.Untrusted, name, signedAt is null
                ? "no trusted timestamp, and the certificate does not lead to a trusted root today"
                : $"the certificate does not lead to a trusted root on {signedAt:yyyy-MM-dd}");
        }

        return IsUnity(certificate)
            ? new Result(Verdict.Valid, name, signedAt is null ? "signed" : $"signed on {signedAt:yyyy-MM-dd}")
            : new Result(Verdict.OtherPublisher, name, $"signed by {name}, not by Unity");
    }

    /// <summary>Whether the certificate's organisation is one of <see cref="Publishers"/>.</summary>
    public static bool IsUnity(X509Certificate2 certificate)
    {
        foreach (var rdn in certificate.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            if (rdn.GetSingleElementType().Value != "2.5.4.10") continue; // O=

            var organisation = rdn.GetSingleElementValue();
            if (Publishers.Any(p => string.Equals(p, organisation, StringComparison.OrdinalIgnoreCase))) return true;
        }

        return false;
    }

    // ── The PE layout the image hash skips ────────────────────────────────────────────────────

    private readonly record struct Layout(int ChecksumOffset, int CertificateEntryOffset,
                                          long CertificateOffset, int CertificateSize);

    private static bool TryLocate(byte[] file, out Layout layout)
    {
        layout = default;
        if (file.Length < 0x40 || file[0] != 'M' || file[1] != 'Z') return false;

        var pe = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(0x3C));
        if (pe < 0 || pe + 24 > file.Length || BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pe)) != 0x00004550)
            return false;

        var optional = pe + 24;
        if (optional + 2 > file.Length) return false;

        var directories = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(optional)) switch
        {
            0x10B => optional + 96,  // PE32
            0x20B => optional + 112, // PE32+
            _ => -1,
        };
        if (directories < 0) return false;

        var entry = directories + 4 * 8; // IMAGE_DIRECTORY_ENTRY_SECURITY
        if (entry + 8 > file.Length) return false;

        layout = new Layout(optional + 64, entry,
                            BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(entry)),
                            (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(entry + 4)));
        return true;
    }

    /// <summary>The first WIN_CERTIFICATE of type PKCS_SIGNED_DATA in the certificate table.</summary>
    private static byte[]? FirstSignedData(byte[] file, Layout layout)
    {
        var position = layout.CertificateOffset;
        var end = layout.CertificateOffset + layout.CertificateSize;

        while (position + 8 <= end)
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan((int)position));
            var type = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan((int)position + 6));
            if (length < 8 || position + length > end) return null;

            if (type == 0x0002) return file.AsSpan((int)position + 8, length - 8).ToArray();

            position += (length + 7) & ~7; // entries are 8-byte aligned
        }

        return null;
    }

    /// <summary>
    /// The Authenticode image hash: the whole file except the checksum, the certificate-table entry,
    /// and the table itself.
    ///
    /// ⚠ The sequential form, as signing tools produce it — the certificate table last, sections in
    /// file order. A file laid out otherwise would hash differently here than in Windows, which the
    /// checks against Windows' verdict would show.
    /// </summary>
    private static byte[]? ImageHash(byte[] file, Layout layout, HashAlgorithmName algorithm)
    {
        using var hash = algorithm.Name switch
        {
            "SHA1" => IncrementalHash.CreateHash(HashAlgorithmName.SHA1),
            "SHA256" => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            "SHA384" => IncrementalHash.CreateHash(HashAlgorithmName.SHA384),
            "SHA512" => IncrementalHash.CreateHash(HashAlgorithmName.SHA512),
            _ => null,
        };
        if (hash is null) return null;

        var tableStart = (int)layout.CertificateOffset;
        var tableEnd = tableStart + layout.CertificateSize;

        hash.AppendData(file, 0, layout.ChecksumOffset);
        hash.AppendData(file, layout.ChecksumOffset + 4, layout.CertificateEntryOffset - (layout.ChecksumOffset + 4));
        hash.AppendData(file, layout.CertificateEntryOffset + 8, tableStart - (layout.CertificateEntryOffset + 8));
        if (tableEnd < file.Length) hash.AppendData(file, tableEnd, file.Length - tableEnd);

        return hash.GetHashAndReset();
    }

    // ── What the signature says ───────────────────────────────────────────────────────────────

    /// <summary>
    /// `SpcIndirectDataContent ::= SEQUENCE { data SpcAttributeTypeAndOptionalValue, messageDigest DigestInfo }`.
    ///
    /// ⚠ The content may arrive with or without its outer SEQUENCE header, depending on how the
    /// runtime hands back a non-data content — both are read.
    /// </summary>
    private static bool TryReadDigest(byte[] content, out HashAlgorithmName algorithm, out byte[] digest)
    {
        algorithm = default;
        digest = Array.Empty<byte>();

        try
        {
            var reader = new AsnReader(content, AsnEncodingRules.BER);
            var sequence = reader.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence) ? reader.ReadSequence() : reader;

            sequence.ReadEncodedValue(); // SpcAttributeTypeAndOptionalValue — what was signed, not how

            var digestInfo = sequence.ReadSequence();
            var algorithmIdentifier = digestInfo.ReadSequence();
            var oid = algorithmIdentifier.ReadObjectIdentifier();
            digest = digestInfo.ReadOctetString();

            algorithm = oid switch
            {
                "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
                "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
                "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
                "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
                _ => new HashAlgorithmName(oid),
            };
            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    /// <summary>
    /// When a trusted timestamp authority says the file was signed — RFC 3161 or the older
    /// Authenticode countersignature. Null when there is none, or none that verifies.
    /// </summary>
    private static DateTime? TrustedSigningTime(SignerInfo signer, X509Certificate2Collection extra)
    {
        foreach (var attribute in signer.UnsignedAttributes)
        {
            if (attribute.Oid.Value != Rfc3161Timestamp) continue;

            foreach (var value in attribute.Values)
            {
                if (Rfc3161Time(value.RawData, signer, extra) is { } time) return time;
            }
        }

        var rawCounterSigners = signer.UnsignedAttributes.OfType<CryptographicAttributeObject>()
            .Where(a => a.Oid.Value == CounterSignature)
            .SelectMany(a => a.Values.OfType<AsnEncodedData>())
            .Select(v => v.RawData)
            .ToList();

        for (var i = 0; i < signer.CounterSignerInfos.Count; i++)
        {
            var counter = signer.CounterSignerInfos[i];
            if (counter.Certificate is not { } tsa) continue;

            var verified = true;
            try { counter.CheckSignature(verifySignatureOnly: true); }
            catch (CryptographicException)
            {
                verified = i < rawCounterSigners.Count && LegacyCounterSignatureHolds(signer, counter, rawCounterSigners[i]);
            }

            if (!verified) continue;

            var time = counter.SignedAttributes.OfType<CryptographicAttributeObject>()
                .SelectMany(a => a.Values.OfType<Pkcs9SigningTime>())
                .Select(t => (DateTime?)t.SigningTime.ToUniversalTime())
                .FirstOrDefault();

            if (time is { } at && ChainsToTrustedRoot(tsa, extra, at, TimeStamping)) return at;
        }

        return null;
    }

    /// <summary>
    /// The time an RFC 3161 token proves, when it holds: its own signature verifies, the digest it
    /// timestamps is the digest of the signature it is attached to, and its authority chains to a
    /// trusted root at that time.
    ///
    /// ⚠ Checked step by step rather than through <see cref="Rfc3161TimestampToken.TryDecode"/>, which
    /// refuses well-formed tokens Windows accepts (Microsoft's own timestamp service, 2019, measured
    /// on 2026-09-21): the token decodes as a SignedData, its TSTInfo reads, its signature holds —
    /// and TryDecode still says no.
    /// </summary>
    private static DateTime? Rfc3161Time(byte[] encoded, SignerInfo signer, X509Certificate2Collection extra)
    {
        var token = new SignedCms();
        try
        {
            token.Decode(encoded);
            if (token.ContentInfo.ContentType.Value != "1.2.840.113549.1.9.16.1.4" || token.SignerInfos.Count != 1) return null;
            token.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException)
        {
            return null;
        }

        if (!Rfc3161TimestampTokenInfo.TryDecode(token.ContentInfo.Content, out var info, out _)) return null;

        var algorithm = info.HashAlgorithmId.Value switch
        {
            "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
            "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
            "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
            "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
            _ => default,
        };
        if (algorithm == default) return null;

        using var hash = IncrementalHash.CreateHash(algorithm);
        hash.AppendData(signer.GetSignature());
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), info.GetMessageHash().Span)) return null;

        if (token.SignerInfos[0].Certificate is not { } tsa) return null;

        // ⚠ The authority's intermediates travel inside the token, not beside the signature.
        var intermediates = new X509Certificate2Collection(extra);
        intermediates.AddRange(token.Certificates);

        var time = info.Timestamp.UtcDateTime;
        return ChainsToTrustedRoot(tsa, intermediates, time, TimeStamping) ? time : null;
    }

    /// <summary>
    /// A countersignature from the older timestamp services, which RSA-sign the raw digest of their
    /// attributes without the DigestInfo wrapper PKCS#1 v1.5 puts around it.
    ///
    /// ⚠ Measured, not guessed (2026-09-21): Symantec's "Time Stamping Services Signer - G4", on
    /// Unity builds from 2017 to 2019. Windows accepts these; .NET's own check refuses them. Everything
    /// else is held to the same standard: the countersigned digest must be the digest of the
    /// signature it vouches for, and the RSA padding must be exact.
    /// </summary>
    private static bool LegacyCounterSignatureHolds(SignerInfo signer, SignerInfo counter, byte[] raw)
    {
        if (counter.Certificate?.GetRSAPublicKey() is not { } key) return false;

        byte[] attributes, signature;
        string digestOid;
        try
        {
            // SignerInfo ::= SEQUENCE { version, sid, digestAlgorithm, [0] signedAttrs, signatureAlgorithm, signature, ... }
            var info = new AsnReader(raw, AsnEncodingRules.BER).ReadSequence();
            info.ReadInteger();
            info.ReadEncodedValue();
            digestOid = info.ReadSequence().ReadObjectIdentifier();
            attributes = info.ReadEncodedValue().ToArray();
            info.ReadEncodedValue();
            signature = info.ReadOctetString();
        }
        catch (AsnContentException)
        {
            return false;
        }

        HashAlgorithmName algorithm;
        switch (digestOid)
        {
            case "1.3.14.3.2.26": algorithm = HashAlgorithmName.SHA1; break;
            case "2.16.840.1.101.3.4.2.1": algorithm = HashAlgorithmName.SHA256; break;
            default: return false;
        }

        // What the countersignature vouches for: the digest of the signature it timestamps.
        var vouched = counter.SignedAttributes.OfType<CryptographicAttributeObject>()
            .SelectMany(a => a.Values.OfType<Pkcs9MessageDigest>())
            .Select(d => d.MessageDigest)
            .FirstOrDefault();
        if (vouched is null || !CryptographicOperations.FixedTimeEquals(vouched, Hash(algorithm, signer.GetSignature())))
            return false;

        // The signed attributes are hashed as a SET, not under their [0] context tag.
        if (attributes.Length == 0 || attributes[0] != 0xA0) return false;
        attributes[0] = 0x31;
        var digest = Hash(algorithm, attributes);

        if (key.VerifyHash(digest, signature, algorithm, RSASignaturePadding.Pkcs1)) return true;

        // The raw form: 00 01 FF..FF 00 <digest>, recovered with the public key alone.
        var parameters = key.ExportParameters(false);
        var modulus = new System.Numerics.BigInteger(parameters.Modulus, isUnsigned: true, isBigEndian: true);
        var exponent = new System.Numerics.BigInteger(parameters.Exponent, isUnsigned: true, isBigEndian: true);
        var value = new System.Numerics.BigInteger(signature, isUnsigned: true, isBigEndian: true);
        if (value >= modulus) return false;

        var recovered = System.Numerics.BigInteger.ModPow(value, exponent, modulus).ToByteArray(isUnsigned: true, isBigEndian: true);
        var length = parameters.Modulus!.Length;
        if (recovered.Length != length - 1) return false; // the leading 00 is dropped by the conversion

        var padding = recovered.Length - digest.Length - 1;
        if (padding < 9 || recovered[0] != 0x01 || recovered[padding] != 0x00) return false;
        for (var i = 1; i < padding; i++) if (recovered[i] != 0xFF) return false;

        return CryptographicOperations.FixedTimeEquals(recovered.AsSpan(padding + 1), digest);
    }

    private static byte[] Hash(HashAlgorithmName algorithm, byte[] data) =>
        algorithm == HashAlgorithmName.SHA1 ? SHA1.HashData(data) : SHA256.HashData(data);

    private static bool ChainsToTrustedRoot(X509Certificate2 certificate, X509Certificate2Collection extra,
                                            DateTime at, string usage)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = at.ToLocalTime();
        chain.ChainPolicy.ExtraStore.AddRange(extra);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(usage));

        // ⚠ The intermediates come from the signature itself. One missing from it would be fetched
        // from the network by Windows and not by Linux — the verdict would differ by system. Unity's
        // signatures carry theirs (read on every signed module of this machine, 2026-09-21).
        return chain.Build(certificate);
    }
}
