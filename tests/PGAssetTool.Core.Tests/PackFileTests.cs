using System.IO.Compression;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Tests;

public class PackFileTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("pgassettool-seal").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Path(string name) => System.IO.Path.Combine(_home, name);

    /// A zip with one entry, which is all any of this needs to be about.
    private static byte[] Zip(string contents = "the pack")
    {
        var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("pgmod.json").Open()))
            writer.Write(contents);
        return bytes.ToArray();
    }

    private static string Read(ZipArchive archive)
    {
        using var reader = new StreamReader(archive.GetEntry("pgmod.json")!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void APlainZipIsStillAPack()
    {
        // Every pack built with protection off is one, and refusing to open those would be a worse
        // kind of protection than none.
        var path = Path("plain.pgmod");
        PackFile.Write(path, Zip(), signer: null, author: "");

        Assert.Equal(Zip(), File.ReadAllBytes(path));
        Assert.Equal("the pack", Read(PackFile.Open(path)));
        Assert.Equal(SealState.Unsigned, PackFile.Inspect(path).State);
        Assert.False(PackFile.Inspect(path).Protected);
    }

    [Fact]
    public void AProtectedPackComesBackTheSameAndSaysWhoBuiltIt()
    {
        using var me = PackAuthor.Mine(_home);
        var path = Path("sealed.pgmod");
        PackFile.Write(path, Zip(), me, "mof22");

        Assert.Equal("the pack", Read(PackFile.Open(path)));

        var seal = PackFile.Inspect(path);
        Assert.True(seal.Protected);
        Assert.Equal(SealState.Signed, seal.State);
        Assert.Equal("mof22", seal.Author);
        Assert.Equal(me.Fingerprint, seal.Fingerprint);
    }

    [Fact]
    public void AProtectedPackDoesNotOpenAsAZip()
    {
        // The whole of what the scrambling is for: renaming it and double-clicking gets nowhere.
        using var me = PackAuthor.Mine(_home);
        var path = Path("sealed.pgmod");
        PackFile.Write(path, Zip(), me, "mof22");

        Assert.Throws<InvalidDataException>(() => ZipFile.OpenRead(path));
        Assert.NotEqual(Zip(), File.ReadAllBytes(path));
    }

    [Fact]
    public void ChangingWhatIsInsideShowsUp()
    {
        // One byte or all of them: the signature covers the whole payload, so there is nothing to
        // be gained by testing a larger edit — and a test that reached in at a known offset would
        // be keeping its own copy of the file layout, which is the thing most likely to be wrong.
        using var me = PackAuthor.Mine(_home);
        var path = Path("sealed.pgmod");
        PackFile.Write(path, Zip(), me, "mof22");

        // One byte, in the payload rather than the header.
        var raw = File.ReadAllBytes(path);
        raw[^1] ^= 0xFF;
        File.WriteAllBytes(path, raw);

        Assert.Equal(SealState.Altered, PackFile.Inspect(path).State);
    }


    [Fact]
    public void ChangingTheNameOnItShowsUpAsWell()
    {
        // The name is part of what is signed, so it cannot be moved without the key: not onto
        // somebody else's work, and not off work that was theirs.
        using var me = PackAuthor.Mine(_home);
        var path = Path("sealed.pgmod");
        PackFile.Write(path, Zip(), me, "mof22");

        // Found in the header rather than at an offset. The header is readable on purpose, which
        // is all this needs to know about the container — and the replacement is the same length,
        // so nothing else moves either.
        var raw = File.ReadAllBytes(path);
        "mof99"u8.CopyTo(raw.AsSpan(raw.AsSpan().IndexOf("mof22"u8)));
        File.WriteAllBytes(path, raw);

        var seal = PackFile.Inspect(path);
        Assert.Equal("mof99", seal.Author);
        Assert.Equal(SealState.Altered, seal.State);
    }

    [Fact]
    public void SigningWithAnotherKeyReadsAsSomebodyElsesPack()
    {
        // Re-signing is not prevented — it cannot be. What it costs is the author's name: an
        // altered pack signed again carries a different fingerprint, and anybody who knows the
        // author's own can see that.
        using var me = PackAuthor.Mine(_home);
        var mineFingerprint = me.Fingerprint;

        var elsewhere = Directory.CreateTempSubdirectory("pgassettool-other").FullName;
        try
        {
            using var them = PackAuthor.Mine(elsewhere);
            var path = Path("resigned.pgmod");
            PackFile.Write(path, Zip(), them, "mof22");

            var seal = PackFile.Inspect(path);
            Assert.Equal(SealState.Signed, seal.State);
            Assert.NotEqual(mineFingerprint, seal.Fingerprint);
        }
        finally { Directory.Delete(elsewhere, recursive: true); }
    }

    [Fact]
    public void TheKeyIsMadeOnceAndKeptBesideEverythingElse()
    {
        using var first = PackAuthor.Mine(_home);
        using var again = PackAuthor.Mine(_home);

        Assert.True(File.Exists(Path(PackAuthor.FileName)));
        Assert.Equal(first.Fingerprint, again.Fingerprint);
    }

    [Fact]
    public void AKeyFileThatCannotBeReadIsReplacedRatherThanFatal()
    {
        // Losing the key costs an author their identity on later packs, which is bad. Refusing to
        // build anything at all would be worse.
        File.WriteAllText(Path(PackAuthor.FileName), "not a key");

        using var made = PackAuthor.Mine(_home);

        Assert.Equal(16, made.Fingerprint.Length);
        Assert.True(File.Exists(Path(PackAuthor.FileName) + ".unreadable"));
    }

    [Fact]
    public void SomethingThatIsNotAPackAtAllIsNotClaimedToBeSigned()
        => Assert.Equal(SealState.Unsigned, PackFile.Inspect(Path("nothing here.pgmod")).State);

    [Fact]
    public void BreakingASignatureDoesNotTurnAPackIntoAnUnsignedOne()
    {
        // The one thing an alteration must never be able to do is erase the evidence of itself.
        // Let anything that fails to decode come back as a plain unsigned zip and a single
        // character in the signature turns "changed since it was built" into "not signed" — a pack
        // that reads, installs, and raises nothing.
        using var me = PackAuthor.Mine(_home);
        var path = Path("broken.pgmod");
        PackFile.Write(path, Zip(), me, "mof22");

        var raw = File.ReadAllBytes(path);
        raw[raw.AsSpan().IndexOf("\"signature\":\""u8) + 13] = (byte)'!';
        File.WriteAllBytes(path, raw);

        Assert.Equal(SealState.Invalid, PackFile.Inspect(path).State);
        Assert.True(PackFile.Inspect(path).Wrong);
    }

    [Fact]
    public void AHeaderThatWillNotParseIsNotAPlainZipEither()
    {
        using var me = PackAuthor.Mine(_home);
        var path = Path("mangled.pgmod");
        PackFile.Write(path, Zip(), me, "mof22");

        var raw = File.ReadAllBytes(path);
        raw[raw.AsSpan().IndexOf("{\"author\""u8)] = (byte)'x';
        File.WriteAllBytes(path, raw);

        Assert.Equal(SealState.Invalid, PackFile.Inspect(path).State);
    }

    [Fact]
    public void BytesAppendedToAPublicKeyDoNotBuyADifferentFingerprint()
    {
        // ImportSubjectPublicKeyInfo stops at the end of the structure and says nothing about what
        // follows, so a fingerprint taken over the whole array lets the same key, padded, verify
        // every signature under a fingerprint of somebody's choosing. A fingerprint that does not
        // name the key it verifies with is worse than none.
        using var me = PackAuthor.Mine(_home);
        var padded = me.PublicKey.Concat(new byte[] { 1, 2, 3, 4 }).ToArray();

        Assert.Throws<System.Security.Cryptography.CryptographicException>(
            () => PackAuthor.FingerprintOf(padded));
        Assert.False(PackAuthor.Verifies("x"u8.ToArray(), me.Sign("x"u8.ToArray()), padded));
    }

    [Fact]
    public void APackTooBigToHoldIsNotReadAtAll()
    {
        // Read whole before any of it is understood, so the size to refuse is the file's — a pack
        // large enough to exhaust memory did so before reaching a single check, and the manager
        // reads every pack it lists.
        var path = Path("huge.pgmod");
        using (var file = File.Create(path)) file.SetLength(PackFile.Most + 1);

        Assert.Equal(SealState.Invalid, PackFile.Inspect(path).State);
        Assert.Throws<InvalidDataException>(() => PackFile.Contents(path));
    }

    /// Whatever somebody hands the tool. The zip layer's own answer names neither the file nor what
    /// was expected of it, and it reaches the window as it is.
    [Fact]
    public void AFileThatIsNotAPackAtAllIsRefusedByName()
    {
        var path = Path("holiday-photo.pgmod");
        File.WriteAllText(path, "this is not a pack");

        var refused = Assert.Throws<InvalidDataException>(() => PackFile.Open(path));

        Assert.Contains("holiday-photo.pgmod", refused.Message, StringComparison.Ordinal);
        Assert.Contains("not a pack", refused.Message, StringComparison.Ordinal);
    }
}
