using PGAssetTool.Core.Game;
using PGAssetTool.Core.Mods;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Tests;

public class ModStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pgassettool-store").FullName;
    private readonly ModStore _store;

    private readonly string _home;

    public ModStoreTests()
    {
        var bundles = Path.Combine(_root, "Game_Data", "StreamingAssets", "Cache", "bundles");
        Directory.CreateDirectory(bundles);
        File.WriteAllText(Path.Combine(bundles, "embedded_asset_bundles.json"), "[]");
        _home = Path.Combine(_root, "home");
        _store = new ModStore(GameInstallation.Open(_root), _home);
    }

    [Fact]
    public void TheStoreLivesUnderItsHome()
    {
        // Not under the game: uninstalling it would take the backups with it, at the moment the
        // downloaded bundle cache outlives them.
        Assert.StartsWith(_home, _store.Root, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFullPath(_root) + Path.DirectorySeparatorChar + "Game_Data", _store.Root);
    }

    [Fact]
    public void TwoInstallationsDoNotShareAStore()
    {
        var other = Directory.CreateTempSubdirectory("pgassettool-other").FullName;
        try
        {
            var bundles = Path.Combine(other, "Game_Data", "StreamingAssets", "Cache", "bundles");
            Directory.CreateDirectory(bundles);
            File.WriteAllText(Path.Combine(bundles, "embedded_asset_bundles.json"), "[]");
            Assert.NotEqual(_store.Root, new ModStore(GameInstallation.Open(other), _home).Root);
        }
        finally { Directory.Delete(other, recursive: true); }
    }

    [Fact]
    public void TheDefaultHomeSitsBesideTheToolRatherThanInsideItsBuildOutput()
    {
        var home = ModStore.DefaultHome();
        Assert.EndsWith(ModStore.DataDirectoryName, home, StringComparison.Ordinal);
        Assert.DoesNotContain($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", home);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string WriteLiveBundle(string bundle, string hash, string content)
    {
        var path = Path.Combine(_root, "Game_Data", "StreamingAssets", "Cache", "bundles", bundle, hash, bundle);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void BackupAndRestoreReturnTheOriginalBytes()
    {
        var live = WriteLiveBundle("woi_0", "aaa", "original");
        _store.Backup(CacheKind.Shipped, "woi_0", "aaa", live);
        File.WriteAllText(live, "modified");

        Assert.True(_store.RestoreIfBackedUp(CacheKind.Shipped, "woi_0", "aaa", live));
        Assert.Equal("original", File.ReadAllText(live));
    }

    [Fact]
    public void BackingUpTwiceKeepsTheFirstCopy()
    {
        var live = WriteLiveBundle("woi_0", "aaa", "original");
        _store.Backup(CacheKind.Shipped, "woi_0", "aaa", live);
        File.WriteAllText(live, "modified");
        _store.Backup(CacheKind.Shipped, "woi_0", "aaa", live);

        _store.RestoreIfBackedUp(CacheKind.Shipped, "woi_0", "aaa", live);
        Assert.Equal("original", File.ReadAllText(live));
    }

    [Fact]
    public void ABackupSaysWhatItHoldsEvenWhenItsNameDoesNot()
    {
        // Filed under "aaa", holding something whose MD5 is not that: a bundle somebody else had
        // already modified. Asked by name, a live copy identical to it never matched.
        var live = WriteLiveBundle("woi_0", "aaa", "modified before this tool came");
        _store.Backup(CacheKind.Shipped, "woi_0", "aaa", live);

        var md5 = BundleIntegrity.Md5(live);
        Assert.Equal(md5, _store.BackupMd5(CacheKind.Shipped, "woi_0", "aaa"));
        // Kept, and the kept answer is the one given the second time.
        Assert.True(File.Exists(_store.BackupPathFor(CacheKind.Shipped, "woi_0", "aaa") + ".md5"));
        Assert.Equal(md5, _store.BackupMd5(CacheKind.Shipped, "woi_0", "aaa"));
        // And the note beside the backup is not taken for another backup.
        Assert.Single(_store.BackedUp());

        Assert.Null(_store.BackupMd5(CacheKind.Shipped, "woi_0", "bbb"));
    }

    [Fact]
    public void RestoringWithoutABackupSaysSo()
    {
        var live = WriteLiveBundle("woi_0", "aaa", "original");
        Assert.False(_store.RestoreIfBackedUp(CacheKind.Shipped, "woi_0", "bbb", live));
    }

    [Fact]
    public void BackupsAreFiledUnderTheBundleVersionTheyCameFrom()
    {
        var oldLive = WriteLiveBundle("woi_0", "aaa", "v1");
        _store.Backup(CacheKind.Shipped, "woi_0", "aaa", oldLive);
        var newLive = WriteLiveBundle("woi_0", "bbb", "v2");
        _store.Backup(CacheKind.Shipped, "woi_0", "bbb", newLive);

        Assert.True(_store.HasBackup(CacheKind.Shipped, "woi_0", "aaa"));
        Assert.True(_store.HasBackup(CacheKind.Shipped, "woi_0", "bbb"));
    }

    [Fact]
    public void AnUpdateLeavesTheBackupForTheSupersededVersionToBePruned()
    {
        _store.Backup(CacheKind.Shipped, "woi_0", "aaa", WriteLiveBundle("woi_0", "aaa", "v1"));
        _store.Backup(CacheKind.Shipped, "woi_0", "bbb", WriteLiveBundle("woi_0", "bbb", "v2"));
        _store.Backup(CacheKind.Shipped, "bhlw", "ccc", WriteLiveBundle("bhlw", "ccc", "unchanged"));

        var pruned = _store.PruneStaleBackups(new Dictionary<string, string>
        {
            ["woi_0"] = "bbb",
            ["bhlw"] = "ccc",
        });

        Assert.Equal(["shipped/woi_0/aaa"], pruned);
        Assert.False(_store.HasBackup(CacheKind.Shipped, "woi_0", "aaa"));
        Assert.True(_store.HasBackup(CacheKind.Shipped, "woi_0", "bbb"));
        Assert.True(_store.HasBackup(CacheKind.Shipped, "bhlw", "ccc"));
    }

    [Fact]
    public void ABundleIsPristineWhileItsContentStillMatchesItsCacheDirectory()
    {
        // The game names each bundle's cache directory after the file's MD5, which is what makes
        // "has something else already edited this?" answerable at all.
        var path = Path.Combine(_root, "sample");
        File.WriteAllText(path, "shipped bytes");
        var hash = BundleIntegrity.Md5(path);

        Assert.True(BundleIntegrity.IsPristine(path, hash));
        Assert.True(BundleIntegrity.IsPristine(path, hash.ToUpperInvariant()));

        File.AppendAllText(path, "!");
        Assert.False(BundleIntegrity.IsPristine(path, hash));
    }

    [Fact]
    public void WritingKeepsTheStateItReplaced()
    {
        // Losing this file loses the record of which mod put what where, and with it the ability to
        // uninstall through the tool.
        var mod = new InstalledMod
        {
            Id = "a", Name = "A", PackPath = "a.pgmod", InstalledAt = DateTimeOffset.UnixEpoch,
            GameVersion = "26.11.0", TouchedBundles = new Dictionary<string, string>(),
        };
        _store.Write([mod]);
        _store.Write([]);

        Assert.Empty(_store.Read());
        Assert.Contains("\"a\"", File.ReadAllText(Path.Combine(_store.Root, "installed.json.previous")));
    }

    [Fact]
    public void InstalledModsRoundTrip()
    {
        _store.Write([
            new InstalledMod
            {
                Id = "a", Name = "A", PackPath = "a.pgmod", InstalledAt = DateTimeOffset.UnixEpoch,
                GameVersion = "26.11.0", Enabled = false,
                TouchedBundles = new Dictionary<string, string> { ["woi_0"] = "aaa" },
            },
        ]);

        var mod = Assert.Single(_store.Read());
        Assert.Equal("a", mod.Id);
        Assert.False(mod.Enabled);
        Assert.Equal("aaa", mod.TouchedBundles["woi_0"]);
    }

    [Fact]
    public void EveryBackupIsFoundEvenWhenNoModClaimsItAnyMore()
    {
        // What has been written to is recorded by the backups, not by the installed list: uninstall
        // drops the mod before the reconcile that would put its bundles back.
        _store.Backup(CacheKind.Shipped, "bhlw", "b356", Bundle("original"));
        _store.Backup(CacheKind.Downloaded, "woi_0", "1773", Bundle("original"));

        var found = _store.BackedUp().OrderBy(b => b.Bundle).ToList();

        Assert.Equal([(CacheKind.Shipped, "bhlw", "b356"), (CacheKind.Downloaded, "woi_0", "1773")],
            found.OrderBy(b => b.Bundle == "bhlw" ? 0 : 1));
    }

    [Fact]
    public void TheLayoutThatPredatedCacheKindsIsIgnoredRatherThanMisread()
    {
        // Backups once sat directly under the bundle name, with no cache segment above it.
        var legacy = Path.Combine(_store.BackupRoot, "woi_0", "1773");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "woi_0"), "original");

        Assert.Empty(_store.BackedUp());
    }

    private string Bundle(string contents)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("n"));
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void AnInstalledPackIsCopiedOutOfWhereverItWasBuilt()
    {
        // A pack built into a workspace is one deletion away from leaving an installed mod with no
        // file to reapply or remove from. That happened, and cost an uninstall to recover.
        var built = Path.Combine(_root, "somewhere", "weapon.pgmod");
        Directory.CreateDirectory(Path.GetDirectoryName(built)!);
        File.WriteAllText(built, "a pack");

        var kept = _store.Keep(built);

        Assert.StartsWith(_store.ModsDirectory, kept, StringComparison.Ordinal);
        Assert.Equal("a pack", File.ReadAllText(kept));

        Directory.Delete(Path.GetDirectoryName(built)!, recursive: true);
        Assert.True(File.Exists(kept), "the copy did not outlive the directory it came from");
    }

    [Fact]
    public void TwoPacksOfTheSameNameFromDifferentPlacesDoNotCollide()
    {
        var first = Path.Combine(_root, "a", "weapon.pgmod");
        var second = Path.Combine(_root, "b", "weapon.pgmod");
        foreach (var path in new[] { first, second })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, path);
        }

        Assert.NotEqual(_store.Keep(first), _store.Keep(second));
        Assert.Equal(first, File.ReadAllText(_store.Keep(first)));
        Assert.Equal(second, File.ReadAllText(_store.Keep(second)));
    }

    [Fact]
    public void KeepingAPackAlreadyInTheStoreLeavesItAlone()
    {
        // Reinstalling from the copy must not spiral into copies of copies.
        var built = Path.Combine(_root, "weapon.pgmod");
        File.WriteAllText(built, "a pack");

        var kept = _store.Keep(built);
        Assert.Equal(kept, _store.Keep(kept));
        Assert.Single(_store.KeptPacks());
    }

    [Fact]
    public void APackIsFiledUnderItsWeaponAndTheLookItChanges()
    {
        // The arrangement on disk is the one the manager shows, and both are the two questions
        // somebody has about an installed mod: which weapon, and which of its skins.
        var built = Path.Combine(_root, "mine.pgmod");
        File.WriteAllText(built, "a pack");

        var kept = _store.Keep(built, new PackSubject
        {
            Kind = PackKind.Weapon, Id = "ultimatum", Number = 416, Prefab = "Weapon834",
            Name = "Ultimatum", Variant = "Weapon834_nuclear_reactor", VariantName = "Nuclear Reactor",
        });

        Assert.Equal(
            Path.Combine(_store.ModsDirectory, PackKind.Weapon, "0416_ultimatum", "nuclear_reactor"),
            Path.GetDirectoryName(kept));
    }

    [Fact]
    public void TheWeaponAsItComesIsALookOfItsOwn()
    {
        var built = Path.Combine(_root, "plain.pgmod");
        File.WriteAllText(built, "a pack");

        var kept = _store.Keep(built, new PackSubject { Kind = PackKind.Weapon, Id = "beretta", Number = 16, Name = "Beretta" });

        Assert.Equal(
            Path.Combine(_store.ModsDirectory, PackKind.Weapon, "0016_beretta", "default"),
            Path.GetDirectoryName(kept));
    }

    [Fact]
    public void APackThatSaysNothingAboutItselfIsFiledSomewhereThatSaysSo()
    {
        var built = Path.Combine(_root, "converted.pgmod");
        File.WriteAllText(built, "a pack");

        Assert.Equal(
            Path.Combine(_store.ModsDirectory, ModStore.Unfiled),
            Path.GetDirectoryName(_store.Keep(built)));
    }

    [Fact]
    public void DeletingTheLastPackUnderAWeaponTakesTheFolderWithIt()
    {
        // A heading over nothing is worse than no heading: the tree is there to be read.
        var built = Path.Combine(_root, "only.pgmod");
        File.WriteAllText(built, "a pack");

        var subject = new PackSubject { Kind = PackKind.Weapon, Id = "ultimatum", Number = 416, Name = "Ultimatum" };
        var kept = _store.Keep(built, subject);

        var mod = new InstalledMod
        {
            Id = "ultimatum-default-abcd1234", Name = "Ultimatum", PackPath = kept,
            InstalledAt = DateTimeOffset.Now, GameVersion = "1", TouchedBundles = [],
        };

        Assert.True(_store.DiscardKeptPack(mod));
        Assert.False(Directory.Exists(Path.Combine(_store.ModsDirectory, PackKind.Weapon, "0416_ultimatum")));
        Assert.True(Directory.Exists(_store.ModsDirectory));
    }

    [Fact]
    public void ThePackStoreSitsBesideEverythingElseTheToolKeeps()
        => Assert.Equal(Path.Combine(_home, "mods"), _store.ModsDirectory);

    /// A truncated ledger is what an interrupted write used to leave, and answering "no mods" for
    /// one would let the next install replace the record of every other mod with itself.
    [Fact]
    public void ALedgerThatCannotBeReadFallsBackToTheCopyKeptBesideIt()
    {
        _store.Write([Mod("a"), Mod("b")]);
        _store.Write([Mod("a"), Mod("b"), Mod("c")]);

        var ledger = Path.Combine(_store.Root, "installed.json");
        File.WriteAllText(ledger, "[{\"id\": \"a\", trunca");

        // The copy is the ledger as it was before the last change: wrong by at most one mod.
        Assert.Equal(["a", "b"], _store.Read().Select(m => m.Id));

        // And it is put back, so the next write does not file the broken one as the copy to keep.
        Assert.Equal(["a", "b"], _store.Read().Select(m => m.Id));
        _store.Write([Mod("a")]);
        Assert.Contains("\"b\"", File.ReadAllText(ledger + ".previous"));
    }

    [Fact]
    public void ALedgerWithNoReadableCopySaysSoRatherThanAnsweringThatNothingIsInstalled()
    {
        _store.Write([Mod("a")]);
        var ledger = Path.Combine(_store.Root, "installed.json");
        File.WriteAllText(ledger, "not json at all");
        File.WriteAllText(ledger + ".previous", "nor is this");

        var refused = Assert.Throws<InvalidDataException>(() => _store.Read());
        Assert.Contains("installed.json", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyLedgerFileIsStillNotReadAsNoMods()
    {
        _store.Write([Mod("a")]);
        _store.Write([Mod("a"), Mod("b")]);

        var ledger = Path.Combine(_store.Root, "installed.json");
        File.WriteAllText(ledger, "");

        // What an interrupted write leaves most often of all: nothing at all.
        Assert.Equal(["a"], _store.Read().Select(m => m.Id));
    }

    private static InstalledMod Mod(string id) => new()
    {
        Id = id, Name = id.ToUpperInvariant(), PackPath = id + ".pgmod",
        InstalledAt = DateTimeOffset.UnixEpoch, GameVersion = "26.11.0",
        TouchedBundles = new Dictionary<string, string>(),
    };

    [Fact]
    public void TheDigestGoesAfterTheNameSoTheFolderSortsByWhatItHolds()
    {
        var built = Path.Combine(_root, "somewhere", "0016_Beretta.pgmod");
        Directory.CreateDirectory(Path.GetDirectoryName(built)!);
        File.WriteAllText(built, "a pack");

        var kept = Path.GetFileName(_store.Keep(built));

        Assert.StartsWith("0016_Beretta-", kept, StringComparison.Ordinal);
        Assert.EndsWith(".pgmod", kept, StringComparison.Ordinal);
    }
}
