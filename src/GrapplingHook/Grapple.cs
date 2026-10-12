using System;
using HarmonyLib;
using UnityEngine;

internal sealed class GrappleState
{
    internal EntityPlayerLocal Player;
    internal Vector3 Anchor;
    internal Vector3 AnchorDirection;
    internal Vector3i AnchorBlock;
    internal int AnchorType;
    internal float Length;
    // World-space velocity in m/s. While the rope drives the player, this is
    // the authoritative motion; the controller only executes it with collision.
    internal Vector3 Velocity;
    internal bool Driving;
    internal Vector3 DriveStart;
    internal Vector3 DriveMove;
    internal float DriveDt;
    internal float LastReelTime;
    internal float LastMotionLog;
    internal bool ReelBlockedLogged;
    // The crossbow that fired this hook. Reeling needs it in hand, and losing
    // it from the inventory detaches. Unset for `gh attach` (no restriction).
    internal bool HasCrossbow;
    internal int CrossbowType;
    internal ushort CrossbowSeed;
    internal ushort CrossbowQuality;
    internal float CrossbowMissingSince = -1f;
    // Stamina level the drain last left, or -1 while not draining.
    internal float DrainedStamina = -1f;
    internal float LastDrainTime = -1f;
}

internal static class Grapple
{
    private const float MinLength = 1.5f;
    private const float TautTolerance = 0.02f;
    private const float MaxRopeSpeed = 30f;
    private const float MaxReelSlack = 0.3f;
    // Swing feel, tuned in play and no longer configurable: acceleration from
    // the movement keys (m/s²) and per-second damping that settles a swing.
    private const float SwingForce = 6f;
    private const float SwingDamping = 0.6f;
    private static GrappleState state;
    private static bool disconnectHeld;
    private static bool suppressReleaseLanding;
    private static float releaseTime;
    private static float groundedSince = -1f;

    internal static bool IsAttached { get { return state != null; } }

    /// <summary>
    /// Tells other mods that a rope is attached, without either side referencing
    /// the other's assembly: a named value on the AppDomain, true while attached
    /// and absent otherwise. It is never saved and is gone when the game closes.
    /// The Glider Suit mod reads it and folds its wings, because both mods drive
    /// the player's velocity from vp_FPController.FixedMove and cannot share it.
    /// </summary>
    internal const string AttachedSignal = "GrapplingHook.Attached";

    private static void PublishAttached(bool attached)
    {
        AppDomain.CurrentDomain.SetData(AttachedSignal, attached ? (object)true : null);
    }
    internal static GrappleState State { get { return state; } }

    internal static Vector3 AttachOffset(EntityPlayerLocal player)
    {
        // A stable point relative to the controller; the animated chest
        // transform lags behind the controller and makes the rope jitter.
        return Vector3.up * Mathf.Max(0.7f, player.GetEyeHeight() * 0.65f);
    }

    internal static Vector3 PlayerAnchorPoint(EntityPlayerLocal player)
    {
        return player.position + AttachOffset(player);
    }

    private static Vector3 ControllerPoint(vp_FPController controller, EntityPlayerLocal player)
    {
        return controller.transform.position + Origin.position + AttachOffset(player);
    }

    /// <summary>
    /// Terrain hits often report the air voxel just above the surface. Step a
    /// few centimetres along the shot direction to find the solid voxel.
    /// </summary>
    internal static Vector3i ResolveAnchorBlock(Vector3 hitPos, Vector3i reported, Vector3 rayDirection)
    {
        World world = GameManager.Instance?.World;
        if (world == null) return reported;
        if (!world.GetBlock(reported).isair) return reported;
        Vector3 dir = rayDirection.sqrMagnitude > 1e-6f ? rayDirection.normalized : Vector3.down;
        foreach (float step in new[] { 0.05f, 0.15f, 0.3f })
        {
            Vector3i candidate = World.worldToBlockPos(hitPos + dir * step);
            if (!world.GetBlock(candidate).isair)
            {
                Log.Out("[GrapplingHook] Anchor moved from air voxel " + reported + " to solid voxel " + candidate);
                return candidate;
            }
        }
        Log.Out("[GrapplingHook] Impact reported only air near " + hitPos + "; no anchor.");
        return reported;
    }

    /// <summary>
    /// True when the player holds the crossbow that fired the hook. Items are
    /// matched by type, seed and quality: the ItemValue object itself is
    /// copied when a stack moves between slots.
    /// </summary>
    internal static bool HoldingCrossbow(GrappleState s)
    {
        if (s == null || !s.HasCrossbow) return true;
        Inventory inventory = s.Player != null ? s.Player.inventory : null;
        return inventory != null && IsCrossbow(s, inventory.holdingItemItemValue);
    }

    private static bool IsCrossbow(GrappleState s, ItemValue item)
    {
        return item != null && !item.IsEmpty() && item.type == s.CrossbowType &&
            item.Seed == s.CrossbowSeed && item.Quality == s.CrossbowQuality;
    }

    private static bool IsCrossbow(GrappleState s, ItemStack stack)
    {
        return stack != null && !stack.IsEmpty() && IsCrossbow(s, stack.itemValue);
    }

    /// <summary>Toolbar, backpack, or the stack being dragged between them.</summary>
    private static bool CarryingCrossbow(GrappleState s)
    {
        EntityPlayerLocal player = s.Player;
        if (player.inventory != null)
        {
            if (IsCrossbow(s, player.inventory.holdingItemItemValue)) return true;
            for (int i = 0; i < player.inventory.SlotCount; i++)
                if (IsCrossbow(s, player.inventory.GetItem(i))) return true;
        }
        ItemStackGrid grid = player.bag != null ? player.bag.ItemGrid : null;
        if (grid != null)
            for (int i = 0; i < grid.Length; i++)
                if (IsCrossbow(s, grid[i])) return true;
        return IsCrossbow(s, player.DragAndDropItem);
    }

    internal static bool Attach(EntityPlayerLocal player, Vector3 anchor, Vector3i blockPos,
        float maxDistance, Vector3 direction = default(Vector3), ItemValue crossbow = null)
    {
        if (state != null || player == null || player.IsDead() || player.AttachedToEntity != null ||
            GameManager.Instance == null || GameManager.Instance.World == null) return false;
        float distance = Vector3.Distance(PlayerAnchorPoint(player), anchor);
        // maxDistance is the fired hook tier's grapple range.
        if (distance < 0.1f) return false;
        if (distance > maxDistance)
        {
            Log.Out("[GrapplingHook] Out of range: " + distance.ToString("F1") + " m, limit " +
                maxDistance.ToString("F1") + " m.");
            return false;
        }
        BlockValue block = GameManager.Instance.World.GetBlock(blockPos);
        if (block.isair) return false;
        // End aiming before the aim guard below starts blocking zoom actions;
        // otherwise the disabled Secondary action never reports its release.
        HookInput.EndAiming(player);
        vp_FPController controller = player.vp_FPController;
        state = new GrappleState
        {
            Player = player,
            Anchor = anchor,
            AnchorDirection = direction.sqrMagnitude > 1e-6f ? direction.normalized : Vector3.zero,
            AnchorBlock = blockPos,
            AnchorType = block.type,
            // max_distance limits how far a hook can attach (checked above),
            // not how much rope can be reeled out afterwards.
            Length = Mathf.Max(distance, MinLength),
            Velocity = controller != null ? controller.Velocity : Vector3.zero,
            LastReelTime = Time.realtimeSinceStartup
        };
        if (crossbow != null && !crossbow.IsEmpty())
        {
            state.HasCrossbow = true;
            state.CrossbowType = crossbow.type;
            state.CrossbowSeed = crossbow.Seed;
            state.CrossbowQuality = crossbow.Quality;
        }
        PublishAttached(true);
        suppressReleaseLanding = false;
        disconnectHeld = HookInput.DisconnectHeld(player);
        HookInput.Enable();
        HookLine.Show();
        Log.Out("[GrapplingHook] Attached to block " + blockPos + " at " + anchor +
            (state.HasCrossbow ? "; reeling tied to crossbow type " + state.CrossbowType + " seed " + state.CrossbowSeed +
                " quality " + state.CrossbowQuality : "; no crossbow recorded, reeling with any item"));
        return true;
    }

    internal static void Detach(bool released = true)
    {
        GrappleState old = state;
        if (old == null) return;
        state = null;
        PublishAttached(false);
        EntityPlayerLocal player = old.Player;
        vp_FPController controller = player != null ? player.vp_FPController : null;
        bool alive = player != null && !player.IsDead() && player.AttachedToEntity == null;
        if (alive && controller != null && !controller.MotorFreeFly)
            HandMomentumToController(controller, old.Velocity);
        if (released && !HookMod.Settings.FallDamageOnRelease && controller != null)
        {
            suppressReleaseLanding = !controller.Grounded;
            releaseTime = Time.time;
            groundedSince = -1f;
        }
        else suppressReleaseLanding = false;
        HookInput.Release(player, alive, HoldingCrossbow(old));
        HookLine.Hide();
        Log.Out("[GrapplingHook] Detached.");
    }

    private static void HandMomentumToController(vp_FPController controller, Vector3 velocity)
    {
        // The controller moves m_MoveDirection * deltaTime * 60 * timeScale per
        // step, with x/z scaled by m_SpeedModifier. Convert m/s into those units
        // so a released swing carries its momentum; the game then damps it.
        float k = 60f * Mathf.Max(Time.timeScale, 0.01f);
        float sm = Mathf.Max(controller.m_SpeedModifier, 0.01f);
        controller.m_MotorThrottle = new Vector3(velocity.x / (k * sm), 0f, velocity.z / (k * sm));
        controller.m_FallSpeed = velocity.y / k;
        controller.m_MaxHeight = controller.transform.position.y + Origin.position.y;
        controller.m_MaxHeightInitialFallSpeed = 0f;
    }

    internal static void Tick(EntityPlayerLocal player)
    {
        GrappleState s = state;
        if (s == null || s.Player != player) return;
        if (player.IsDead() || player.AttachedToEntity != null || GameManager.Instance == null ||
            GameManager.Instance.World == null)
        {
            Detach(false);
            return;
        }
        BlockValue block = GameManager.Instance.World.GetBlock(s.AnchorBlock);
        if (block.isair || block.type != s.AnchorType)
        {
            Log.Out("[GrapplingHook] Anchor block removed.");
            Detach();
            return;
        }

        HookSettings cfg = HookMod.Settings;
        // The game's jump action lets go, with any item in hand.
        bool disconnect = !HookInput.UiBlocksInput(player) && HookInput.DisconnectHeld(player);
        if (disconnect && !disconnectHeld)
        {
            disconnectHeld = true;
            Detach();
            return;
        }
        if (!disconnect) disconnectHeld = false;

        if (DrainStamina(s, player, cfg.StaminaUse))
        {
            // Out of stamina: the player lets go, wherever they are.
            Log.Out("[GrapplingHook] Out of stamina.");
            Detach();
            return;
        }

        // Dropped, scrapped, sold or stored: without the crossbow that fired
        // the hook the rope lets go. A second of grace covers the moments an
        // item is in neither list while the game moves it.
        if (!s.HasCrossbow) return;
        if (CarryingCrossbow(s)) { s.CrossbowMissingSince = -1f; return; }
        if (s.CrossbowMissingSince < 0f) s.CrossbowMissingSince = Time.time;
        else if (Time.time - s.CrossbowMissingSince > 1f)
        {
            Log.Out("[GrapplingHook] The crossbow that fired the hook left the inventory.");
            Detach();
        }
    }

    /// <summary>
    /// Hanging costs stamina: stamina_use per second while attached and off
    /// the ground. The game regenerates stamina by itself, so the drain also
    /// holds the value at what it last left; otherwise regeneration would
    /// cancel part of it and the cost would not be the configured rate.
    /// Regeneration resumes on the ground or after a detach. Returns true
    /// when the drain has emptied the bar.
    /// </summary>
    private static bool DrainStamina(GrappleState s, EntityPlayerLocal player, float perSecond)
    {
        vp_FPController controller = player.vp_FPController;
        if (perSecond <= 0f || controller == null || controller.Grounded)
        {
            s.DrainedStamina = -1f;
            s.LastDrainTime = -1f;
            return false;
        }
        // This runs once per entity update, which is slower than the frame
        // rate, so measure the time since the last call rather than using
        // one frame's deltaTime (that drained about a fifth of the rate).
        float now = Time.time;
        float elapsed = s.LastDrainTime >= 0f ? Mathf.Clamp(now - s.LastDrainTime, 0f, 0.5f) : 0f;
        s.LastDrainTime = now;
        float current = player.Stamina;
        if (s.DrainedStamina >= 0f) current = Mathf.Min(current, s.DrainedStamina);
        s.DrainedStamina = Mathf.Max(0f, current - perSecond * elapsed);
        player.Stamina = s.DrainedStamina;
        return s.DrainedStamina <= 0f;
    }

    /// <summary>
    /// Runs every frame for the local player. The release-only fall protection
    /// expires shortly after landing, or after a timeout, so a landing that never
    /// calls fallHitGround (water, ladder) cannot protect a later, unrelated fall.
    /// </summary>
    internal static void UpdateReleaseProtection(EntityPlayerLocal player)
    {
        if (!suppressReleaseLanding || state != null || player == null) return;
        if (Time.time - releaseTime > 20f) { suppressReleaseLanding = false; return; }
        vp_FPController controller = player.vp_FPController;
        if (controller == null || !controller.Grounded) { groundedSince = -1f; return; }
        if (groundedSince < 0f) groundedSince = Time.time;
        else if (Time.time - groundedSince > 0.5f) suppressReleaseLanding = false;
    }

    /// <summary>
    /// FixedMove prefix. UpdateMotor and UpdateForces have just run, so the
    /// controller's throttle holds this step's movement input and m_GravityForce
    /// this step's gravity. Integrate the rope velocity, apply the constraint,
    /// and hand the result to FixedMove, which performs the collision-aware move.
    /// </summary>
    internal static void BeforeMove(vp_FPController controller)
    {
        GrappleState s = state;
        if (s == null || controller == null || s.Player == null ||
            s.Player.vp_FPController != controller || controller.CharacterController == null) return;
        s.Driving = false;
        if (controller.MotorFreeFly) return; // swimming: leave movement to the game
        float dt = Time.deltaTime;
        float ts = Time.timeScale;
        if (dt <= 0f || ts <= 0f) return;
        HookSettings cfg = HookMod.Settings;

        // Reel by real elapsed time; earlier tests showed the controller can
        // step less often than the nominal fixed rate.
        float now = Time.realtimeSinceStartup;
        float realDt = Mathf.Clamp(now - s.LastReelTime, 0f, 0.1f);
        s.LastReelTime = now;
        bool uiBlocked = HookInput.UiBlocksInput(s.Player);
        // Reeling needs the crossbow that fired the hook in hand; with any
        // other item the rope holds its length and the buttons belong to it.
        bool canReel = !uiBlocked && HoldingCrossbow(s);
        bool reelIn = canReel && HookInput.ReelInHeld(s.Player);
        bool reelOut = canReel && !reelIn && HookInput.ReelOutHeld(s.Player);
        if (reelIn) s.Length = Mathf.Max(MinLength, s.Length - cfg.ReelInSpeed * realDt);
        else if (reelOut) s.Length += cfg.ReelOutSpeed * realDt;

        Vector3 pos = ControllerPoint(controller, s.Player);
        float dist = Vector3.Distance(pos, s.Anchor);
        bool taut = dist >= s.Length - TautTolerance;
        if (controller.Grounded && !taut)
        {
            // Standing with slack: normal walking, no rope forces.
            s.Velocity = Vector3.zero;
            return;
        }

        Vector3 v = s.Velocity;
        // Gravity: the controller adds m_GravityForce to its fall speed each
        // step; one unit of fall speed is 60 * timeScale m/s.
        v.y += controller.m_GravityForce * 60f * ts;
        Vector3 input = controller.m_MotorThrottle;
        input.y = 0f;
        bool hasInput = !uiBlocked && input.sqrMagnitude > 1e-8f;
        if (hasInput) v += input.normalized * (SwingForce * dt);
        float damping = hasInput ? SwingDamping * 0.25f : SwingDamping;
        v /= 1f + damping * dt;
        if (v.magnitude > MaxRopeSpeed) v = v.normalized * MaxRopeSpeed;

        // Constraint: if this step would leave the rope sphere, move to the
        // sphere instead. The stored velocity keeps its tangential part.
        Vector3 move = v;
        Vector3 next = pos + v * dt;
        Vector3 radial = next - s.Anchor;
        float nextDist = radial.magnitude;
        if (nextDist > s.Length && nextDist > 0.001f)
        {
            Vector3 target = s.Anchor + radial * (s.Length / nextDist);
            move = (target - pos) / dt;
        }

        s.Velocity = v;
        s.DriveStart = controller.transform.position;
        s.DriveMove = move;
        s.DriveDt = dt;
        s.Driving = true;

        float k = 60f * ts;
        float sm = Mathf.Max(controller.m_SpeedModifier, 0.01f);
        // Throttle y above 0.001 also disables the grounded anti-bump push.
        controller.m_MotorThrottle = new Vector3(move.x / (k * sm), move.y / k, move.z / (k * sm));
        controller.m_FallSpeed = 0f;
        controller.m_ExternalForce = Vector3.zero;
        // Fall height restarts at the release point, not the swing's peak.
        controller.m_MaxHeight = controller.transform.position.y + Origin.position.y;
        controller.m_MaxHeightInitialFallSpeed = 0f;

        if ((reelIn || reelOut) && Time.time - s.LastMotionLog >= 2f)
        {
            s.LastMotionLog = Time.time;
            Log.Out("[GrapplingHook] Reel " + (reelIn ? "in" : "out") + " length=" + s.Length.ToString("F2") +
                " distance=" + dist.ToString("F2") + " speed=" + v.magnitude.ToString("F2"));
        }
    }

    /// <summary>FixedMove postfix: reconcile the stored velocity with what collision allowed.</summary>
    internal static void AfterMove(vp_FPController controller)
    {
        GrappleState s = state;
        if (s == null || !s.Driving || controller == null || s.Player == null ||
            s.Player.vp_FPController != controller) return;
        s.Driving = false;
        Vector3 actual = (controller.transform.position - s.DriveStart) / s.DriveDt;
        Vector3 v = s.Velocity;
        // Collision stopped part of the move (ground, wall, ceiling): keep only
        // the motion that actually happened.
        if ((actual - s.DriveMove).magnitude > 0.5f) v = actual;

        Vector3 pos = ControllerPoint(controller, s.Player);
        Vector3 offset = pos - s.Anchor;
        float dist = offset.magnitude;
        if (dist > 0.001f && dist >= s.Length - TautTolerance)
        {
            Vector3 dir = offset / dist;
            float outward = Vector3.Dot(v, dir);
            if (outward > 0f) v -= dir * outward;
        }
        s.Velocity = v;

        if (dist > s.Length + MaxReelSlack)
        {
            // Geometry blocks the pull. Keep tension without storing a large
            // correction that would snap the player once the path clears.
            s.Length = dist - MaxReelSlack;
            if (!s.ReelBlockedLogged)
            {
                s.ReelBlockedLogged = true;
                Log.Out("[GrapplingHook] Rope pull blocked by geometry at distance " + dist.ToString("F2"));
            }
        }
        else s.ReelBlockedLogged = false;

        // Clear the driven values so next step's UpdateMotor exposes pure input.
        controller.m_MotorThrottle = Vector3.zero;
        controller.m_FallSpeed = 0f;
    }

    internal static bool ConsumeReleaseFallProtection(EntityAlive entity)
    {
        if (!suppressReleaseLanding || state != null || entity == null ||
            entity != GameManager.Instance?.World?.GetPrimaryPlayer()) return false;
        suppressReleaseLanding = false;
        return true;
    }
}

[HarmonyPatch(typeof(vp_FPController), "FixedMove")]
internal static class HookControllerPhysicsPatch
{
    private static void Prefix(vp_FPController __instance)
    {
        try { Grapple.BeforeMove(__instance); }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Rope physics failed: " + ex); Grapple.Detach(false); }
    }

    private static void Postfix(vp_FPController __instance)
    {
        try { Grapple.AfterMove(__instance); }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Rope physics failed: " + ex); Grapple.Detach(false); }
    }
}

[HarmonyPatch(typeof(EntityPlayerLocal), "OnUpdateLive")]
internal static class HookPlayerTickPatch
{
    private static void Postfix(EntityPlayerLocal __instance)
    {
        try
        {
            Grapple.Tick(__instance);
            Grapple.UpdateReleaseProtection(__instance);
            HookInput.UpdateRelease(__instance);
        }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Rope update failed: " + ex); Grapple.Detach(false); }
        try { HookLoadedBoltRestorePatch.Update(__instance); }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Loaded hook restore failed: " + ex); }
    }
}

[HarmonyPatch(typeof(EntityPlayerLocal), "OnEntityDeath")]
internal static class HookDeathPatch
{
    private static void Prefix(EntityPlayerLocal __instance)
    {
        if (Grapple.State != null && Grapple.State.Player == __instance) Grapple.Detach(false);
    }
}

[HarmonyPatch(typeof(GameManager), "Cleanup")]
internal static class HookWorldExitPatch
{
    private static void Prefix() { Grapple.Detach(false); }
}

/// <summary>
/// Mounting a vehicle lets go of the rope straight away. (A rider is the one
/// with AttachedToEntity set; AttachedMainEntity is the vehicle's view of its
/// driver, which is why checking it on the player never fired.)
/// </summary>
[HarmonyPatch(typeof(EntityPlayerLocal), "AttachToEntity")]
internal static class HookVehicleEntryPatch
{
    private static void Prefix(EntityPlayerLocal __instance)
    {
        if (Grapple.State == null || Grapple.State.Player != __instance) return;
        Log.Out("[GrapplingHook] Entered a vehicle.");
        Grapple.Detach(false);
    }
}

[HarmonyPatch(typeof(EntityAlive), "fallHitGround")]
internal static class HookFallPatch
{
    private static bool Prefix(EntityAlive __instance)
    {
        return !Grapple.ConsumeReleaseFallProtection(__instance);
    }
}
