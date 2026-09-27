using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Export;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Tests;

public class WorkspaceViewTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pgassettool-view").FullName;

    /// A workspace as `extract --workspace` leaves one: files plus a manifest naming their targets.
    private string MakeWorkspace(string name, params (string Path, string Content)[] files)
    {
        var directory = Path.Combine(_root, name);
        var assets = new List<ExportedAsset>();

        foreach (var (relative, content) in files)
        {
            var full = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);

            assets.Add(new ExportedAsset(full, AssetsTools.NET.Extra.AssetClassID.Texture2D,
                Path.GetFileNameWithoutExtension(relative), Path.GetExtension(relative).TrimStart('.'),
                content.Length, new AssetAddress("bhlw", "Texture2D", Path.GetFileNameWithoutExtension(relative))));
        }

        Workspace.Create(directory, name, name, "tester", "26.11.0", assets);
        return directory;
    }

    [Fact]
    public void AFreshWorkspaceHasNothingEdited()
    {
        var directory = MakeWorkspace("w1", ("textures/a.png", "original"), ("textures/b.png", "original"));

        var view = WorkspaceView.Open(directory);

        Assert.NotNull(view);
        Assert.Equal(2, view.Files.Count);
        Assert.Equal(0, view.EditedCount);
    }

    [Fact]
    public void EditingAFileShowsUpOnTheNextRead()
    {
        // The files are edited by other programs, so the answer has to be taken when asked rather
        // than remembered from when the workspace was written.
        var directory = MakeWorkspace("w2", ("textures/a.png", "original"), ("textures/b.png", "original"));
        Assert.Equal(0, WorkspaceView.Open(directory)!.EditedCount);

        File.WriteAllText(Path.Combine(directory, "textures", "a.png"), "painted over");

        var view = WorkspaceView.Open(directory)!;
        Assert.Equal(1, view.EditedCount);
        Assert.True(view.Files.Single(f => f.Name == "a.png").Edited);
        Assert.False(view.Files.Single(f => f.Name == "b.png").Edited);
    }

    [Fact]
    public void EditingAFileBackToWhatItWasCountsAsUnedited()
    {
        // The hash decides, not the timestamp: undoing an edit has to leave the file out of the
        // pack, or a pack would contain operations that change nothing.
        var directory = MakeWorkspace("w3", ("textures/a.png", "original"));
        var path = Path.Combine(directory, "textures", "a.png");

        File.WriteAllText(path, "painted over");
        Assert.Equal(1, WorkspaceView.Open(directory)!.EditedCount);

        File.WriteAllText(path, "original");
        Assert.Equal(0, WorkspaceView.Open(directory)!.EditedCount);
    }

    [Fact]
    public void EveryWorkspaceUnderTheRootIsFound()
    {
        MakeWorkspace("0016_Beretta", ("textures/a.png", "x"));
        MakeWorkspace("0058_Plasma", ("textures/b.png", "y"));
        Directory.CreateDirectory(Path.Combine(_root, "not-a-workspace"));

        var found = WorkspaceView.Discover(_root);

        Assert.Equal(2, found.Count);
        Assert.All(found, d => Assert.True(File.Exists(Path.Combine(d, PackManifest.FileName))));
    }

    [Fact]
    public void ARootThatDoesNotExistYetIsEmptyRatherThanAnError()
        => Assert.Empty(WorkspaceView.Discover(Path.Combine(_root, "never-created")));

    [Fact]
    public void ADirectoryWithNoManifestOpensAsNothing()
    {
        var bare = Path.Combine(_root, "bare");
        Directory.CreateDirectory(bare);

        Assert.Null(WorkspaceView.Open(bare));
    }

    [Fact]
    public void FilesAreGroupedByFolderAndNamed()
    {
        var directory = MakeWorkspace("w4", ("meshes/z.png", "1"), ("textures/a.png", "2"));

        var view = WorkspaceView.Open(directory)!;

        Assert.Equal(["meshes", "textures"], view.Files.Select(f => f.Folder));
        Assert.Equal(["z.png", "a.png"], view.Files.Select(f => f.Name));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("icon/Beretta_icon1_big.png", true)]
    [InlineData("related/WeaponChatIcons/Weapon25_chaticon.png", true)]
    [InlineData("textures/Map_Beretta_A.png", false)]
    [InlineData("meshes/Beretta_3_Mesh.glb", false)]
    public void OnlyIconsMeanCoverageByTheirAlphaChannel(string relativePath, bool coverage)
    {
        // A model texture keeps emission there, so honouring it blanks the picture; an icon really
        // is cut out. The folder is what the workspace records, so the folder is what decides.
        var file = new WorkspaceFile(
            relativePath, relativePath, new AssetAddress("", "", ""), "replaceTexture", false, 0);

        Assert.Equal(coverage, file.AlphaIsCoverage);
    }

    [Fact]
    public void RenamingMovesTheDirectoryAndLeavesTheContentsAlone()
    {
        // The folder name is what the built .pgmod is called, so this is how an author names their
        // mod rather than living with the number and prefab the export chose.
        var directory = MakeWorkspace("0016_Beretta", ("icon/a.png", "one"), ("textures/b.png", "two"));

        var moved = Workspace.Rename(directory, "Synthwave Beretta");

        Assert.Equal(Path.Combine(_root, "Synthwave Beretta"), moved);
        Assert.False(Directory.Exists(directory));
        Assert.Equal("one", File.ReadAllText(Path.Combine(moved, "icon", "a.png")));
        Assert.Equal(2, WorkspaceView.Open(moved)!.Files.Count);
    }

    [Fact]
    public void RenamingOntoAnExistingWorkspaceIsRefusedRatherThanMerged()
    {
        var mine = MakeWorkspace("0016_Beretta", ("icon/a.png", "mine"));
        MakeWorkspace("theirs", ("icon/a.png", "theirs"));

        Assert.Contains("already a workspace",
            Assert.Throws<IOException>(() => Workspace.Rename(mine, "theirs")).Message);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(mine, "icon", "a.png")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("what?")]
    public void ANameThatCannotBeAFolderIsRefused(string name)
    {
        var directory = MakeWorkspace("0016_Beretta", ("icon/a.png", "one"));
        Assert.Throws<ArgumentException>(() => Workspace.Rename(directory, name));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void ChangingOnlyTheCapitalisationIsStillARename()
    {
        // The directory it "already exists" as is the one being renamed, so the collision check has
        // to let this one through.
        var directory = MakeWorkspace("beretta", ("icon/a.png", "one"));

        var moved = Workspace.Rename(directory, "Beretta");

        Assert.Equal("Beretta", Path.GetFileName(moved));
        Assert.Equal("Beretta", new DirectoryInfo(moved).Name);
    }

    [Fact]
    public void SavingTheManifestKeepsTheOperationsAndTheirBaselines()
    {
        // Only the descriptive half is ever edited by hand; the operations are addresses the export
        // resolved, and losing a baseline would make every unedited file look changed.
        var directory = MakeWorkspace("0016_Beretta", ("icon/a.png", "one"), ("textures/b.png", "two"));
        var before = Workspace.Read(directory);

        Workspace.Save(directory, before with
        {
            Name = "Synthwave Beretta", Author = "mof-22", Version = "2.1.0", Description = "neon",
        });

        var after = Workspace.Read(directory);
        Assert.Equal("Synthwave Beretta", after.Name);
        Assert.Equal("mof-22", after.Author);
        Assert.Equal("2.1.0", after.Version);
        Assert.Equal("neon", after.Description);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(
            before.Operations.Select(o => (o.Source, o.BaselineSha256)),
            after.Operations.Select(o => (o.Source, o.BaselineSha256)));
        Assert.Empty(Workspace.Changed(directory, after));
    }

    [Fact]
    public void ExtractionPicksTheWeaponsOwnIconWithoutBeingAsked()
    {
        // A pack has a picture without anybody deciding to give it one. The icon folder holds the
        // one image that stands for the whole weapon, and the largest of them is the one meant to
        // be looked at — the others are chat and profile sizes.
        var directory = MakeWorkspace("0016_Beretta",
            ("icon/Beretta_icon1_big.png", new string('x', 400)),
            ("related/WeaponChatIcons/Weapon25_chaticon.png", "small"),
            ("textures/Map_Beretta_A.png", "a texture"));

        Assert.Equal("icon/Beretta_icon1_big.png", Workspace.Read(directory).Icon);
    }

    [Fact]
    public void AWeaponWithNoIconFolderSimplyHasNoPicture()
    {
        var directory = MakeWorkspace("0016_Beretta", ("textures/Map_Beretta_A.png", "a texture"));

        Assert.Equal("", Workspace.Read(directory).Icon);
    }

    /// pgmod.json is a file the author edits by hand, so a stray comma in one is an ordinary event.
    /// It used to come back as a JsonException, which is not what anything reading a manifest
    /// answers for — the editor listing workspaces let it past and the window went with it.
    [Fact]
    public void AManifestThatIsNotJsonIsSaidToBeUnreadableRatherThanThrownRaw()
    {
        var refused = Assert.Throws<InvalidDataException>(
            () => PackManifest.Parse("{ \"name\": \"half a manifest\""));

        Assert.Contains("not readable", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkspaceWithAnUnreadableManifestIsSkippedRatherThanStoppingTheScan()
    {
        var directory = MakeWorkspace("w9", ("textures/a.png", "original"));
        File.WriteAllText(Path.Combine(directory, PackManifest.FileName), "{ oh dear");

        Assert.Null(WorkspaceView.Open(directory));
        Assert.Contains(directory, WorkspaceView.Discover(_root));
    }

    [Fact]
    public void EveryImageInTheWorkspaceIsOfferedAsAnIcon()
    {
        var directory = MakeWorkspace("0016_Beretta",
            ("icon/big.png", "one"), ("textures/map.png", "two"), ("meshes/gun.glb", "three"));

        Assert.Equal(["icon/big.png", "textures/map.png"], Workspace.Pictures(directory));
    }

    /// What a mesh wears is recorded per submesh, in the renderer's order.
    ///
    /// A submesh is what decides which material draws a triangle: 140 of the game's 3,245 item
    /// meshes wear more than one picture, and #145's one mesh is the gun in submesh 0 and its
    /// flashlight in submesh 1. Flattened to the set of names that happened to be written out —
    /// which is what this did — it was enough to say which pictures a model wears and not enough
    /// to put them on it, and the editor drew the flashlight in the gun's paint.
    [Fact]
    public void WhatAMeshWearsIsRecordedSubmeshBySubmesh()
    {
        var directory = Path.Combine(_root, "0145_mp5_gold_gift");
        Directory.CreateDirectory(Path.Combine(directory, "textures"));
        Directory.CreateDirectory(Path.Combine(directory, "meshes"));

        // With path ids, which is what pairs a written file with the texture a renderer named.
        ExportedAsset Written(string relative, AssetsTools.NET.Extra.AssetClassID cls, string name, long id)
        {
            var full = Path.Combine(directory, relative);
            File.WriteAllText(full, name);
            return new ExportedAsset(full, cls, name, Path.GetExtension(relative).TrimStart('.'),
                name.Length, new AssetAddress("bhlw", cls.ToString(), name, PathId: id));
        }

        var gold = Written("textures/mp5Gold_map.png", AssetsTools.NET.Extra.AssetClassID.Texture2D, "mp5Gold_map", 11);
        var light = Written("textures/mp5Light_map.png", AssetsTools.NET.Extra.AssetClassID.Texture2D, "mp5Light_map", 22);
        var mesh = Written("meshes/mp5_gold_mesh.glb", AssetsTools.NET.Extra.AssetClassID.Mesh, "mp5_gold_mesh", 33);

        // The middle one is a picture the game paints this model with and the export did not write
        // out — the base weapon's, in a workspace made from one of its skins. It keeps its place.
        var elsewhere = new AssetAddress("bhlw", "Texture2D", "not_written_here", PathId: 44);

        Workspace.Create(directory, "w", "w", "tester", "26.11.0",
            [gold, light, mesh with { Wears = [gold.Address, elsewhere, light.Address] }]);

        var wears = WorkspaceView.Open(directory)!.Files.Single(f => f.Name == "mp5_gold_mesh.glb").Wears;

        Assert.Equal(["textures/mp5Gold_map.png", "", "textures/mp5Light_map.png"], wears);
    }
}
