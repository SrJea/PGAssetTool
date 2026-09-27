using PGAssetTool.Core.Assets;

namespace PGAssetTool.Core.Tests;

/// Matching an asset path out of the game's lookup table against a bundle's own table of what it
/// holds. The paths here are the game's, read off four bundles.
public class BundleContentsTests
{
    /// The shape every one of them has: the folder the game's authors kept it in, then the path the
    /// lookup table registers, then the file's own extension, all in lower case.
    [Theory]
    [InlineData("assets/editor/resources/weaponchaticons/weapon25_chaticon.png", "WeaponChatIcons/Weapon25_chaticon")]
    [InlineData("assets/editor/resources/offericons/beretta_icon1_big.png", "OfferIcons/Beretta_icon1_big")]
    [InlineData("assets/editor/resources/weapons/weapon25.prefab", "Weapons/Weapon25")]
    public void TheGamesOwnPathsMatch(string entry, string asked)
        => Assert.True(BundleContents.Names(entry, asked));

    /// The namespace is part of the answer. Without it, a weapon's chat icon would be answered by
    /// whatever else in the bundle was filed under the same name in another folder.
    [Fact]
    public void ThePathHasToMatchAndNotOnlyTheName()
        => Assert.False(BundleContents.Names(
            "assets/editor/resources/weaponprofiles/weapon25_chaticon.png", "WeaponChatIcons/Weapon25_chaticon"));

    /// And it has to end where a folder does: `Weapon25` must not be answered by `Weapon250`.
    [Theory]
    [InlineData("assets/editor/resources/weapons/weapon250.prefab")]
    [InlineData("assets/editor/resources/xweapons/weapon25.prefab")]
    public void ANameThatMerelyEndsTheSameWayIsNotIt(string entry)
        => Assert.False(BundleContents.Names(entry, "Weapons/Weapon25"));

    /// Case is the game's own inconsistency: the lookup table spells icons `_Icon1_big` for two
    /// dozen weapons and the container table is lower case throughout.
    [Fact]
    public void CaseIsNotPartOfIt()
        => Assert.True(BundleContents.Names(
            "assets/editor/resources/offericons/dragongun_icon1_big.png", "OfferIcons/DragonGun_Icon1_big"));

    /// Only the last one. A dot in a folder name is not an extension, and an asset registered
    /// without one is registered as it is.
    [Theory]
    [InlineData("assets/v1.2/resources/weapons/weapon25", "Weapons/Weapon25")]
    [InlineData("assets/editor/resources/weapons/weapon25", "Weapons/Weapon25")]
    public void TheExtensionIsTakenOffTheNameAndNowhereElse(string entry, string asked)
        => Assert.True(BundleContents.Names(entry, asked));

    /// An entry that is exactly what was asked for, which is what a bundle holding its assets at
    /// the top would look like.
    [Fact]
    public void ThePathCanBeTheWholeEntry()
        => Assert.True(BundleContents.Names("WeaponChatIcons/Weapon25_chaticon.png", "WeaponChatIcons/Weapon25_chaticon"));

    /// Nothing is not a path, and must not be answered by the first entry in the table.
    [Fact]
    public void NothingMatchesNothing()
        => Assert.False(BundleContents.Names("assets/editor/resources/weapons/weapon25.prefab", ""));
}
