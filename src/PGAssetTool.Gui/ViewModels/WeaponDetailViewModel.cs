using System.Collections.ObjectModel;
using AssetsTools.NET.Extra;
using CommunityToolkit.Mvvm.ComponentModel;
using PGAssetTool.Core.Assets;
using PGAssetTool.Core.Pack;
using PGAssetTool.Core.Weapons;

namespace PGAssetTool.Gui.ViewModels;

/// <param name="Path">
/// Which model this row came out of, so two rows standing for the same asset under two models are
/// two things to remember an angle and a texture for rather than one.
/// </param>
/// <param name="Wearing">
/// What this row's mesh is drawn with inside that model, worked out while it was being read. Null
/// for a row that is not a mesh, and for a mesh whose materials led nowhere.
/// </param>
public sealed record ModelSource(string Path, MeshTextures? Wearing);

/// One row in the tree. Only the leaves stand for something that can be looked at or replaced.
public sealed partial class TreeNode(
    string label, string? detail = null, AssetClassID? cls = null, long pathId = 0, string bundle = "",
    bool alphaIsCoverage = false) : ObservableObject
{
    public string Label { get; } = label;
    public string? Detail { get; } = detail;
    public AssetClassID? Class { get; } = cls;
    public long PathId { get; } = pathId;

    /// Which bundle the object lives in. A weapon spans several, so the prefab's bundle is not
    /// enough to find one of its textures again.
    public string Bundle { get; } = bundle;

    /// Whether this object's alpha channel means transparency. Icons sit on an empty background
    /// and need it; a model texture usually keeps something else there and is holed by it.
    public bool AlphaIsCoverage { get; } = alphaIsCoverage;

    public ObservableCollection<TreeNode> Children { get; } = [];

    /// Two-way bound to the row, so clicking one and expanding it are the same gesture.
    [ObservableProperty] private bool _isExpanded;

    /// The skin this row stands for, when it stands for one.
    ///
    /// Opening such a row shows the weapon wearing it, which needs the skin's own materials — and
    /// for the ones that bring a model, the place to read that model from.
    public WeaponSkinView? Skin { get; init; }

    /// A model this row stands for whose contents have not been read yet.
    ///
    /// A skin that brings its own model is recorded as a place rather than as what is in it:
    /// reading the closure of every skin would be a bundle walk per skin every time a weapon was
    /// selected, and most of them are never opened. So it is read when somebody opens the row, and
    /// the row can be opened whether or not it has anything under it yet.
    public SkinModel? Unread => Skin?.Model;

    /// The model this row was read out of, when it is not the weapon's own.
    ///
    /// A skin's model can point straight at the weapon's own mesh rather than carrying a copy of
    /// it — #416's Ultimative Snowfall does — so the same Mesh asset stands in the tree twice,
    /// under two different sets of materials. Which of the two a row means is the row's own
    /// business and cannot be got back from a path id: asked that way, the skin's row came up
    /// wearing the weapon's paint, and opening it stopped the weapon's own row from offering the
    /// weapon's skins.
    public ModelSource? Within { get; init; }

    /// Which mesh under this row is the thing the model is of, once it has been read. A skin's
    /// model carries the player's arms the same way the weapon's does, so the same question has to
    /// be asked of it and is answered the same way. See WeaponResolver.MainMesh.
    public AssetNode? Body { get; set; }

    /// Whether the model behind this row has been asked for.
    ///
    /// Kept apart from whether the row has anything under it, which is what used to stand in for
    /// it. A skin can bring a model *and* paint the weapon, and such a row arrives with the paint
    /// already beneath it — so "it has children, it must have been read" was wrong for exactly the
    /// skins that have the most to show, and their models were never read at all.
    public bool ModelRead { get; set; }

    public bool HasChildren => Children.Count > 0 || Unread is not null;

    public string Icon => Class switch
    {
        AssetClassID.Texture2D => "🖼",
        AssetClassID.Mesh => "🧊",
        AssetClassID.AudioClip => "🔊",
        AssetClassID.Material => "🎨",
        AssetClassID.GameObject => "📦",
        // A skin that replaces the weapon is a model, not a folder of paint, and which of the two
        // a skin is decides everything about working on it.
        _ when Unread is not null => "📦",

        // What it holds, not what has been read out of it. A skin that brings its own model has
        // nothing beneath it until somebody opens the row, and drawing those two as an empty row
        // said the skins with the most in them were the ones with nothing.
        _ => HasChildren ? "📁" : "·",
    };

    public TreeNode With(TreeNode child)
    {
        Children.Add(child);
        return this;
    }
}

public sealed partial class WeaponDetailViewModel : ObservableObject
{
    public WeaponDetailViewModel(WeaponTree tree, bool replaceableOnly)
    {
        Tree = tree;
        Roots = Build(tree, replaceableOnly);
        Watch(Roots);
    }

    public WeaponTree Tree { get; }
    public ObservableCollection<TreeNode> Roots { get; }

    /// Set by the shell so a click in the tree can be turned into a preview.
    public Action<TreeNode?>? NodeSelected { get; set; }

    /// Set by the shell so opening a row whose contents have not been read can read them. The shell
    /// owns the bundles and the lock around them; this only says which row was opened.
    public Action<TreeNode>? NodeOpened { get; set; }

    /// Watches the skin rows, so opening one asks to be shown it — once.
    ///
    /// Every skin, not only the ones with a model to read: opening a skin that repaints the weapon
    /// has something to show too, and it is the same thing seen from the other side.
    private void Watch(IEnumerable<TreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Skin is not null)
            {
                var row = node;
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(TreeNode.IsExpanded)) return;

                    // Every time it is opened. Showing the weapon in this skin is worth doing
                    // again — somebody closing a row and opening it is asking to see it again —
                    // and reading the model behind it is what happens only once, guarded there.
                    if (row.IsExpanded) NodeOpened?.Invoke(row);
                };
            }

            Watch(node.Children);
        }
    }

    [ObservableProperty] private TreeNode? _selectedNode;

    partial void OnSelectedNodeChanged(TreeNode? value) => NodeSelected?.Invoke(value);

    public string Name => Tree.DisplayName;

    /// What it is, said in the terms its own kind uses. A weapon has a number a player reads off
    /// the list and a prefab name that agrees with nothing else; a hat has an id and that is all.
    public string Subtitle =>
        (Tree.Record.IsNumbered
            ? $"#{Tree.Record.GameNumber}   {Tree.Record.PrefabName}   {Tree.Record.Slug}"
            : $"{Tree.Record.Kind.Name}   {Tree.Record.Slug}")
        + (Tree.PrefabBundle is null ? "" : $"   @ {Tree.PrefabBundle}");

    private static ObservableCollection<TreeNode> Build(WeaponTree tree, bool replaceableOnly)
    {
        var roots = new ObservableCollection<TreeNode>();

        // Grouped by class rather than listed flat: a weapon prefab reaches a hundred-odd objects
        // and most of them are transforms nobody wants to scroll past. Which classes are worth
        // keeping when filtered comes from the import registry, so a type gained later shows up
        // here without this file being touched.
        var byClass = tree.PrefabAssets
            .Where(a => !replaceableOnly || Replaceable.Supports(a.Class))
            .GroupBy(a => a.Class)
            .OrderBy(g => Rank(g.Key))
            .ThenBy(g => g.Key.ToString(), StringComparer.Ordinal);

        // Closed. Selecting a weapon used to unfold it into thirty-odd rows across four groups, and
        // the thing anybody had come for — the model — is shown in the pane on its own now. What
        // the tree is for is going looking, and going looking starts from a list of what there is.
        var prefab = new TreeNode(tree.Record.PrefabName, $"{tree.PrefabAssets.Count} objects",
            AssetClassID.GameObject);

        foreach (var group in byClass)
        {
            var node = new TreeNode(group.Key.ToString(), $"{group.Count()}");
            foreach (var asset in group.OrderBy(a => a.Name, StringComparer.Ordinal))
                node.With(new TreeNode(
                    asset.Name.Length > 0 ? asset.Name : $"(unnamed {asset.PathId})",
                    Where(asset, tree) == tree.PrefabBundle ? null : Where(asset, tree),
                    asset.Class, asset.PathId, Where(asset, tree)));
            prefab.With(node);
        }
        roots.Add(prefab);

        if (tree.Icon is { } icon)
            roots.Add(new TreeNode("Icon", icon.Container, AssetClassID.Texture2D)
                .With(new TreeNode(icon.TextureName, icon.AssetPath, AssetClassID.Texture2D, 0,
                    icon.Container, alphaIsCoverage: true)));

        if (tree.Skins.Count > 0)
        {
            // A skin is a set of materials, and what a modder wants from it is the textures they
            // use. The materials themselves are a step on the way and stay collapsed.
            var skins = new TreeNode("Skins", $"{tree.Skins.Count}");
            foreach (var skin in tree.Skins)
            {
                // A skin that brings its own model gets a row for it, read when it is opened. Its
                // own materials do not resolve against the usual roots — the model carries what it
                // needs — so without this there was nothing of such a skin to look at at all.
                var node = new TreeNode(skin.DisplayName ?? skin.Record.Id, skin.Record.Id)
                {
                    Skin = skin,
                };

                foreach (var material in skin.Materials)
                {
                    // With one material there is nothing to distinguish, so its textures hang
                    // straight off the skin rather than behind a row that says nothing.
                    var into = node;
                    if (skin.Materials.Count > 1)
                    {
                        into = new TreeNode(material.Name, material.Bundle, AssetClassID.Material);
                        node.With(into);
                    }

                    foreach (var texture in material.Textures)
                        into.With(new TreeNode(texture.Name, material.Bundle, AssetClassID.Texture2D,
                            texture.PathId, material.Locate(texture).Bundle));
                }
                skins.With(node);
            }
            roots.Add(skins);
        }

        // Filtered, this keeps the ones that resolved to something replaceable and drops the rest.
        // The whole group used to go, on the strength of these being paths rather than objects —
        // but a chat icon and a skin's shop icon are textures the extract writes out and a pack
        // replaces, so what the filter promises to show was leaving out two of them per weapon.
        var listed = replaceableOnly
            ? tree.Related.Where(r => r.Asset is not null).ToList()
            : tree.Related;

        if (listed.Count > 0)
        {
            var related = new TreeNode("Related", $"{listed.Count}");
            foreach (var group in listed.GroupBy(r => r.Namespace).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var space = new TreeNode(group.Key, $"{group.Count()}");
                foreach (var asset in group)
                    // Named by its path, which is what says which weapon's icon this is; the row is
                    // the object itself where there is one behind it, so it can be looked at.
                    // A picture reached this way is one of the game's flat ones — a shop icon, a
                    // chat icon — so its alpha means transparency, as the weapon's own icon's does.
                    space.With(asset.Asset is { } resolved
                        ? new TreeNode(asset.Path, asset.Bundle, resolved.Class, resolved.PathId, resolved.Bundle,
                            alphaIsCoverage: resolved.Class == AssetClassID.Texture2D)
                        : new TreeNode(asset.Path, asset.Bundle));
                related.With(space);
            }
            roots.Add(related);
        }

        return roots;
    }

    /// An object reached across a bundle boundary belongs to whichever bundle it was found in.
    private static string Where(AssetNode asset, WeaponTree tree)
        => asset.Bundle.Length > 0 ? asset.Bundle : tree.PrefabBundle ?? "";

    /// What a modder came to find goes at the top; the scaffolding sinks.
    private static int Rank(AssetClassID cls) => cls switch
    {
        AssetClassID.Texture2D => 0,
        AssetClassID.Mesh => 1,
        AssetClassID.AudioClip => 2,
        AssetClassID.Material => 3,
        AssetClassID.GameObject => 4,
        _ => 5,
    };
}
