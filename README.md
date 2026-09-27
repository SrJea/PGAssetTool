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
| Hats | ⚠️ | CLI only. Not recommended, not supported |
| Masks | ⚠️ | CLI only. Not recommended, not supported |
| Avatars | ⚠️ | CLI only. Not recommended, not supported |
| Capes | ⚠️ | CLI only. Not recommended, not supported |
| Boots | ⚠️ | CLI only. Not recommended, not supported |
| Gliders | ⚠️ | CLI only. Not recommended, not supported |
| Transports | ⚠️ | CLI only. Not recommended, not supported |
| Pets | ⚠️ | CLI only. Not recommended, not supported |
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

WIP...

---

## The three tabs

**Browse** - The list of weapons, and a tree of the data the selected one refers to.\
Search by the number in the in-game gallery, by the weapon's name in any language, or by its internal number.\
In the data tree, the shortcut `Ctrl+Shift+R` switches to a simpler view.

The panel on the right is the preview: look at the actual model, show it in its related skins, and play its animations.

**Editor** - Compare what is in the workspaces you have extracted, and more.
The workspace's details can be changed in Pack details at the bottom left.\
With one or more files changed, the Build pack button writes a mod pack, a `.pgmod` file, into the workspace folder.\
Build and apply installs the mod pack in one click.

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
| `Ctrl+E` | Extract the selected weapon |
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

Everything the GUI does. The executable is
`src/PGAssetTool.Cli/bin/Release/net10.0-windows/win-x64/pgassettool.exe`, or:

```
dotnet run --project src/PGAssetTool.Cli -c Release -- <command>
```

| Command | What it does |
| --- | --- |
| `info` | Show the detected installation and version. |
| `weapons [<filter>]` | List weapons, filtered by name, internal number, tag or gallery number. |
| `items [<kind>]` | List the kinds besides weapons — hats, capes, masks, boots, pets, gliders, transports, avatars — or the whole of one of them. |
| `show <item>` | One item and everything it refers to. A weapon takes its in-game number (`819`), its prefab name (`Weapon1257`) or a slug; anything else takes the id `items` lists. **The in-game number and the prefab number are different sequences.** |
| `extract <item>` | Write out everything belonging to an item. With `--workspace`, also writes the `pgmod.json` that makes it packable. |
| `pack [<directory>]` | Build a `.pgmod` from a workspace. Only files edited since the extract go in. |
| `apply <pack>` | Install a `.pgmod` into the game. |
| `mods` | List installed mods. |
| `enable <id>` / `disable <id>` | Turn a mod on, or off without uninstalling it. |
| `remove <id>` | Uninstall a mod. |
| `verify` | Check every bundle against the hash the game recorded for it. |
| `consolidate` | Report what emptying the downloaded cache would change. `--apply` removes only the copies that change nothing. |

| Option | What it does |
| --- | --- |
| `--game <directory>` | Use this installation instead of the detected one. Anything with the expected layout works, so you can work on a copy rather than the live game. |
| `--language <bundle>` | The translation bundle names are read from (default `l_en-gb`). |
| `--out <directory>` | Where output goes. `extract` defaults to `./workspace`. |
| `--author <name>` | Recorded in the manifest. |
| `--skin <id or name>` | Write out one of the weapon's skins instead of the weapon as it comes. `show` lists what there is. |
| `--opaque` | Write textures with no alpha channel. Some weapons use transparency, so if your image editor is not good at editing alpha, try this. |
| `--whole` | Write the whole of every texture. By default **the part no model uses is made transparent**, leaving only what is actually seen. |
| `--protect` | Sign the built pack, and keep it from opening as a zip. |
| `--force` | Let `apply` go ahead anyway: back up a bundle already modified, or install a signed pack altered after it was built. |
| `--fast` | Compress rebuilt bundles less: that step is about four times quicker and bundles come out about 1.2 times the size. The game reads both at the same speed. |
| `--rebuild` | Rebuild every bundle, including the ones already holding what they should. Those are normally left as they are. |
| `--low-memory` | Rebuild bundles one at a time rather than four: about a third slower, about a third less memory at the peak. |
| `--read-memory <MB>` | How much of the game `show` and `extract` may keep unpacked in memory (default 1024). Reading is several times quicker for it; `0` reads everything from disk. |

```
pgassettool weapons crystal
pgassettool extract 819 --workspace --out workspace
pgassettool pack workspace/0819_something
pgassettool apply workspace/0819_something/something.pgmod
```

---

## What can be changed

The following are supported now.
| What | From |
| --- | --- |
| Textures | `.png` |
| Models | `.glb` |
| Sounds | `.wav` `.mp3` `.ogg` |

All of them open in ordinary editing software.

---

## For anyone changing the tool

[DESIGN.md](DESIGN.md) is why it is built the way it is: how an item's parts are found, what a pack is, how
it follows the game's updates, and **the pieces that look wrong but are that way for a reason**.

```
dotnet test -c Release
dotnet run --project src/PGAssetTool.Gui -c Release -- --self-test
```

`dotnet test` needs no game and is what CI runs. `--self-test` is the real check: it drives the whole tool
against the live installation and puts everything back afterwards.

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

---

## License

MIT. See [LICENSE](LICENSE).

This project ships no game content. A mod pack describes changes declaratively and refers to assets already
present in the user's own installation.
