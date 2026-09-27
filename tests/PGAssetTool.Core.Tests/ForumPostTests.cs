using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Tests;

public class ForumPostTests
{
    private static PackManifest Manifest(string variant = "", string variantName = "") => new()
    {
        Id = "ultimatum-1a2b3c4d",
        Name = "Nuclear Glow",
        Author = "mof",
        Version = "1.1.0",
        Description = "Brighter core.",
        BuiltAgainstGameVersion = "26.10.2",
        Subject = new PackSubject
        {
            Kind = PackKind.Weapon, Id = "ultimatum", Number = 416, Prefab = "Weapon416",
            Variant = variant, VariantName = variantName,
        },
    };

    [Fact]
    public void ReadsTheWayTheForumAskedFor()
    {
        var post = ForumPost.For(
            Manifest("Weapon416_nuclear_reactor", "Nuclear Reactor"),
            [PackOperations.ReplaceAudio, PackOperations.ReplaceTexture, PackOperations.ReplaceTexture],
            "26.11.0", "0.1.0");

        Assert.Equal(
            "Nuclear Glow / mof / v1.1.0\n"
            + "Brighter core.\n"
            + "Skin: Nuclear Reactor\n"
            + "Changes: 2 textures, 1 sound\n"
            + "GameVersion: 26.11.0\n"
            + "ToolVersion: 0.1.0",
            post);
    }

    [Fact]
    public void TheWeaponAsItComesIsTheDefaultSkin()
        => Assert.Contains("\nSkin: Default\n", ForumPost.For(Manifest(), [], null, "0.1.0"));

    [Fact]
    public void LeavesOutWhatIsNotThere()
    {
        var post = ForumPost.For(
            Manifest() with { Author = " ", Version = "v2", Description = "", Subject = null },
            [PackOperations.ReplaceMesh], null, "0.1.0");

        Assert.Equal(
            "Nuclear Glow / v2\n"
            + "Changes: 1 model\n"
            + "GameVersion: 26.10.2\n"
            + "ToolVersion: 0.1.0",
            post);
    }

    [Fact]
    public void NothingEditedSaysSo()
        => Assert.Equal("nothing yet", ForumPost.Changes([]));

    [Fact]
    public void TheToolVersionIsTheProjectsWithoutTheCommit()
        => Assert.Equal("1.0.5", ForumPost.ToolVersion);
}
