# How PGAssetTool works

This is the reasoning behind the tool, for anyone changing it. If you only want to use it, the
[README](README.md) is the whole of what you need.

Everything here is written because it was not obvious, or because it was got wrong once. The code
carries the same explanations at the things they explain; this is the shape they make together.

---

## What makes a game's item hard to find

An item's parts are spread across hundreds of bundles. One weapon's model, materials, icon, skins,
effects and display name each live somewhere different, and they are joined by three different
mechanisms rather than one:

| Link | Example |
| --- | --- |
| Binary `PPtr` references | `Material` → `Texture2D` |
| String paths resolved at runtime | a field holding `"Weapons/Weapon25"` |
| Naming convention | `Weapon687` ↔ `Ray687` |

Following only `PPtr` references leaves the tree disconnected: the model appears without its icon,
the skins without their materials. The tool follows all three, which is why a weapon comes out whole.

The same three joins hold for the game's other items. A hat has a prefab, an offer icon, related
assets and skins exactly as a weapon does; it is found by a string id where a weapon is found by a
number. Only the finding differs, which is why the code that walks and writes knows nothing about
weapons at all.

---

## The three layers

**Reading and writing assets** knows nothing about the game. `BundleSet`, `ReferenceWalker`,
`AssetExporter`, `AssetAddress`, `PackOperation`, `PackBuilder`, `PackFile`, `ModApplier`,
`ModStore`, the importers — all of it addresses a container, a class, a name and a path id. Nothing
in it has a concept of a weapon.

**The catalogues** are the part that is game-shaped: `ItemCatalog` reads the weapon registry,
`SkinCatalog` assembles skins from three bundles, `AssetLookup` maps asset paths to the bundles
holding them, `IconResolver` finds the one picture that stands for an item. This layer answers
"what exists, and where".

**The shell** — the GUI and the CLI — puts the two together. Both offer every kind; the picker above the
window's list chooses which registry the list is filled from, and nothing below it changes.

Adding another kind of item is work in the middle layer. The layers below and above it already do
what they would need to do.

---

## Addressing an asset

A pack has to name assets in somebody else's installation, where path ids are not stable across
game versions. `AssetAddress` is therefore a container, a class, a name and an ordinal, with the
path id recorded as a preference rather than as the address.

Applying resolves the address in the player's own bundles. The path id is tried first, because it
makes a pack match what its author tested; when it names the wrong thing or nothing at all, the
name and class find it instead.

`AssetNaming` exists because a `Shader` keeps its name in `m_ParsedForm` rather than at the top of
the object. Every shader in the game has an empty `m_Name`, so anything reading the obvious field
finds nothing.

### A path out of the lookup table becomes an object through the bundle's own table

The third join gives paths, not objects: `WeaponChatIcons/Weapon25_chaticon` is registered against a
bundle and nothing more. Every bundle carries an `AssetBundle` object whose `m_Container` is the map
the game itself loads by — that path, written as the folder it was authored in plus an extension and
folded to lower case — so `BundleContents` turns one into a path id, and thereby into a class,
without reading anything else in the bundle.

That is why it is not a search by name. A search reads the name of every object that might match,
a name is not unique inside a bundle, and it answers whatever else happened to share the name: asked
for a skin's definition, it came back with a texture called the same thing. Over 60 weapons' 237
related assets the container table resolved every one, at 1.6ms a weapon against 14ms for the search.

The class is what tells a picture from a piece of configuration, and so what lets the tree keep a
chat icon and a skin's shop icon when it is filtered to what can be replaced. Before this the whole
group went, on the strength of their being paths rather than objects — while an extract wrote them
out and a pack replaced them.

The item's own prefab is found the same way, and a search by name is only the fallback. A pet's
bundle holds `Pets/Content/pet_alien_cat`, the model, and `Pets/Infos/pet_alien_cat`, a description of
it, and both GameObjects are called `pet_alien_cat`; searched for by name, 50 of the 112 pets resolved
to the description — a GameObject and a Transform, no model — and three weapons and eighteen vehicles
to a part of themselves that reached less than the whole.

### An item's related paths are the ones no other item claims

Everything but a weapon finds its related assets by its id appearing in their names, ending where the
name does or at an underscore. Nine ids in the game are another item's id and more — `pet_horse` and
`pet_horse_figure`, `glider_dragon` and `glider_dragon_form`, `avatar_spec_ops` and three of its squad —
and by that rule the shorter one collected the longer one's prefab and icons, which an extract wrote
out and a pack could then replace. So a path a longer id also names is that item's.

---

## What can be written back

| Operation | Source | Applies to |
| --- | --- | --- |
| `replaceTexture` | `.png` | `Texture2D` |
| `replaceMesh` | `.glb` | `Mesh` |
| `replaceAudio` | `.wav`, `.mp3`, `.ogg` | `AudioClip` |
| `replaceAnimation` | `.anim` | `AnimationClip` |

Each takes a file anybody can edit in an ordinary tool. `Replaceable` is the single registry of them,
and a class earns a place in it only once the import path works end to end — listing one earlier
produces packs that fail when applied. Nothing else is written back, and nothing else is written out:
an extract holds the files these operations take, the pictures that stand for the pack, and the
`pgmod.json` that names them.

Raw serialized bytes from a `.dat`, assets added to a bundle, and JSON dumps of everything else were
once written back or out as well. They were retired — nothing read the dumps, and a folder of files
nobody can act on reads as an invitation — and the commit that removed them is where to start if any
of it returns.

### Textures come out masked to what the model shows

A weapon's texture is an atlas with a great deal of nothing in it, and which island the model reads
is written in its UVs and nowhere an image editor can see. `UvCoverage` rasterises the triangles into
the texture's own grid, and the export clears the rest — 2,561 of #16's 4,096 texels, and 1,824 of
the 2,048 in the player atlas its arms read from.

The mask is cut exactly to the triangles, because the game reads exactly what it draws: 347 of the
367 textures in the bundle holding the weapons' art are point-filtered with no mip chain, so nothing
is sampled that a triangle does not land on. `UvCoverage.MarginFor` reaches one texel further for the
handful that are filtered or mipped. A flat two-texel margin — the first guess — claimed 65% of #16's
atlas where its model reads 37%, and the difference is visible against the same UVs opened in
Blender.

The alpha channel carries that mask outright rather than the original's. It has to: most of these
textures keep emission there rather than coverage, and `Map_Beretta_A` is 99.8% transparent before
anything is done to it, so a masked export that kept the original alpha would be as invisible as the
one it came from. The colours outside the mask are left exactly as they were, so nothing is painted
over and nothing is lost.

What the game had in that channel is not lost either. The operation records `AlphaIsMask`, and
applying one of those keeps the alpha already in the game — the same promise `--opaque` makes, by
the same route. Whoever drops that flag puts out the lights on every emissive part of every masked
texture, and nothing else would say so.

A texture no model here draws is written whole: a shop icon, a gloss or mask map bound beside the
main slot, a particle sheet. Clearing those would mean guessing which mesh reads them.

A texel is kept once a triangle covers a hundredth of it, and nothing more is asked. A texel a
triangle only clips looks from Blender like one that does not belong, but how much of it is covered
says nothing about whether the game draws it: a sliver of UV stretched over a large face lands on a
great many pixels. Cleared one at a time and rendered from sixty angles, 55 of #64's 60 thinnest kept
texels were drawn, and #416 has one covered 1.9% that lands on nearly forty thousand pixels — while
texels covered 18% turned up that nothing draws at all. No threshold separates the two.

### A model plays its own animations

The game's weapons are one skinned mesh on three or four bones — the slide, the magazine, the hands —
and their clips are legacy curves bound to those bones by the path from whatever carries the
Animation component. So a reload can be shown without a scene and without a second mesh: `Motion`
reads the curves, `Skeleton` walks the hierarchy from the renderer's `m_Bones`, and the vertices
follow. Twelve of twelve weapons sampled animate, along with half the capes, most gliders and half
the pets; hats, masks and boots are rigid and have no bones at all, and avatars are on bones whose
clips are not in their own prefab.

A skin that brings a model is a prefab of its own, and what moves it is the clips of the model the row
was reached through, not the weapon's. Of 130 skin models, 77 are moved only by their own clips.
Eleven carry none: seven of those are built on the weapon's rig under a name of their own
(`Weapon893_deepwater_comet/FPS_PLAYER_Arm_Right/...` where the weapon's clip says
`comet_sniper_rifle/FPS_PLAYER_Arm_Right/...`), so they are played the weapon's clips with that first
name taken off (`Motion.WithoutRoot`); the other four are rigged unlike the weapon and move only as far
as the arm does. A plain mesh that a clip moves by its object's name — #8's Old Combat Knife skin —
has one bone, its own transform (`Skeleton.Rigid`).

Four things about it that look arbitrary:

- **The space comes from the model at rest, not from the renderer's transform.** A bind pose is
  written against whatever the model was parented to when it was rigged, and the prefab no longer
  says what that was — the beretta's bind poses put its bones at the origin where its prefab hangs
  them a metre and a half away. At rest every bone's skinning matrix is the same one, so its inverse
  is by construction the transform that leaves the mesh where it was read.
- **A missing weight channel means one bone at full weight, not no bones.** These weapons are rigged
  one bone per vertex, so Unity writes no weights at all; reading that as zero left every vertex
  where it started, which looks exactly like an animation that does nothing.
- **The view is framed by the model at rest.** A pistol with its magazine out is a taller model than
  the same pistol at rest, and a frame sized to the moment slides about for the length of the clip.
- **It plays on the display's own frames, by the time that really passed between them.** A timer at
  thirty a second stepping a fixed thirtieth showed something new one frame in four on a 120Hz
  screen, and a slow frame put the clip behind the clock for good.

### An animation goes back as its curves

Every one of the 8,926 clips the weapons play is a legacy clip: position, rotation and scale curves
bound by path, 213 of them with their rotations packed into `m_CompressedRotationCurves`
(`PackedCurve` reads those), 33 with float curves and eight with events. So a workspace holds each
clip as a `.anim` — the curves and nothing else, as JSON a person can read (`ClipFile`) — and
`replaceAnimation` writes them back (`ClipImporter`): the three curve arrays rebuilt, the packed and
Euler ones emptied, and everything that is not motion left as the game had it. The clip's name is
what the game plays it by, and its events call into the game's code.

**A clip always keeps the length the game shipped it with.** The game times a weapon by its clips —
how long a shot takes, how long a reload takes (`shotDelayForAnimation`, `GetReloadAnimationSpeed`
in its metadata) — so another length is another fire rate: #2's 0.67s shot in #416's 2.60s one made
#416 fire four times as often. That is a cheat whether meant or not. So `ClipImporter.Fit` stretches
or squeezes whatever motion comes in to end where the game's clip ends — times scaled, slopes by the
inverse, the same curve at another speed — and a legacy clip's length is its last key. It is done on
the way into the bundle, so it holds for every pack, including ones built before it; the workspace
is fitted too when an animation is put in, so the preview shows what the game will play.

What goes in is one of two things.

**Another item's clip** (`AnimationSwap.Use`). A clip names what it moves by path from its Animation
component, and every weapon's paths start with a name of its own, so `Retarget` matches them in
turns: the same path; the same last name agreeing longest from the end, when only one does; what
those matches say about the names in front; and an only child of something matched, one level down
and only when the other clip's own model is known — that last is the gun's root under the hand,
which every weapon calls something different. What is still unmatched is left out and said.

**A glTF** (`AnimationGlb`), which is how Blender gets a say. Written out, it holds every object
under the Animation component as a joint of one skin, the workspace's models bound to it where they
stand at rest, and each clip as an animation of its name. Read back, the animation named like the
slot is fitted the same way. The decisions in it that look arbitrary:

- **Curves go out as straight lines, sampled on Blender's 24 frames a second.** glTF can say a
  Unity curve exactly — a cubic with the slopes either side of each key — and Blender reads that by
  keeping the keys and putting handles of its own where the slopes were. A weapon's motion is mostly
  its slopes, so what Blender showed was not what the game plays. Straight lines it reads as they
  are, and on its own frames every key lands on one.
- **A curve that comes back unchanged keeps the game's keys.** Blender writes the animation back
  sampled on the same frames, so a curve nobody touched comes back as exactly the numbers it went
  out as; checked against the slot as it is, it is kept as it is rather than as the samples. A file
  saved without an edit is the animation the slot already had, and nothing is written.
- **Every object is a joint, and every animation holds what it leaves alone at rest.** One
  armature and one action per clip, rather than loose empties with actions of their own; and a
  Blender bone cannot keep a scale at rest, so `fx_sleeve`, which the game keeps at 0.95, stood at 1
  in every action that did not say otherwise and went back out as a curve the clip never had. A
  curve that only holds an object where it already stands is left out on the way back in — unless
  the game's own clip has it, when pinning it is part of what the clip does.
- **The models are bound where they stand.** The skinning matrix at rest is the bone where it
  stands times its bind pose, whatever the bind poses were written against; the vertices are put
  there and every joint's inverse bind matrix is the inverse of where it stands, so no bind pose of
  the game's reaches another program to be got wrong. Checked vertex by vertex against Blender's own
  evaluation, on #1, #943 and #1200: the same on every frame.
- **The renderer under the animated object, where a mesh has several.** The shop's `_info` prefab
  draws the same mesh on bones of its own, and the first renderer found was that one for #1200.
- **Blender's names are read as Blender writes them.** Everything hangs under an `Armature` it adds,
  so paths are counted from the object named like the one carrying the Animation component; a name
  it has made unique with `.001` is read as the name it was; an action comes back called
  `Reload_Armature` as often as `Reload`; and an animation that starts at frame 1 starts at 0 here.

### A workspace records what each model is drawn with

`PackOperation.Wears` names, for each `.glb`, the pictures beside it that go on it. A workspace holds
a model and a folder of images and nothing that pairs them, and the pairing cannot be recovered
afterwards by asking the game:

- More than one renderer draws the same mesh, and they disagree. The tactical knife's geometry is
  drawn by its own prefab in one paint and by a skin's prefab in another, both in `ecw_8`; #64's
  mesh answers with #62's texture. The weapon tree picks the renderer inside the weapon's own prefab
  closure, which is the one that is right.
- A workspace made from a skin does not agree with the game at all. The geometry is the weapon's, so
  the renderer names the weapon's paint — and the only pictures in the workspace are the skin's.

So the answer is written down at the one moment that knows it. Where several accounts are possible,
the export keeps the one whose textures it actually wrote out. Asking the game is still the fallback
for a workspace that says nothing.

It is written **per submesh, in the renderer's own order**, with an empty entry where the picture
that submesh wears was not written here. A submesh is what decides which material draws a triangle:
140 of the game's 3,245 item meshes wear more than one picture, and #145's single mesh is the gun in
submesh 0 and its flashlight in submesh 1, painted `mp5Gold_map` and `mp5Light_map`. Recorded as the
set of names that happened to be here — which it was — it said which pictures a model wears and not
which part wears which, so the editor put one of them over the whole model and drew the flashlight
in the gun's paint.

Putting one picture over the whole model is still what the texture list in the editor does, and that
is the other question: what this geometry looks like in some other paint, which is what a skin is.

### A weapon's paint reaches its default skin

A weapon with skins has two default looks, and which one a player gets depends on their own history:
the weapon's own paint until they first change its skin, and the default skin — filed as
`<prefab>_default` — ever after. A pack that replaced only the weapon's texture showed to some
players and not to others.

The game has it both ways. #416's default skin paints with the weapon's own texture, the same asset,
and needs nothing. #507's paints with a separate asset whose pixels are identical to the weapon's.
For that case the extract writes the one picture to both addresses — one file, two operations — so
the author edits once and every player sees it. Where the two pictures really differ, both are
written out and the author is told, because painting one over the other would be a guess.

Everything keyed by file already copes with a file standing for two assets: what counts as edited,
what is packed (a source goes into the pack once), what the editor lists (a row per file). Only the
write is per address.

### The one thing that is neither

Components, and the scripts that say which component they are, are neither shown nor written.

They are how the game decides what a thing *does*. This is a tool for how things look and sound, and
behaviour is a different question with different consequences: it reaches other players, where a
texture does not.

Not writing them is enforced at building and at applying. Applying is the one that holds — a pack
built by something else never went past building and arrives anyway.

Not *showing* them is a separate and weaker decision, made deliberately and worth being honest
about: anything that opens the bundles shows the same thing, and this makes no claim to prevent
that. What it buys is that the tool is not where somebody first meets the idea. References are still
followed *through* them, so a texture or a sound a component names is found exactly as before.

Nothing else in the tool is hidden, and nothing else should be. This is the exception, not a habit.

---

## What a pack is

A `.pgmod` is a zip holding a manifest, the files it references, and the pack's picture.

Built with protection on it is a small container instead: a readable header saying who built it, and
the same zip with a keystream over it. The header stays readable so the manager can say where a pack
came from without unpacking it, and because a file that gives no account of itself is worse than one
that does.

The scrambling stops a pack being renamed to `.zip` and opened. It stops nothing else — this
repository says exactly how to undo it — and is not meant to. What it is for is that somebody's work
is not casually lifted out of the file they published.

A new pack is protected. The pack that leaves the machine that built it is what the decision is
about, and a signature added afterwards says nothing — it says who built *this* file, so it has to
be there from the build. Options turns it off for every pack and the editor for one; the CLI has no
settings, so there `--protect` is how to ask.

`PackFile` is the one place that layout is written down. Do not duplicate it anywhere else, tests
included; that has already been wrong once.

### Signing

Signing says the contents are what the holder of that key put in, so a pack altered afterwards shows
up as altered. The author's name in the header is signed along with them, length-prefixed ahead of
the payload, so it cannot be moved onto somebody else's work or off your own without the key.

A name that is not signed is only a label. An earlier shape signed the contents alone, and a genuine
pack could be relabelled and go on reading as genuinely signed — by the genuine key, with the
genuine fingerprint printed beside somebody else's name.

Signing a pack again under a different key is not prevented, because it cannot be. What it costs is
the fingerprint, which is the part worth checking: the name is what somebody typed, the fingerprint
is what they hold. The fingerprint is taken over the key as the framework re-exports it rather than
over the bytes as they arrived: `ImportSubjectPublicKeyInfo` stops at the end of the structure, so a
padded key verified exactly as before under a fingerprint of the padder's choosing.

A seal is read before a pack from outside is installed, not when somebody looks at the row
afterwards, and a seal that will not decode is `Invalid` rather than `Unsigned`. Those are two
different statements and folding them together let an alteration erase the evidence of itself: break
the signature badly enough and the pack read as an ordinary unsigned zip. Installing an altered pack
is still allowed — a pack is a description of changes and whoever holds one may install it — but the
GUI asks first and the CLI wants `--force`.

### What a pack may not do

Two rules that exist because a manifest is data, and a workspace is a folder that gets shared:

- **Every path stays inside the workspace.** `PackBuilder.Inside` resolves the icon and every
  operation's source and refuses anything absolute, drive-relative, or reaching out through `..`.
  Without it, a manifest could name any file the builder could read and it went into the pack under
  whatever name the manifest gave it. The file that made this worth fixing is `author.key`.
- **Nothing is decompressed without a ceiling.** A zip states each entry's length and then hands
  over as many bytes as it likes, so the size that counts is the one coming out — `ReadEntry` and
  `WriteEntry` count as they go. `PackFile.Most` caps the file before it is read at all, because the
  manager reads every pack it lists in order to draw its picture.

---

## What a pack is for

A pack records the item it was made from — the kind, the thing, and which of its looks:

```
weapon/0416_ultimatum/nuclear_reactor
hat/league1_hat_hitman/default
```

A mod is its operations, and this is the label on the box. Applying reads it for one thing: when an
operation's asset is not in the bundle it names, it says whose bundles to look through (see
*Installing*). Where to look, never what is found. It exists because the operations name assets and
bundles, and no amount of reading them back says
"the nuclear reactor skin of #416" — so a pack that does not carry it cannot be filed, and filing by
guessing would file some of them wrong.

It is written at extraction, which is the only moment anything knows.

The record was deliberately not weapon-shaped from the start, when weapons were the only kind
extraction made. Naming the parts generally cost nothing then, and when the other kinds arrived no
installed pack and no folder on disk had to move.

The names are recorded *and* their translation keys are. The two answer different questions: the
name is what it was called when the pack was made, which a pack handed to somebody has to be able to
say for itself; the key is what it is called now, in whatever language the tool is set to. The
manager prefers to look the thing up by number or by skin id.

### Identity

Each extraction mints its own id — a readable half saying what it is for, and eight characters that
make it this one.

The id was the item's slug and nothing else, which made every mod of a weapon the same mod:
installing a skin replaced the plain one, and installing somebody else's replaced yours. Quietly,
and reasonably, because replacing by id is exactly how a mod is updated.

The id is written into the workspace once and never changes, so rebuilding the same workspace still
updates the mod it built before — the one case where replacing was meant. A second extraction of the
same thing gets its own workspace directory too, numbering up past any that already holds a
manifest.

---

## Installing

Applying, toggling and removing all work the same way: restore every affected bundle from its
backup, then reapply everything enabled, in the order it was installed.

That is why turning one mod off cannot undo another that shares a bundle, and why nothing stacks.
`ModApplier.Reconcile` restores from every backup but skips bundles already pristine.

A bundle is backed up before it is first written to, filed under its own hash so a backup taken
before a game update stays distinguishable from the version that replaced it. The backups are the
record of what has actually been written to — the installed list cannot answer that, because
uninstalling drops the mod before its bundles are put back.

`apply` refuses a bundle that something else has already modified and has no backup, unless `--force`
says to accept its contents as the original.

The backups are also what Browse and extraction read. A reader given `ModStore.OriginalOf` opens a
bundle's original wherever one is kept, so an item is shown as the game ships it whatever is
installed over it. Reading the live bundles showed the mod, and an extract then wrote the mod's bytes
out as the weapon's own — and since a pack carries only what changed after the extract, an author
could build a pack around somebody else's installed work without either of them seeing it. Applying,
verifying and the manager read the live files, which is what they are about.

A game update replaces bundles under new hashes and the game loads those, so every mod on them is out
of the game while the ledger lists it as on. `GameUpdate` notices when the game is opened, from the
hash each mod recorded for every bundle it wrote to — kept current by every reconcile, including for a
bundle it left alone — so a moved hash is an update, and reapplying records the new one and clears it.
The version the game reports is only for saying so. Across the updates on hand, 26.10.1 and 26.10.2
share 2,164 of their 2,296 bundles with 26.11.0 and replaced 132, and all 44 operations in the 37 packs
kept here found their targets by path id in every one of them. Against 24.3.7, 1,240 of 1,538 bundles
were replaced and 20 operations name an asset the bundle does not hold there. Nineteen of those assets
were in another bundle of the same item under the same path id — the game had refiled them, ecw_6's
textures in ecw_4 and d_w's in bhlw — and seventeen were the same picture to the pixel. The twentieth
is a skin that did not exist yet.

An address only looks inside the bundle it names, so `Relocation` follows the rest. An operation
whose asset is not there — or whose bundle is gone — is looked for among the bundles the pack's
subject has in this version: its prefab and what that reaches, what its meshes are drawn with, its
skins' materials and models, and what the lookup table files under it. It must be the same asset: a
bundle holding it under the same path id, class and name, or failing that the one bundle among them
holding that class and name. Where it was found goes into the ledger (`InstalledMod.Moved`) and the
reconcile runs once more, so the order mods are applied in and what each bundle is built from are
still decided in one place; the next reconcile goes straight there. A search that found nothing is
remembered against the hash of the bundle it was missing from and not repeated until that changes,
because looking costs the catalogues and a handful of bundles — seconds — and a pack for a skin this
version does not have would otherwise pay it on every toggle.

A reconcile is organised by bundle rather than by mod: every enabled mod's operations for one bundle
are gathered first, and the bundle is opened, edited and written once. Going mod by mod meant
rebuilding a bundle once per mod on it, and a rebuild is a decompress and a recompress — thirty-one
mods on seventeen bundles took thirty-eight seconds for forty operations.

Bundles that already hold what this run would put into them are left where they are. `written.json`
records, per bundle, the mods and packs it was built from, which of their operations land in it, how
it was compressed, and the hash of the file that came out; a
bundle whose recipe is unchanged and whose file still hashes to what was written is neither restored
nor rebuilt. Both halves have to hold, and anything else — a game update, an edit from outside, a
record from a run that did not finish — falls through to restoring and reapplying. `ModApplier.Rebuild`
turns the shortcut off, which is what the manager's *Reapply everything* is for. Turning one mod off
then costs the bundles that mod is on rather than every bundle any mod is on.

A bundle is written down only when every operation meant for it went in. The recipe is what was asked
for, not what came out, so a bundle where one operation failed was recorded as holding that one too —
and once whatever stopped it was put right, every later reconcile left the bundle alone as already
correct. A glider's mesh the importer could not read stayed out of the game after the importer learned
to read it.

The ones that are rebuilt are rebuilt side by side, four at a time, largest first. They share
nothing: different files, different backups, their own corner of the staging directory. The limit is
memory rather than cores — a bundle holds its whole decompressed self while it is worked on, and the
largest in this game is 216MB — and each one already spreads its compression across every core.
One at a time is offered for a machine short of memory (`ModApplier.AtOnce`): rebuilding the eight
largest bundles in the game took 2.9 seconds with a 1.25GB peak four at a time, and 3.9 seconds with
800MB one at a time. What is written is the same either way.
A pack is opened once for the whole run and read under a lock, because several bundles may be drawing
from it at the same time.

The edited bundle's entries are laid end to end straight into the buffer its blocks are cut from.
Writing the whole bundle out uncompressed into memory and reading that back to pack it held the
bundle five or six times over; dropping it took a five-pack install's peak from 950MB to 510MB.

Two things were tried here and left out. Decompressing a bundle whole before editing it, rather than
letting the library decode blocks as they are read, saved about a fifth of the time on d_c_f and
doubled the peak, before four are rebuilt at once. And writing bundles uncompressed was no quicker than
`Faster` for bundles two to three times the size.

Writing one back out is `BundlePacker`, not the library's `Pack`. AssetsTools.NET's LZ4 is LZ4HC
through a managed port that manages about 28MB/s, and it was seventeen of those nineteen remaining
seconds; the blocks are independent, so they are compressed here, on every core. `BundlePacking`
chooses between the size the game ships and about four times the speed for about a sixth more disk.
The choice is part of what a bundle is recorded as built from, because the same mods packed the two
ways are two different files: left out, a bundle applied for speed was left alone by a reconcile
asked for size, and rebuilding everything then arrived somewhere else. So changing it rewrites every
bundle a mod is in at the next change, once.

The layout written is the one AssetsTools.NET wrote — 128KB blocks, the block and directory table
compressed at the end — which is not the layout the game ships and is the one this tool has always
written into it. Each bundle is rebuilt beside the file it replaces and renamed over it, so the last
step is a rename rather than a copy across drives.

### Where things are kept

Everything the tool writes lives in `PGAssetTool-data/`, beside the executable. No registry, no
`%APPDATA%`. Inside a checkout it resolves to the repository root, so every build shares one store.

```
PGAssetTool-data/
  settings.json          preferences
  author.key             the key packs are signed with
  mods/<kind>/<item>/<look>/   packs kept so a mod can be reinstalled without its workspace
  workspace/             extracted workspaces
  installs/<game>/
    backup/              each bundle as it was before the first write to it
    installed.json       the ledger
    written.json         what was last written into each bundle, so a reconcile can skip it
```

The arrangement under `mods/` is the one the manager shows. Two views of the same thing that
disagree are worse than either.

---

## Things that look arbitrary and are not

**`BundleSet` opens each bundle once.** Asking twice leaves a handle that disposing does not close,
and the next write to that bundle fails with the file in use.

**Names are read off the file, not off the object.** `AssetsContext.NameOf` steps over whatever
sits ahead of `m_Name` — nothing for most classes, two pointers and a flag for a MonoBehaviour, a
count of component pointers for a GameObject — and reads the string. Finding a texture by name had
meant deserializing every object in its bundle, pixels and all: extracting #16 allocated 17GB to write
fifteen files, and took 7.4 seconds where it now takes about one. Checked against the whole read for
all 1,627,889 objects in the game's bundles, none differ. A Shader keeps its name elsewhere and is read
whole, as is anything whose name sits behind a field only reading it would size.

**AssetsTools' quick lookup and template cache are turned on.** Both are off unless asked for:
without the first, finding an object by path id walks the file's whole table.

**Bundles are unpacked into memory, up to a limit.** The library reads an LZ4 bundle a block at a time
as bytes are asked for, and a reader jumps about — a name, a parent transform, a material — so the
same 128KB blocks were decoded again and again. `BundleUnpacker` decodes a bundle whole, on every
core, and hands the library an uncompressed copy: resolving #16 went from 110ms to 13ms, finding
which way it faces from 270ms to 13ms, extracting it from 1.0s to 0.4s. What it costs is memory, so
`BundleSet.UnpackBudget` caps it — 1GB unless Options says otherwise — and bundles beyond it are read
from disk as before. An unpacked bundle holds no file open, which also means browsing it never
stands in the way of a write. Applying keeps reading from disk (see *Installing*).

**`MeshRenderer` fills rows in parallel bands, and scans each row wider than the triangle.** One
thread over every pixel was 75–90ms a frame at 2400x1500 with a model filling half the pane. Bands
share no pixels and keep the triangles in their order, so the picture is the same to the byte — 162
sampled frames hashed identical. The span a row is scanned over comes from two edges in double
precision and is widened past what float arithmetic can be out by, because the per-pixel test that
decides is still the old float one; a tight span would disagree with it at the edges.

**`MeshRenderer` keeps the larger Z** in its depth test: forward points at the viewer.
**Screen-right is the negative of the frame's own right axis**, applied after the roll — the viewer
stands on the far side of the model from Unity's own camera, so what that camera has on its right is
on their left. Getting this wrong draws every model mirrored, which is invisible on a gun and
obvious the moment a texture has writing on it.

**A mesh's normals are three wide even when the game keeps four.** Unity pads a half-float normal to
four components so the attribute fills a whole four-byte step, and 55 main meshes are kept that way.
`UnityMesh` narrows position and normal to three as it reads them, because everything downstream
takes three a vertex; left at four, #14 Battle Shovel and King's Crown shaded in alternating light and
dark triangles, and the shovel was wrongly taken for a model whose normals disagree with its winding.

**A `.glb` is written with its submeshes sharing one set of vertices, and read back the same way.**
That is a Unity mesh: one vertex buffer, an index range per submesh. Read back a primitive at a time,
each submesh brought its own copy of the vertices — #1200's Space Dessert, two submeshes over 2,779
vertices, went into the game with 5,558 — so primitives naming the same accessors share them. Blender
writes a set per primitive and comes through as it always did.

**A value in a mesh that is not a number is written as one that is.** The game ships a few — two of
avatar_gunslinger's normals are NaN — and the JSON half of a `.glb` cannot hold one, so the whole item
failed to extract. A normal or tangent comes out as a unit vector, anything else as zero. Only POSITION
is given bounds, as glTF asks; bounding every three-wide accessor is how the NaN reached the JSON.

**The camera holds one rotation, not a yaw, a pitch and a roll.** It held three angles for a long
time and that made it a turntable: sideways turned the model about one axis of its own whatever the
picture was tilted to, so a tilted view dragged about an axis that no longer looked vertical, and
the pitch had to stop at the poles because past them a drag to the right walked the viewer left.
Every one of those is a property of keeping the rotation as three numbers in a fixed order. The
turntable, an arcball and a trackball were built side by side and driven against the same models,
and the trackball was kept: a drag turns the model about the axis lying across it on the screen, so
there is no axis of the model to become unstable and no pole to collapse at. What it gives up is a
level horizon — the named views are how a model is put back square. `Camera.Facing` still takes a
yaw, a pitch and a roll, built through the old frame so every angle written down means what it did.

**A drag's turn goes on the left of the rotation, with its axis turned over in y and z.** On the
left because a drag is a movement of the picture, not of the model; turned over because the frame
is drawn mirrored in x, and conjugating a turn by that mirror is exactly the sign change. Without it
a drag downwards tips the model the wrong way and a tilt winds backwards.

**The pivot is a point of the model rather than an offset in the frame**, so panning something into
view and then turning keeps it in view.

**The preview opens tilted.** The default camera is the angle the game draws its own `icon1_big`
shop pictures at — muzzle up and to the right, nearly side-on — so an author meets the shape they
already know. It was measured, not chosen: fitting silhouettes to the icons stalled at about 0.8
because the icons carry a drawn outline, so 55 weapons were matched by hand and read back against
each candidate frame. Against the faced bounding box the 39 that fire agree to within 5 degrees;
blades scatter over 35 around the same middle, which is no convention rather than a second one.

**A pack's picture is framed from the model's vertices, not from a drawing of it.** A drawing is
clipped at the frame, so a model three times too wide reads as one that fits exactly.

**Which end a weapon's barrel is on comes from the prefab, not from the model's shape.** The
bounding box says which side is longest and nothing about which end is which, so half the weapons
opened pointing left. Guessing from the geometry does not work: four signals measured against 157
weapons labelled by hand all read the same thing — that one end carries more of the model — and the
best, counting vertices, called 74.5%. The prefab knows outright: `GunFlash`, `BulletSpawnPoint` and
`Point_Arm_Left` are where the muzzle and the hand are, and the first of them present is right for
150 of the 151 weapons it answers for. `Facing` turns the model a half circle about the vertical
when the muzzle would be on the left — a half circle and not a mirror, or every texture with writing
on it reads backwards — and decides which end is which along the long axis only, because the other
two axes swing into the answer as the opening angle changes.

**`FsbAdpcm`** decodes the game's ADPCM here because Fmod5Sharp does not saturate the predictor. The
container layout was read off the data; it is not documented anywhere reachable.

**Each preview pane remembers a view per model**, and the two panes of a comparison hand theirs to
each other only once both are filled. Mirroring mid-load writes one model's angle into another's
memory.

**A pack's protection and picture are saved the moment they are chosen; its text waits for Save.**
A three-state checkbox turned protection off on its first click, and only Save made anything stick.
And because any write to a workspace makes the editor read it again, a re-read keeps text typed and
not yet saved rather than refilling the form from disk, which it used to do without a word.

**The window's busy flag and the manager's are two flags, and the status bar shows both.** The bar in
the status line is the only thing on screen that says the tool is doing something, and the manager
does its work under a flag of its own — so turning a mod on or off left the bar still for the whole
rewrite. `MainViewModel.Working` is what the bar binds.

**The whole of a window is scaled, not its font size.** `UiScale` is a `ScaleTransform` on a
`LayoutTransformControl` around each window's content, which takes part in the layout: the columns,
the paddings and the text grow together. A larger font on its own grows the words inside columns that
stayed the width they were. `MeshView` rasterises at the scale its own content actually lands on the
screen at — the transform and the display's own scaling together, read off `TransformToVisual` — so a
model is drawn once per pixel it is shown at rather than magnified afterwards. The main window's
minimum size is deliberately not scaled: at 200% it would be 1800 wide, which on a narrower screen is
a window that cannot be brought back.

---

## Verifying a change

```
dotnet test -c Release
dotnet run --project src/PGAssetTool.Gui -c Release -- --self-test
```

`dotnet test` needs no game and is what CI runs.

**`--self-test` is the real check.** It drives the view models against the live installation:
resolves weapons, decodes previews, rasterises, builds the real window and reads back what landed in
it, then extracts, edits, packs, installs, toggles and removes for real. Most of the bugs in this
codebase were found by it, or by a check added to it. Prefer adding a check there over reasoning
about whether something works.

Three things about it that must stay true:

- **It writes to the game.** It refuses to start if the game is running, records what was installed
  before it began, and removes only its own packs.
- **It leaves the author's things alone** — their installed mods, their workspaces, their
  `settings.json`. It uses its own temp workspace and its own settings home, and asserts at the end
  that the real settings file is byte-identical.
- **It tidies up on the way out, a failed run included** — its own mods leave the ledger, its kept
  packs are swept by the id in their manifest, and any of the author's mods it stood down are put
  back. A green run leaves `pgassettool mods` as it found it.

Two traps when adding to it:

- Avalonia headless decodes every `Bitmap` to 1×1. Measure images from their bytes
  (`StbImageSharp.ImageInfo`), and measure layout from `Bounds` after an explicit `Measure`/`Arrange`
  — a binding that resolves to nothing leaves the view model perfectly correct.
- The self-test keeps the dispatcher as its synchronization context and pumps it in `WaitWhile`.
  Clearing it puts continuations on the pool, where the first one to touch a bound collection throws
  "call from invalid thread" a long way from the cause.
