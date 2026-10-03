# PGAssetTool

English / [日本語](README.ja.md)

<p align="center">
  <a href="https://github.com/mof-22/PGAssetTool/releases"><img src="https://img.shields.io/github/downloads/mof-22/PGAssetTool/total.svg?style=for-the-badge"></a>
  <a href="https://github.com/mof-22/PGAssetTool/releases"><img src="https://img.shields.io/github/v/release/mof-22/PGAssetTool?style=for-the-badge"></a>
</p>

A Windows tool that makes modding the assets of a certain pixel-styled game easy.
- **Browse** everything an item refers to — its models, textures and more — in one place
- **Editor** to change that data and build it into a mod pack
- **Manager** to look after the mod packs you have downloaded

Everything to do with the game's assets, made simple.

## **Supported items**

| Kind | Status | Notes |
|---------|------|--------------------|
| Weapons | ✅️ | |
| Weapon skins | ✅️ | |
| Hats | ✅️ | Their skins are drawn with the hat's own textures, so editing the hat changes both |
| Masks | ✅️ | As hats |
| Avatars | ✅️ | |
| Capes | ✅️ | As hats |
| Boots | ✅️ | As hats |
| Gliders | ✅️ | |
| Transports | ✅️ | |
| Pets | ✅️ | |
| Gadgets | ❌️ | Planned |
| Buttons and other UI | ❌️ | Planned |
| Hat skins | ❌️ | Planned |
| Mask skins | ❌️ | Planned |
| Cape skins | ❌️ | Planned |
| Boot skins | ❌️ | Planned |
| Lobby items | ❌️ | Undecided |
| Armor | ❌️ | Undecided |
| Graffiti | ❌️ | Undecided |

**Other kinds are being considered, or undecided.**

## Getting it

You need Windows x64.

The tool keeps its settings and backups in the same folder as the exe, so **after downloading, move the exe into a folder of its own.** For example:\
`C:/Users/%USER%/downloads/PGAssetTool.exe` → `C:/Users/%USER%/Tools/PGAT/PGAssetTool.exe`

### Download a ready-built copy from Releases
Download the latest version from Releases, on the right of this page.


### Build it from source

To build the tool yourself you also need the `.NET 10 SDK`.

```
dotnet publish src/PGAssetTool.Gui -c Release -o dist
```

---

## Making a mod pack

1. **Extract the item.** In **Browse**, pick the kind and the item, then press Extract (`Ctrl+E`). Its files are
   written into a workspace folder under `PGAssetTool-data/workspace` (`Ctrl+O` opens it): `textures/` (`.png`),
   `meshes/` (`.glb`), `audio/` and, for weapons, `animations/`, beside a `pgmod.json` that says which file
   replaces what. To write a weapon's skins too, choose one in *Extract with skin* — or *all of them*, which writes
   the weapon and every skin, each into a workspace of its own (`--all-skins` on the command line).
2. **Edit the files you want to change**, in the program you would use anyway: an image editor for the
   textures, Blender for the models, any sound editor for the sounds. Save over the file with the same name, or
   drop the edited file onto the tool's window and it goes in its place. Animations are changed in the Editor
   (see below).
3. **Check it in the Editor.** Each changed file is shown next to the game's own, the model wearing your
   textures and playing your animations. Pack details, at the bottom left, set the pack's name, author, version, description and icon.
4. **Build pack** writes a `.pgmod` into the workspace folder, holding only the files you changed. **Build and
   apply** also installs it.
5. **Manager** turns it off, back on, or removes it. Removing a mod puts the game's own files back.

---

## The three tabs

**Browse** - The list of items of whichever kind the picker above it shows — weapons, hats, capes, masks, boots,
pets, gliders, transports or avatars — and a tree of the data the selected one refers to.\
Search weapons by the number in the in-game gallery, by the weapon's name in any language, or by its internal
number; anything else by its name or its id.\
In the data tree, the shortcut `Ctrl+Shift+R` switches to a simpler view.

The panel on the right is the preview: look at the actual model, show it in its related skins, and play its animations.

**Editor** - Compare what is in the workspaces you have extracted, and more.
The workspace's details can be changed in Pack details at the bottom left.\
With one or more files changed, the Build pack button writes a mod pack, a `.pgmod` file, into the workspace folder.\
Build and apply installs the mod pack in one click.\
Selecting a weapon's animation plays it on the model, the game's next to the workspace's. It can be swapped for
another item's animation — type its number or name, pick the clip, Use — or for one made in Blender: Write a .glb
to edit puts every animation and the model into `animations/animations.glb`, and Use a .glb (or dropping the file
on the window) brings the edited one back. A `.anim` dragged in from another weapon's workspace is fitted to
this weapon's bones the same way. Put the original back undoes any of them.\
**An animation always keeps the length of the weapon's own**: the game times shots and reloads by them, so the
new motion is played faster or slower to fit, and the weapon fires and reloads exactly as before.

**Manager** - Look after the mod packs you have installed.\
Turn on/off switches a mod on or off\
Remove takes it out of the tool (hold `SHIFT` to delete the file as well)\
Reapply all writes every enabled mod again (useful after a game update), and more.\
`Ctrl` + wheel changes the size of the list.\
The panel on the left can narrow the list to matching mods, with the same search as Browse.

---

## Installing somebody else's mod pack

Drag the `.pgmod` file onto the window. Several at once works too.

---

## Shortcuts

| Key | What it does |
| --- | --- |
| `Ctrl+1` `Ctrl+2` `Ctrl+3` | Browse / Editor / Manager |
| `Ctrl+F` | Jump to the search on this tab |
| `Ctrl+E` | Extract the selected item |
| `Ctrl+O` | Open the workspace folder |
| `Ctrl+Shift+E` | Extract just the selected asset |
| `Shift+A` | Show the alpha of the picture in front of you |
| `Ctrl+Shift+R` | Show only what can be replaced |
| `Ctrl+R` | Re-read the game from disk |
| `F5` | Re-read the workspaces |
| `Delete` | Delete the selected workspace / remove the selected mod |
| `Ctrl+,` | Options |

With a model in front of you:

| | |
| --- | --- |
| Drag | Turn it |
| `Shift` + drag | Tilt it |
| Middle button + drag | Move it in the frame |
| Wheel | Closer and further |
| `R` | Straighten it up |

---

## When something is wrong

**The game was not found** - The Steam version is assumed. If yours is from elsewhere, such as Epic, and that
causes trouble, name it yourself in **File → Options** — the folder holding the game's own `*_Data` folder.

**A bundle was changed by something else** - The data this tool is about to change has already been changed some other way. Put the original data back with Steam's integrity check.

**The text and the buttons are too small, or too large** - *How large everything is drawn*, in
**File → Options**, goes from 75% to 200%. The whole window is scaled rather than the text alone, so the
column widths and the spacing grow with it.

**The tool uses too much memory** - Two settings in **File → Options** trade speed for it. *Memory for reading
the game faster* is how much of the bundles you browse is kept unpacked, 1GB by default; less makes selecting
and extracting slower. *Rebuild one bundle at a time* makes installing and toggling about a third slower for
about a third less memory.

**Moving to another PC, or reinstalling** - Move the whole contents of the `PGAssetTool-data` folder.
`author.key` above all is the key your packs are signed with: lose it, and packs you build afterwards cannot be
shown to be by the same author. Keeping a copy somewhere safe is recommended.

**The mods you installed keep changing** - Two copies of the tool installing into one game do that to each
other. See [KNOWN_ISSUES.md](KNOWN_ISSUES.md).

**The tool crashed, or you found a bug** - Send a report with the `crash-<date and time>.txt` (if it crashed)
or `errors.log` from `PGAssetTool-data/logs`. **File → Open the log folder** opens it.

---

## The command line

Everything the GUI does, for every kind of item. The executable is
`src/PGAssetTool.Cli/bin/Release/net10.0-windows/win-x64/pgassettool.exe`, or:

```
dotnet run --project src/PGAssetTool.Cli -c Release -- <command>
```

| Command | What it does |
| --- | --- |
| `info` | Show the detected installation and version. |
| `weapons [<filter>]` | List weapons, filtered by name, internal number, tag or gallery number. |
| `items [<kind>]` | List the kinds besides weapons — hats, capes, masks, boots, pets, gliders, transports, avatars — or the whole of one of them (`items hat` and `items hats` both work). |
| `show <item>` | One item and everything it refers to. A weapon takes its in-game number (`819`), its prefab name (`Weapon1257`) or a slug; anything else takes the id `items` lists. Any item can also be named by its whole display name (`"Aztec Power Hat"`), and a near miss lists what it might have meant. **The in-game number and the prefab number are different sequences.** |
| `extract <item>` | Write out everything belonging to an item. With `--workspace`, also writes the `pgmod.json` that makes it packable. |
| `pack [<directory>]` | Build a `.pgmod` from a workspace. Only files edited since the extract go in. |
| `apply <pack>...` | Install one or more `.pgmod` files into the game, rebuilding it once for all of them. |
| `mods` | List installed mods. |
| `enable <id>...` / `disable <id>...` | Turn mods on, or off without uninstalling them. |
| `remove <id>...` | Uninstall mods. A mod can be named by its id, by the start of its id, or by its name, as long as that names only one. |
| `verify` | Check every bundle against the hash the game recorded for it. |
| `consolidate` | Report what emptying the downloaded cache would change. `--apply` removes only the copies that change nothing. |
| `animation <directory> [<clip>]` | List a workspace's animations, or change one: `--from <item>` puts in that item's animation of the same name (or `--clip <name>`), fitted to this item's bones; `--anim <file>` puts in a `.anim` from another workspace; `--glb <file>` puts in one from a glTF; `--reset` puts the original back. `--export` writes them all, with the item's models, into one `.glb` to edit. The animation always keeps this item's own length. |

| Option | What it does |
| --- | --- |
| `--game <directory>` | Use this installation instead of the detected one. Anything with the expected layout works, so you can work on a copy rather than the live game. |
| `--language <bundle>` | The translation bundle names are read from (default `l_en-gb`). |
| `--out <directory>` | Where output goes. `extract` defaults to `./workspace`. |
| `--author <name>` | Recorded in the manifest. |
| `--skin <id or name>` | Write out one of the weapon's skins instead of the weapon as it comes. `show` lists what there is. |
| `--all-skins` | With `extract`: write the weapon as it comes and every one of its skins, each into a workspace of its own. |
| `--opaque` | Write textures with no alpha channel. Some weapons use transparency, so if your image editor is not good at editing alpha, try this. |
| `--whole` | Write the whole of every texture. By default **the part no model uses is made transparent**, leaving only what is actually seen. |
| `--protect` | Sign the built pack, and keep it from opening as a zip. |
| `--force` | Let `apply` go ahead anyway: back up a bundle already modified, or install a signed pack altered after it was built. |
| `--fast` | Compress rebuilt bundles less: that step is about four times quicker and bundles come out about 1.2 times the size. The game reads both at the same speed. |
| `--rebuild` | Rebuild every bundle, including the ones already holding what they should. Those are normally left as they are. |
| `--low-memory` | Rebuild bundles one at a time rather than four: about a third slower, about a third less memory at the peak. |
| `--read-memory <MB>` | How much of the game `show` and `extract` may keep unpacked in memory (default 1024). Reading is several times quicker for it; `0` reads everything from disk. |
| `--from <item>` | For `animation`: the item whose animation to use. |
| `--clip <name>` | For `animation`: which of its animations, when not the same name. |
| `--glb <file>` | For `animation`: the `.glb` whose animation to use — the one named like the animation being replaced, or the only one in it. |
| `--anim <file>` | For `animation`: a `.anim` to use — another workspace's, say — fitted to this item's bones. Dropping one on an animation in the Editor does the same. |
| `--take <name>` | For `animation`: which of the `.glb`'s animations to use instead. |
| `--export` | For `animation`: write the workspace's animations into `animations/animations.glb` (or `--out <file>`). |
| `--reset` | For `animation`: put the item's own animation back. |
| `--version` | Say which version this is. |

Options can go before or after the command, as `--name value` or `--name=value`. An option the tool does not
know is refused by name rather than ignored. An id with a space in it — the game has one, `avatar_ programmer` —
needs quoting. A mistake in what was typed exits with `2` and anything else that goes wrong with `1`, said in a
sentence; something the tool did not expect also leaves a crash report in `PGAssetTool-data/logs`.

```
pgassettool weapons crystal
pgassettool extract 819 --workspace --out workspace
pgassettool pack workspace/0819_something
pgassettool apply workspace/0819_something/something.pgmod

pgassettool items pets
pgassettool extract pet_alien_cat --workspace
pgassettool disable pet_alien_cat

pgassettool animation workspace/0001_FirstPistol
pgassettool animation workspace/0001_FirstPistol Reload --from 416
pgassettool animation workspace/0001_FirstPistol --export
pgassettool animation workspace/0001_FirstPistol Reload --glb edited.glb
```

### Editing an animation in Blender

1. `pgassettool animation <workspace> --export` (or Write a .glb to edit in the Editor) writes
   `animations/animations.glb`: the weapon and the arms on their bones, one action per animation.
2. In Blender, File › Import › glTF 2.0, pick the action to change, and key the bones as usual.
3. File › Export › glTF 2.0, as glTF Binary (`.glb`), with the default settings.
4. `pgassettool animation <workspace> Reload --glb <that file>` (or Use a .glb) puts the action called Reload
   back. Only the curves you changed are taken from the file; everything you left alone keeps the game's own
   keys, and a file saved without any change changes nothing.

---

## What can be changed

The following are supported now.
| What | From |
| --- | --- |
| Textures | `.png` |
| Models | `.glb` |
| Sounds | `.wav` `.mp3` `.ogg` |
| Weapon animations | another item's animation, or a `.glb` (Blender) |

All of them open in ordinary editing software.

---

## For anyone changing the tool

[DESIGN.md](DESIGN.md) is why it is built the way it is: how an item's parts are found, what a pack is, how
it follows the game's updates, and **the pieces that look wrong but are that way for a reason**.

```
dotnet test -c Release
dotnet run --project src/PGAssetTool.Gui -c Release -- --self-test --game <a copy of the game>
```

`dotnet test` needs no game and is what CI runs. `--self-test` is the real check: it drives the whole tool
against an installation and puts everything back afterwards. It installs and removes mods for real, so point it
at a copy of the game with `--game`; without one it uses the installation it finds.

`classdata.tpk`, Unity's engine class database, is embedded in the core assembly. It is needed because the
files under `*_Data` are built without a TypeTree (AssetBundles carry their own). **It describes stock Unity
classes only and has nothing to do with the game's own code.** See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## Please note

- This tool is unofficial and has no connection whatsoever with the game's developer or publisher.
- It rewrites the game's files, so use it at your own risk. The author accepts no responsibility for any
  damage arising from using this tool, including any effect on your account (such as a ban).
- Do not redistribute other people's work without their permission.
- The tool changes how things look, never how they play. A replaced animation is always stretched or squeezed to
  the length of the weapon's own, because the game times shots and reloads by its animations and another length
  would change the weapon's fire rate and damage. If you installed animation mods with a version from before
  this, use **Reapply all** in the Manager.

---

## License

MIT. See [LICENSE](LICENSE).

This project ships no game content. A mod pack describes changes declaratively and refers to assets already
present in the user's own installation.
