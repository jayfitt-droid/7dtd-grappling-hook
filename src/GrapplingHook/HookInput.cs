using System;
using HarmonyLib;
using InControl;

/// <summary>
/// Attached-state input. The grapple has no keys of its own: with the crossbow
/// that fired the hook in hand, the game's attack action reels in and its aim
/// action reels out, and the game's jump action lets go with any item. That
/// follows whatever the player has bound in the game's controls and leaves
/// nothing to conflict with.
///
/// Attack and aim are blocked from the crossbow where the game hands a click to
/// the held item (Hand.Execute), not by disabling the InControl Primary and
/// Secondary actions: switching those off and on again left the game dropping
/// the first left-click press after a detach. Presses are blocked; releases
/// always pass, so weapons keep a consistent trigger state. With any other
/// item in hand nothing is blocked.
/// </summary>
internal static class HookInput
{
    private static EntityPlayerLocal waitPlayer;

    /// <summary>
    /// True while attached with the firing crossbow in hand, and after a detach
    /// until the attack/aim buttons are released, so the click that was reeling
    /// never turns into a shot.
    /// </summary>
    internal static bool IsBlocking(EntityAlive entity)
    {
        if (entity == null) return false;
        if (Grapple.IsAttached && entity == Grapple.State.Player) return Grapple.HoldingCrossbow(Grapple.State);
        return waitPlayer != null && entity == waitPlayer;
    }

    /// <summary>True while a text field, the console, or a cursor window has input.</summary>
    internal static bool UiBlocksInput(EntityPlayerLocal player)
    {
        try
        {
            LocalPlayerUI ui = player != null ? player.PlayerUI : null;
            GUIWindowManager windows = ui != null ? ui.windowManager : null;
            if (windows != null && windows.IsInputActive()) return true;
            return GameManager.Instance != null && GameManager.Instance.isAnyCursorWindowOpen(null);
        }
        catch (Exception) { return false; }
    }

    internal static bool ReelInHeld(EntityPlayerLocal player) { return Held(player, a => a.Primary); }
    internal static bool ReelOutHeld(EntityPlayerLocal player) { return Held(player, a => a.Secondary); }
    internal static bool DisconnectHeld(EntityPlayerLocal player) { return Held(player, a => a.Jump); }

    private static bool Held(EntityPlayerLocal player, Func<PlayerActionsLocal, PlayerAction> pick)
    {
        PlayerActionsLocal actions = player != null ? player.playerInput : null;
        PlayerAction action = actions != null ? pick(actions) : null;
        return action != null && action.IsPressed;
    }

    /// <summary>Ends aiming down sights before attaching; aim presses are blocked while attached.</summary>
    internal static void EndAiming(EntityPlayerLocal player)
    {
        if (player == null || !player.AimingGun) return;
        ItemInventoryData held = player.inventory?.holdingItemData;
        if (held == null || held.item == null || held.item.Actions == null || held.actionData == null) return;
        for (int i = 0; i < held.item.Actions.Length && i < held.actionData.Count; i++)
        {
            ItemActionZoom zoom = held.item.Actions[i] as ItemActionZoom;
            if (zoom == null || held.actionData[i] == null) continue;
            zoom.ExecuteAction(held.actionData[i], true);
            Log.Out("[GrapplingHook] Ended aiming before attaching.");
            return;
        }
    }

    internal static void Enable() { waitPlayer = null; }

    /// <summary>Ends the attached-state override. Called by Grapple.Detach.</summary>
    internal static void Release(EntityPlayerLocal player, bool alive, bool wasReelItem)
    {
        waitPlayer = null;
        if (player == null || !alive) return;
        // Only the crossbow's buttons were reeling; another item's held
        // trigger is already its own.
        if (wasReelItem && (ReelInHeld(player) || ReelOutHeld(player)))
        {
            waitPlayer = player;
            Log.Out("[GrapplingHook] Waiting for attack/aim buttons to be released.");
        }
        else Log.Out("[GrapplingHook] Input restored.");
    }

    /// <summary>Runs every frame; ends the post-detach block once the buttons are up.</summary>
    internal static void UpdateRelease(EntityPlayerLocal player)
    {
        if (waitPlayer == null || player != waitPlayer) return;
        if (!player.IsDead() && player.AttachedToEntity == null &&
            (ReelInHeld(player) || ReelOutHeld(player))) return;
        waitPlayer = null;
        Log.Out("[GrapplingHook] Input restored.");
    }
}

/// <summary>
/// The single point where the game hands attack (0) and aim (1) clicks to the
/// held item. Block presses for the grappling player; let releases through.
/// </summary>
[HarmonyPatch(typeof(Hand), "Execute")]
internal static class HookHandExecuteGuard
{
    private static bool Prefix(Hand __instance, int __0, bool __1)
    {
        if (__1 || (__0 != 0 && __0 != 1)) return true;
        ItemInventoryData held = __instance.Held;
        return held == null || !HookInput.IsBlocking(held.holdingEntity);
    }
}
