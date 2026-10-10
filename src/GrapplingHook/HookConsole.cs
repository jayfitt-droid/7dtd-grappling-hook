using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

public sealed class ConsoleCmdGrapplingHook : ConsoleCmdAbstract
{
    public override string[] getCommands() { return new[] { "gh" }; }
    public override string getDescription() { return "Grappling Hook test commands"; }
    public override string getHelp() { return "gh give hook [count] | give crossbow | give schematics | attach | detach | breakanchor | state | config | reload"; }

    public override void Execute(List<string> args, CommandSenderInfo sender)
    {
        string command = args.Count == 0 ? "help" : args[0].ToLowerInvariant();
        EntityPlayerLocal player = GameManager.Instance?.World?.GetPrimaryPlayer();
        try
        {
            switch (command)
            {
                case "give": Give(args, player); break;
                case "attach": Attach(player); break;
                case "detach": Grapple.Detach(); break;
                case "breakanchor": BreakAnchor(); break;
                case "state": State(); break;
                case "config": Config(); break;
                case "reload": HookMod.Reload(); Config(); break;
                default: Log.Out("[GrapplingHook] " + getHelp()); break;
            }
        }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Command failed: " + ex); }
    }

    private static void Give(List<string> args, EntityPlayerLocal player)
    {
        if (player == null) { Log.Out("[GrapplingHook] No local player."); return; }
        string kind = args.Count > 1 ? args[1].ToLowerInvariant() : "all";
        if (kind == "schematics" || kind == "schematic")
        {
            foreach (string schematic in HookMod.SchematicNames) GiveItem(player, schematic, 1);
            return;
        }
        if (kind != "all" && kind != "hook" && kind != "crossbow")
        { Log.Out("[GrapplingHook] Usage: gh give [hook [count] | crossbow | schematics]"); return; }
        int count = 50;
        int countIndex = kind == "all" ? 1 : 2;
        if (args.Count > countIndex && (!int.TryParse(args[countIndex], out count) || count < 1 || count > 1000))
        { Log.Out("[GrapplingHook] Count must be 1-1000."); return; }
        // Every tier, so each can be tested.
        if (kind != "crossbow")
            foreach (string hook in HookMod.ItemNames) GiveItem(player, hook, count);
        if (kind != "hook")
        {
            GiveItem(player, "gunBowT1IronCrossbow", 1);
            if (kind == "all") GiveItem(player, "ammoCrossbowBoltIron", 150);
        }
    }

    private static void GiveItem(EntityPlayerLocal player, string name, int count)
    {
        ItemValue value = ItemClass.GetItem(name, false);
        if (value == null || value.IsEmpty())
        { Log.Out("[GrapplingHook] Item unavailable after XML load: " + name); return; }
        int given = 0;
        while (count > 0)
        {
            int batch = Math.Min(count, 150);
            ItemStack stack = new ItemStack(value, batch);
            if (player.bag != null && player.bag.AddItem(stack) || player.inventory.AddItem(stack))
            { given += batch; count -= batch; continue; }
            Log.Out("[GrapplingHook] Backpack and toolbar full; gave " + given + " of " + name);
            return;
        }
        Log.Out("[GrapplingHook] Gave " + given + " " + name + " (backpack first).");
    }

    private static void Attach(EntityPlayerLocal player)
    {
        if (player == null || GameManager.Instance?.World == null) return;
        // No hook is fired, so use the longest tier's range.
        float maxDistance = Mathf.Max(HookMod.Settings.MaxDistance);
        if (!Voxel.Raycast(GameManager.Instance.World, player.GetLookRay(),
            maxDistance, false, false) ||
            !GameUtils.IsBlockOrTerrain(Voxel.voxelRayHitInfo.tag) ||
            !Grapple.Attach(player, Voxel.voxelRayHitInfo.hit.pos,
                Grapple.ResolveAnchorBlock(Voxel.voxelRayHitInfo.hit.pos, Voxel.voxelRayHitInfo.hit.blockPos,
                    Voxel.voxelRayHitInfo.ray.direction), maxDistance, Voxel.voxelRayHitInfo.ray.direction))
            Log.Out("[GrapplingHook] No valid anchor in range.");
    }

    /// <summary>
    /// Removes the block the hook is anchored to, as if it had been destroyed.
    /// Attack and aim are blocked while attached, so this is the way to test
    /// that losing the anchor block drops the rope.
    /// </summary>
    private static void BreakAnchor()
    {
        GrappleState state = Grapple.State;
        World world = GameManager.Instance?.World;
        if (state == null || world == null) { Log.Out("[GrapplingHook] Not attached."); return; }
        Log.Out("[GrapplingHook] Removing anchor block " + state.AnchorBlock + " for testing.");
        world.SetBlockRPC(new BlockValueRef(state.AnchorBlock), BlockValue.Air, MarchingCubes.DensityAir);
    }

    private static void State()
    {
        GrappleState state = Grapple.State;
        Log.Out(state == null ? "[GrapplingHook] Detached." :
            "[GrapplingHook] Attached: anchor=" + state.Anchor + " block=" + state.AnchorBlock +
            " length=" + state.Length.ToString("F1") + " distance=" +
            Vector3.Distance(Grapple.PlayerAnchorPoint(state.Player), state.Anchor).ToString("F1"));
    }

    private static void Config()
    {
        HookSettings c = HookMod.Settings;
        Log.Out("[GrapplingHook] scrap_max_distance=" + c.MaxDistance[0] + " iron_max_distance=" + c.MaxDistance[1] +
            " steel_max_distance=" + c.MaxDistance[2] + " reel_in_speed=" + c.ReelInSpeed +
            " reel_out_speed=" + c.ReelOutSpeed +
            " block_damage=" + c.BlockDamage + " fall_damage_on_release=" + c.FallDamageOnRelease +
            " stamina_use=" + c.StaminaUse);
    }
}

internal static class HookConsole
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Register(SdtdConsole console)
    {
        if (console == null) return;
        try
        {
            var variants = typeof(SdtdConsole).GetField("m_CommandsAllVariants", Flags)?.GetValue(console)
                as Dictionary<string, IConsoleCommand>;
            var list = typeof(SdtdConsole).GetField("m_Commands", Flags)?.GetValue(console)
                as List<IConsoleCommand>;
            if (variants == null || list == null || variants.ContainsKey("gh")) return;
            var command = new ConsoleCmdGrapplingHook();
            variants["gh"] = command;
            list.Add(command);
            Log.Out("[GrapplingHook] gh command registered.");
        }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Console registration: " + ex); }
    }
}

[HarmonyPatch(typeof(SdtdConsole), "RegisterCommands")]
internal static class HookConsoleRegistrationPatch
{
    private static void Postfix(SdtdConsole __instance) { HookConsole.Register(__instance); }
}
