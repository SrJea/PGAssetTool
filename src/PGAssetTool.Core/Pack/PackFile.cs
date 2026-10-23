using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PGAssetTool.Core.Pack;

/// What is known about who built a pack and whether it still says what they wrote.
public sealed record PackSeal(bool Protected, string Author, string Fingerprint, SealState State)
{
    public static PackSeal Plain { get; } = new(false, "", "", SealState.Unsigned);

    public string Describe => State switch
    {
        SealState.Signed when Author.Length > 0 => $"signed by {Author} ({Fingerprint})",
        SealState.Signed => $"signed ({Fingerprint})",
        SealState.Altered => "changed since it was built",
        SealState.Invalid => "its signature cannot be read",
        _ => "not signed",
    };

    /// Whether this is something to say before installing rather than after.
    public bool Wrong => State is SealState.Altered or SealState.Invalid;
}

public enum SealState
{
    /// No signature at all: a pack built with protection off.
    Unsigned,

    /// The contents are what the holder of that key put in.
    Signed,

    /// There is a signature and the contents no longer match it.
    Altered,

    /// It is a protected pack and its seal cannot be read at all: the key, the signature or the
    /// header is not what it should be.
    ///
    /// Kept apart from Unsigned. Folded into it, anything that fails to decode comes back as a
    /// plain unsigned zip, so a damaged or doctored seal can be made to disappear simply by
    /// breaking it — and the pack still reads, still installs, and raises nothing. Saying "not
    /// signed" about a file that plainly carries a signature is the one thing this refuses to do;
    /// what to do about it is the caller's.
    Invalid,
}

/// A .pgmod on disk, in either of the two shapes it comes in.
///
/// Plainly, it is a zip. Protected, it is a small container: a readable header saying who built it
/// and a payload that is the same zip with a keystream over it. The header stays readable because
/// the manager wants to say who a pack came from without unpacking it, and because a file that
/// gives no account of itself is worse than one that does.
///
/// The scrambling stops a pack being renamed to .zip and opened. It stops nothing else — this
/// tool's source says exactly how to undo it — and it is not meant to. What it is for is that
/// somebody's work is not casually lifted out of the file they published.
public static class PackFile
{
    private static readonly byte[] Magic = "PGMOD"u8.ToArray();

    /// The shape of the container, written straight after the magic.
    ///
    /// A byte of its own, and not a control character inside the magic's own string literal: there
    /// nothing shows it — not the editor, not a diff, not a grep for the constant — and anything
    /// that strips control characters from a source file, a careless paste or a helpful tool, would
    /// change the magic with no line in the diff to say so and leave every pack built afterwards
    /// unreadable by every build before it.
    ///
    /// 2 signs the author's name along with the contents. 1 signed the contents alone and is not
    /// read: no pack ever published is in that shape.
    private const byte Format = 2;

    /// Fixed, and deliberately so: any build of this tool has to be able to open any pack. A key
    /// only the author knew would make a pack nobody else could install, which is a different
    /// feature and not this one.
    private static readonly byte[] Seed = "PGAssetTool pack payload"u8.ToArray();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Header(string Author, string PublicKey, string Signature);

    /// Reads a pack, whichever shape it is in. The archive owns the stream it was opened from.
    ///
    /// A file that is not one comes back named. The zip layer's own answer is "Central Directory
    /// corrupt", which says nothing about which file it was talking about or that a pack was what
    /// it expected — and the file it is talking about arrived from somebody else, so being unable
    /// to read it is an ordinary event rather than a fault.
    public static ZipArchive Open(string path)
    {
        var contents = Contents(path);
        try
        {
            return new ZipArchive(new MemoryStream(contents), ZipArchiveMode.Read);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' is not a pack this can read: {e.Message}", e);
        }
    }

    /// As much of a pack as anything here will hold in memory at once.
    ///
    /// A pack is a description of changes and the files they need; the largest one this tool has
    /// built is a few megabytes. The limit is here rather than at the zip because the file is read
    /// whole before any of it is understood — inspecting a seal, listing the manager, drawing a
    /// tile — so a pack big enough to exhaust memory does so before reaching a single check.
    public const long Most = 512L * 1024 * 1024;

    /// The zip inside a pack, unscrambled if it needed to be.
    public static byte[] Contents(string path)
    {
        var raw = ReadWhole(path);
        return Split(raw) is var (_, payload) ? payload : raw;
    }

    /// Who built it and whether it still matches, without unpacking anything.
    public static PackSeal Inspect(string path)
    {
        byte[] raw;
        (Header Header, byte[] Payload)? split;
        try
        {
            raw = ReadWhole(path);
            split = Split(raw);
        }
        catch (Exception e) when (e is IOException or InvalidDataException)
        {
            // Nothing readable to speak for. A file that is not there, or is too big to hold, is
            // not a claim about whether anybody signed anything.
            return e is InvalidDataException ? Unreadable : PackSeal.Plain;
        }

        if (split is not var (header, payload)) return PackSeal.Plain;

        // Past here the file says it carries a seal, so a seal that will not decode is reported as
        // one that will not decode. It is never quietly demoted to "not signed".
        try
        {
            var publicKey = Convert.FromBase64String(header.PublicKey);
            var state = PackAuthor.Verifies(
                    Signed(header.Author, payload),
                    Convert.FromBase64String(header.Signature), publicKey)
                ? SealState.Signed
                : SealState.Altered;

            return new PackSeal(true, header.Author, PackAuthor.FingerprintOf(publicKey), state);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or CryptographicException)
        {
            return new PackSeal(true, header.Author, "", SealState.Invalid);
        }
    }

    /// Too big to hold, or a container whose header will not parse. Not claimed as protected:
    /// nothing was read far enough to know that, and Invalid says only that no seal could be read.
    private static PackSeal Unreadable { get; } = new(false, "", "", SealState.Invalid);

    private static byte[] ReadWhole(string path)
    {
        var length = new FileInfo(path).Length;
        if (length > Most)
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' is {length / (1024 * 1024)}MB, and a pack is not read past "
                + $"{Most / (1024 * 1024)}MB.");

        return File.ReadAllBytes(path);
    }

    /// Writes a pack: the zip as it is, or wrapped and signed.
    public static void Write(string path, byte[] zip, PackAuthor? signer, string author)
    {
        // Both ways out go through one write that either lands whole or does not land: a pack cut
        // off half way is a file that still looks like a pack, and the protected one would then be
        // a container whose header runs off the end of it.
        if (signer is null)
        {
            Settings.AtomicFile.WriteAllBytes(path, zip);
            return;
        }

        var header = JsonSerializer.SerializeToUtf8Bytes(
            new Header(author, Convert.ToBase64String(signer.PublicKey),
                Convert.ToBase64String(signer.Sign(Signed(author, zip)))),
            Json);

        var file = new MemoryStream(Magic.Length + 5 + header.Length + zip.Length);
        file.Write(Magic);
        file.WriteByte(Format);

        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, header.Length);
        file.Write(length);
        file.Write(header);

        // Signed over the plain contents, scrambled on the way out: the signature says what the
        // pack holds, not how it happens to be stored.
        file.Write(Scramble((byte[])zip.Clone()));

        Settings.AtomicFile.WriteAllBytes(path, file.ToArray());
    }

    /// Splits a protected pack into what it says about itself and what it holds. Null for a plain
    /// zip, which is every pack built with protection off.
    ///
    /// The two ways of not being a protected pack are kept apart. Null means it never claimed to be
    /// one; a throw means it did and the claim is broken. Folded together, a doctored container
    /// passes as an ordinary unsigned zip.
    private static (Header Header, byte[] Payload)? Split(byte[] raw)
    {
        if (raw.Length < Magic.Length + 5 || !raw.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;

        // A shape this build does not know is not a pack it can speak for. Refusing here rather
        // than reading it anyway means another format is a plain "not signed" to this build, never
        // a wrong answer about who wrote it.
        if (raw[Magic.Length] != Format) return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(Magic.Length + 1));
        var at = Magic.Length + 1 + 4;
        if (length < 0 || at + length > raw.Length)
            throw new InvalidDataException("the pack says it is protected and its header runs off the end.");

        Header? header;
        try
        {
            header = JsonSerializer.Deserialize<Header>(raw.AsSpan(at, length), Json);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"the pack says it is protected and its header is not readable: {e.Message}");
        }

        if (header is null)
            throw new InvalidDataException("the pack says it is protected and carries no header.");

        return (header, Scramble(raw[(at + length)..]));
    }

    /// What the signature is taken over: the name in the header as well as the contents.
    ///
    /// A name that is not signed is only a label. Over the contents alone, the author's name can be
    /// rewritten in a pack that goes on reading as genuinely signed, by the genuine key, with the
    /// genuine fingerprint beside it — somebody else's name over work they did not put it to, or a
    /// name taken off work that was theirs. Nothing about the contents is at risk either way, which
    /// is what makes it easy to miss.
    ///
    /// Re-signing a pack under another key is not prevented, because it cannot be. What it costs is
    /// the fingerprint; the name is what cannot be moved without the key.
    ///
    /// The name is length-prefixed rather than run together with the payload, so that no two
    /// (name, contents) pairs can produce the same bytes to sign.
    private static byte[] Signed(string author, byte[] payload)
    {
        var name = Encoding.UTF8.GetBytes(author);
        var bytes = new byte[4 + name.Length + payload.Length];

        BinaryPrimitives.WriteInt32LittleEndian(bytes, name.Length);
        name.CopyTo(bytes, 4);
        payload.CopyTo(bytes, 4 + name.Length);
        return bytes;
    }

    /// Its own inverse: the same keystream over the bytes puts them back.
    private static byte[] Scramble(byte[] bytes)
    {
        Span<byte> counter = stackalloc byte[Seed.Length + 4];
        Seed.CopyTo(counter);

        var block = new byte[SHA256.HashSizeInBytes];
        for (var at = 0; at < bytes.Length; at += block.Length)
        {
            BinaryPrimitives.WriteInt32LittleEndian(counter[Seed.Length..], at / block.Length);
            SHA256.HashData(counter, block);

            var run = Math.Min(block.Length, bytes.Length - at);
            for (var i = 0; i < run; i++) bytes[at + i] ^= block[i];
        }
        return bytes;
    }
}
