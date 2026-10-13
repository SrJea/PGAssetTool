using AssetsTools.NET.Extra;
using PGAssetTool.Core.Pack;

namespace PGAssetTool.Core.Tests;

/// The list is consulted when building a manifest, when building a pack, when applying an operation,
/// and when deciding what a tree shows. These pin it as one list rather than several that drift.
public class ReplaceableTests
{
    [Theory]
    [InlineData("png", PackOperations.ReplaceTexture)]
    [InlineData("PNG", PackOperations.ReplaceTexture)]
    [InlineData("glb", PackOperations.ReplaceMesh)]
    [InlineData("wav", PackOperations.ReplaceAudio)]
    [InlineData("mp3", PackOperations.ReplaceAudio)]
    [InlineData("ogg", PackOperations.ReplaceAudio)]
    public void EachAuthoringFormatNamesTheOperationThatTakesIt(string format, string expected)
        => Assert.Equal(expected, Replaceable.OperationForFormat(format));

    [Fact]
    public void AFormatWithNoImportPathIsNotOfferedAtAll()
    {
        // A workspace listing one of these would produce a pack that fails when applied. Raw bytes
        // went back once; they do not now.
        Assert.Null(Replaceable.OperationForFormat("json"));
        Assert.Null(Replaceable.OperationForFormat("dat"));
        Assert.Null(Replaceable.OperationForFormat(""));
    }

    [Fact]
    public void TheClassesMatchTheFormats()
    {
        Assert.True(Replaceable.Supports(AssetClassID.Texture2D));
        Assert.True(Replaceable.Supports(AssetClassID.Mesh));
        Assert.True(Replaceable.Supports(AssetClassID.AudioClip));

        Assert.False(Replaceable.Supports(AssetClassID.Shader));
        Assert.False(Replaceable.Supports(AssetClassID.MonoBehaviour));
        Assert.False(Replaceable.Supports(AssetClassID.Material));
        Assert.False(Replaceable.Supports(AssetClassID.Font));
    }

    [Fact]
    public void EveryOperationTheApplierKnowsIsReachableFromSomeFormat()
    {
        // If an operation exists with no format mapping to it, nothing can ever produce one.
        var reachable = new[] { "png", "glb", "wav", "mp3", "ogg", "anim" }
            .Select(Replaceable.OperationForFormat)
            .ToHashSet();

        Assert.Contains(PackOperations.ReplaceTexture, reachable);
        Assert.Contains(PackOperations.ReplaceMesh, reachable);
        Assert.Contains(PackOperations.ReplaceAudio, reachable);
        Assert.Contains(PackOperations.ReplaceAnimation, reachable);
        Assert.Equal(Replaceable.Classes.Count, reachable.Count);
    }

    [Fact]
    public void AComponentIsNotListed()
    {
        // Weaker than refusing to write one, and known to be: anything that opens the bundles shows
        // the same thing. What it buys is that this tool is not where somebody first meets the idea.
        Assert.False(Replaceable.CanShow(AssetClassID.MonoBehaviour));
        Assert.False(Replaceable.CanShow(AssetClassID.MonoScript));

        // Everything else is listed, including the classes that cannot be replaced. Not being
        // replaceable is a different thing from not being shown.
        foreach (var cls in new[]
                 {
                     AssetClassID.Texture2D, AssetClassID.Mesh, AssetClassID.AudioClip,
                     AssetClassID.Material, AssetClassID.Shader, AssetClassID.Font,
                     AssetClassID.GameObject, AssetClassID.Transform, AssetClassID.AnimationClip,
                 })
            Assert.True(Replaceable.CanShow(cls), $"{cls} should still be listed");
    }

    [Fact]
    public void TheRefusalSaysWhichFileAndWhy()
    {
        var component = Replaceable.WhyRefused(AssetClassID.MonoBehaviour, "weapon_thing.png");
        Assert.Contains("weapon_thing.png", component);
        Assert.Contains("behave", component);

        // Anything else is refused for what it is not, rather than for being a component.
        var material = Replaceable.WhyRefused(AssetClassID.Material, "paint.png");
        Assert.Contains("paint.png", material);
        Assert.DoesNotContain("behave", material);
    }
}
