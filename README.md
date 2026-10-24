# Grappling Hook for 7 Days to Die v3.3.0 (b18)

A mod that adds three consumable grappling hooks for the iron and compound crossbows. Fire one at a solid block or terrain within its range and it anchors a rope you can climb, descend and swing on. Enemy hits do no damage and do not attach. It works in single-player, on dedicated servers and in hosted games, and other players see your rope and hook.

## Getting hooks

| Hook | Range | Recipe (makes 5, 10 s) | How you get the recipe |
|---|---:|---|---|
| Scrap Grappling Hook | 15 m | 20 scrap iron, 10 plant fibers, 1 duct tape; crafted in the backpack | Known from the start |
| Iron Grappling Hook | 25 m | 4 forged iron, 5 mechanical parts, 3 duct tape; workbench | Read the Iron Grappling Hook Schematic |
| Steel Grappling Hook | 35 m | 4 forged steel, 5 mechanical parts, 3 duct tape; workbench | Read the Steel Grappling Hook Schematic |

- **Hooks are crafted.** Traders don't sell the hooks themselves.
- **Schematics are found as loot or bought from traders.** They're part of the game's own schematic loot: books and schematics in bookshelves and other book loot, quest reward schematics and buried treasure chests. The iron schematic is in the mid-tier pool and the steel one in the late-tier pool, so steel turns up rarer and later in the game. Traders can also stock them: iron with their usual schematics for 2,400 coins, steel only among their rare books for 3,000 (before Better Barter and other discounts). Reading a schematic unlocks that hook's recipe for good.
- The three hooks share one model with different finishes (rusty scrap, dark forged iron, bright steel) and behave the same apart from range. A fired hook is used up whether or not it attaches, and can't be picked back up.

## Install

1. Download `GrapplingHook-<version>.zip` from the Releases page and unzip it into the game's `Mods` folder. You should end up with `Mods\GrapplingHook\ModInfo.xml`.
2. Make sure `Mods\0_TFP_Harmony` is there. It ships with the game and loads the mod's code.
3. Start the game with Easy Anti-Cheat turned off; mods with code do not load with it on.

To remove the mod, delete `Mods\GrapplingHook`.

### Multiplayer

- Install the mod on the server **and on every player's game**, all the same version. The server can share XML changes, but not the mod's code or its hook model.
- EAC must be off on the server and every client.
- On a server the **server's config file** applies to everyone (ranges, reel speeds, block damage, fall damage, stamina). Players' own config files apply again when they leave.
- Other players see each grappling player's hook and rope, including ropes already out when they join.

## Controls

The grapple has no keys of its own. It uses the game's attack, aim and jump actions, so it follows whatever you have bound in the game's controls.

| Input | While attached |
|---|---|
| Attack (left mouse button by default) | Reel in, while holding the crossbow that fired the hook |
| Aim (right mouse button by default) | Reel out, while holding the crossbow that fired the hook |
| Jump (space by default) | Let go, with any item in hand |
| Movement keys | Swing; release them and the swing settles into a hang |

- You can switch to any other weapon or tool while attached and use it normally. The rope holds its length until you switch back to the crossbow that fired the hook; while that crossbow is in hand its attack and aim reel instead of firing and aiming.
- If that crossbow leaves your inventory (dropped, scrapped, sold, stored), the rope lets go.
- Hanging off the ground uses stamina. When it runs out you let go, however high you are.
- The rope also lets go if the block it is anchored to is destroyed, if you die, if you get on a vehicle, or when you leave the world. A rope is not saved: quit while hanging and you fall from that spot when you come back.
- The rope can pass through blocks between you and the anchor.
- Grapple controls are ignored while the console, chat or an inventory window has input.

## Configuration

Edit `Mods\GrapplingHook\Config\GrapplingHookConfig.json`, then restart the game or run `gh reload` in the console. Every setting has a comment in the file describing it and its allowed range; an invalid value falls back to its default and the game log says so. On a server, edit the server's file and run `ghserver reload`; the new values go to every player straight away (except `debug`, which each game sets for itself).

| Setting | Default | Meaning |
|---|---:|---|
| `scrap_max_distance` | 15 | How far away a Scrap Grappling Hook can attach, in meters |
| `iron_max_distance` | 25 | The same for the Iron Grappling Hook |
| `steel_max_distance` | 35 | The same for the Steel Grappling Hook |
| `reel_in_speed` | 6.0 | Meters per second |
| `reel_out_speed` | 4.0 | Meters per second; there is no limit on how far you can reel out |
| `block_damage` | 10 | Base damage a hook does to the block it hits; the game scales it by the block's material and hardness, so the health lost can differ |
| `fall_damage_on_release` | true | Whether the fall after letting go can hurt you |
| `stamina_use` | 3 | Stamina per second while attached and off the ground; 0 turns it off |
| `debug` | false | Detailed log of every shot, attach, reel and rope message; each game's own choice, not taken from the server |

## Console commands

For testing; they need admin rights on a server. `gh give hook [count]` adds that many of each hook to the backpack, falling back to the toolbar. `gh give crossbow` gives an iron crossbow, `gh give schematics` both schematics, and `gh give` hooks, a crossbow and iron bolts. `gh attach` attaches to the aimed block without firing; `gh breakanchor` removes the block the hook is anchored to; `gh detach`, `gh state`, `gh ropes` (other players' ropes this game knows about), `gh config` and `gh reload` are also available. `gh` runs on your own game; `ghserver config` and `ghserver reload` run on the server. Mod messages in the game log start with `[GrapplingHook]`.

## Building from source

Requirements: the .NET SDK and a 7 Days to Die v3.3 install. The project file and the scripts point at `C:\7D2D\Custom\v3.3`; change those paths to your own install.

- `powershell -ExecutionPolicy Bypass -File package.ps1` compiles the mod and writes `dist\GrapplingHook-<version>.zip`, the file players install. The version comes from `modlet\GrapplingHook\ModInfo.xml`; `-Label beta` adds a pre-release label to the file name.
- For development, `dotnet build src/GrapplingHook/GrapplingHook.csproj -c Release` (or `build.cmd`) builds the DLL and `deploy.ps1` installs it into the game's `Mods` folder.
- `test-server.ps1 start | status | cmd "<command>" | stop` runs a local dedicated server from the same install for multiplayer testing; `test-client2.ps1` starts a second copy of the game with the same mods (it needs a second Steam account to join).
- `build-assets.ps1` rebuilds the art: Blender 5.2 runs `unity/blender/build_assets.py` (hook model, three baked finishes, rope textures), then Unity **2022.3.62f2** builds the prefabs, icons and asset bundle into `modlet/GrapplingHook`. The Unity version must match the game's engine; bundles from a newer editor do not load.
- `tools/inspect` and `tools/ildump` are small helpers for looking at the game's assemblies when writing patches.

## Status

Version 1.0.0-beta. Built against v3.3.0 (b18) and tested in single-player, on a dedicated server and in a hosted game with two players.

## License

MIT; see `LICENSE`.
