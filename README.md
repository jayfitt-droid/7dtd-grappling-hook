# Grappling Hook for 7 Days to Die v3.3.0 (b18)

A single-player mod that adds three consumable grappling hooks for the iron and compound crossbows. Fire one at a solid block or terrain within its range and it anchors a rope you can climb, descend and swing on. Enemy hits do no damage and do not attach.

| Hook | Range | Recipe (makes 5) | How you get the recipe |
|---|---:|---|---|
| Scrap Grappling Hook | 15 m | 20 scrap iron, 10 plant fibers, 1 duct tape; no workbench needed | Known from the start |
| Iron Grappling Hook | 25 m | 4 forged iron, 5 mechanical parts, 3 duct tape; workbench | Iron Grappling Hook Schematic, found as loot |
| Steel Grappling Hook | 35 m | 4 forged steel, 5 mechanical parts, 3 duct tape; workbench | Steel Grappling Hook Schematic, found as rarer, later loot |

The three hooks share one model with different finishes and behave the same apart from range. A fired hook is used up whether or not it attaches.

## Install

1. Download `GrapplingHook-<version>.zip` from the Releases page and unzip it into the game's `Mods` folder. You should end up with `Mods\GrapplingHook\ModInfo.xml`.
2. Make sure `Mods\0_TFP_Harmony` is there. It ships with the game and loads the mod's code.
3. Start the game with Easy Anti-Cheat turned off; mods with code do not load with it on.

To remove the mod, delete `Mods\GrapplingHook`.

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

Edit `Mods\GrapplingHook\Config\GrapplingHookConfig.json`, then restart the game or run `gh reload` in the console. Every setting has a comment in the file describing it and its allowed range; an invalid value falls back to its default and the game log says so.

| Setting | Default | Meaning |
|---|---:|---|
| `scrap_max_distance` | 15 | How far away a Scrap Grappling Hook can attach, in meters |
| `iron_max_distance` | 25 | The same for the Iron Grappling Hook |
| `steel_max_distance` | 35 | The same for the Steel Grappling Hook |
| `reel_in_speed` | 6.0 | Meters per second |
| `reel_out_speed` | 4.0 | Meters per second; there is no limit on how far you can reel out |
| `block_damage` | 10 | Damage a hook does to the block it hits |
| `fall_damage_on_release` | true | Whether the fall after letting go can hurt you |
| `stamina_use` | 3 | Stamina per second while attached and off the ground; 0 turns it off |

## Console commands

For testing. `gh give hook [count]` adds that many of each hook to the backpack, falling back to the toolbar. `gh give crossbow` gives an iron crossbow, `gh give schematics` both schematics, and `gh give` hooks, a crossbow and iron bolts. `gh attach` attaches to the aimed block without firing; `gh breakanchor` removes the block the hook is anchored to; `gh detach`, `gh state`, `gh config` and `gh reload` are also available. Mod messages in the game log start with `[GrapplingHook]`.

## Building from source

Requirements: the .NET SDK and a 7 Days to Die v3.3 install. The project file and `deploy.ps1` point at `C:\7D2D\Custom\v3.3`; change those paths to your own install.

- `powershell -ExecutionPolicy Bypass -File package.ps1` compiles the mod and writes `dist\GrapplingHook-<version>.zip`, the file players install. The version comes from `modlet\GrapplingHook\ModInfo.xml`.
- For development, `dotnet build src/GrapplingHook/GrapplingHook.csproj -c Release` (or `build.cmd`) builds the DLL and `deploy.ps1` installs it into the game's `Mods` folder.
- `build-assets.ps1` rebuilds the art: Blender 5.2 runs `unity/blender/build_assets.py` (hook model, three baked finishes, rope textures), then Unity **2022.3.62f2** builds the prefabs, icons and asset bundle into `modlet/GrapplingHook`. The Unity version must match the game's engine; bundles from a newer editor do not load.
- `tools/inspect` and `tools/ildump` are small helpers for looking at the game's assemblies when writing patches.

## Status

Version 0.9.0. Built against and tested in single-player v3.3.0 (b18). Multiplayer and dedicated servers are not supported.

## License

MIT; see `LICENSE`.
