using AssetsTools.NET.Extra;

namespace PGAssetTool.Core.Pack;

/// What this tool can put back into the game, and how.
///
/// One list, because the answer is needed in several places that must not drift apart: building a
/// workspace manifest, building a pack, applying an operation, and deciding what a tree is worth
/// showing. A type gains an entry only once its import path works end to end — listing one earlier
/// would produce packs that fail when applied.
///
/// Nothing else goes back. An asset's raw serialized bytes (`.dat`) once did, for any class, along with
/// assets added to a bundle; the user retired both until there is a design worth rebuilding them on. A
/// pack is pictures, models, sounds and animations, and nothing in it has to understand a class it
/// cannot open.
public static class Replaceable
{
    private static readonly (AssetClassID Class, string Operation, string[] Formats)[] Kinds =
    [
        (AssetClassID.Texture2D, PackOperations.ReplaceTexture, ["png"]),
        (AssetClassID.Mesh, PackOperations.ReplaceMesh, ["glb"]),
        (AssetClassID.AudioClip, PackOperations.ReplaceAudio, ["wav", "mp3", "ogg"]),
        (AssetClassID.AnimationClip, PackOperations.ReplaceAnimation, ["anim"]),
    ];

    /// The classes something can be written back to.
    public static IReadOnlySet<AssetClassID> Classes { get; } = Kinds.Select(k => k.Class).ToHashSet();

    /// Classes this tool neither shows nor writes.
    ///
    /// A component is the game's own configuration of how a thing *behaves*, and its script says
    /// which configuration it is. This tool exists to change how things look and sound; behaviour
    /// is a different question with different consequences — it reaches other players, where a
    /// texture does not.
    ///
    /// Not writing them is enforced at both doors, building and applying. Applying is the one that
    /// holds, because a pack built by something else never went past building.
    ///
    /// Not showing them is a separate decision and a weaker one — anything that opens the bundles
    /// shows the same thing, and this makes no claim to prevent that. What it does is keep the tool
    /// from being where somebody first learns the idea. Nothing else in the tool is hidden, and
    /// nothing else should be: this is the exception, not a habit.
    private static readonly AssetClassID[] Withheld =
        [AssetClassID.MonoBehaviour, AssetClassID.MonoScript];

    /// Whether this tool lists an asset of this class at all — in the tree, or in anything the
    /// command line prints.
    ///
    /// References are still followed *through* these on the way to what they point at, so a texture
    /// a component names is found exactly as before. It is the row itself that is not shown.
    public static bool CanShow(AssetClassID cls) => !Withheld.Contains(cls);

    /// Why an operation naming this class is refused. Said the same way wherever it is said, so the
    /// answer does not depend on which door it was asked at.
    public static string WhyRefused(AssetClassID cls, string what)
        => Withheld.Contains(cls)
            ? $"'{what}' is a {cls}, which this tool does not write back. It changes how things look "
                + "and sound; a component decides how they behave."
            : $"'{what}' is a {cls}, and this tool writes back only textures, meshes, sounds and animations.";

    /// The operation to run for a file, by its extension without the dot.
    public static string? OperationForFormat(string format)
        => Kinds.FirstOrDefault(k => k.Formats.Contains(format, StringComparer.OrdinalIgnoreCase)).Operation;

    public static bool Supports(AssetClassID cls) => Classes.Contains(cls);
}
