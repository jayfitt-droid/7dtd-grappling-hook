using System;
using HarmonyLib;
using UnityEngine;

internal static class HookCollisionScope
{
    [ThreadStatic] internal static bool Active;
    [ThreadStatic] internal static EntityPlayerLocal Player;
    [ThreadStatic] internal static Transform Projectile;
    [ThreadStatic] internal static ItemValue Launcher;
    [ThreadStatic] internal static int Tier;
}

[HarmonyPatch(typeof(ItemActionLauncher), "instantiateProjectile")]
internal static class HookLoadedBoltVisualPatch
{
    private static void Postfix(ItemActionData __0, Transform __result)
    {
        if (__result == null) return;
        ProjectileMoveScript projectile = __result.GetComponent<ProjectileMoveScript>();
        int tier = projectile != null ? HookMod.TierOf(projectile.itemValueProjectile) : -1;
        if (tier < 0) return;
        try
        {
            // The iron-bolt prefab can keep its visible child inactive until Fire().
            OnActivateItemGameObjectReference visual =
                __result.GetComponent<OnActivateItemGameObjectReference>();
            if (visual != null && !visual.IsActivated()) visual.ActivateItem(true);

            // Swap the stock bolt look for the hook model; the same object is
            // loaded, flies, and is copied at the anchor.
            HookAssets.DressBolt(__result, tier);

            // The launcher parents the loaded bolt to the weapon's
            // "ProjectileJoint", found once when the weapon's action data is
            // created. For the hook that lookup came up empty, so the bolt fell
            // back to the third-person right hand (layer 0), which the
            // first-person view does not draw. Find the joint on the held
            // weapon model now and move the bolt onto it.
            if (__result.parent != null && __result.parent.name == "ProjectileJoint") return;
            Transform model = __0?.invData?.model;
            Transform joint = model != null ? FindChild(model, "ProjectileJoint") : null;
            if (joint == null)
            {
                HookMod.Verbose("[GrapplingHook] Loaded hook: weapon model has no ProjectileJoint; parent=" +
                    (__result.parent != null ? __result.parent.name : "none"));
                return;
            }
            __result.SetParent(joint, false);
            __result.localPosition = Vector3.zero;
            __result.localRotation = Quaternion.identity;
            __result.localScale = Vector3.one;
            SetLayer(__result, joint.gameObject.layer);
            HookMod.Verbose("[GrapplingHook] Loaded hook moved to the weapon's ProjectileJoint (layer " +
                joint.gameObject.layer + ").");
        }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Loaded hook visual: " + ex.Message); }
    }

    private static Transform FindChild(Transform root, string name)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            if (child.name == name) return child;
        return null;
    }

    private static void SetLayer(Transform root, int layer)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = layer;
    }
}

[HarmonyPatch(typeof(ProjectileMoveScript), "Fire")]
internal static class HookLaunchDiagnosticsPatch
{
    private static void Postfix(ProjectileMoveScript __instance)
    {
        if (!HookMod.IsHook(__instance.itemValueProjectile)) return;
        HookMod.Verbose("[GrapplingHook] Projectile fired from " + __instance.previousPosition +
            " velocity=" + __instance.velocity + " gravity=" + __instance.gravity);
    }
}

[HarmonyPatch(typeof(ProjectileMoveScript), "checkCollision")]
internal static class HookProjectileCollisionPatch
{
    private static void Prefix(ProjectileMoveScript __instance)
    {
        HookCollisionScope.Tier = HookMod.TierOf(__instance.itemValueProjectile);
        HookCollisionScope.Active = HookCollisionScope.Tier >= 0;
        HookCollisionScope.Player = HookCollisionScope.Active
            ? __instance.firingEntity as EntityPlayerLocal : null;
        HookCollisionScope.Projectile = HookCollisionScope.Active ? __instance.transform : null;
        HookCollisionScope.Launcher = HookCollisionScope.Active ? __instance.itemValueLauncher : null;
    }

    private static void Postfix()
    {
        HookCollisionScope.Active = false;
        HookCollisionScope.Player = null;
        HookCollisionScope.Projectile = null;
        HookCollisionScope.Launcher = null;
    }

    private static Exception Finalizer(Exception __exception)
    {
        HookCollisionScope.Active = false;
        HookCollisionScope.Player = null;
        HookCollisionScope.Projectile = null;
        HookCollisionScope.Launcher = null;
        return __exception;
    }
}

[HarmonyPatch(typeof(ItemActionAttack), "FindHitEntity")]
internal static class HookImpactPatch
{
    private static void Prefix(WorldRayHitInfo hitInfo)
    {
        if (!HookCollisionScope.Active || HookCollisionScope.Player == null || hitInfo == null ||
            !hitInfo.bHitValid) return;
        try
        {
            // checkCollision calls FindHitEntity immediately after its impact
            // raycast, before damage handling can replace the shared hit info.
            HookMod.Verbose("[GrapplingHook] " + HookMod.TierNames[HookCollisionScope.Tier] + " hook impact tag=" + hitInfo.tag +
                " point=" + hitInfo.hit.pos + " player=" + Grapple.PlayerAnchorPoint(HookCollisionScope.Player));
            if (!GameUtils.IsBlockOrTerrain(hitInfo.tag)) return;
            Vector3i anchorBlock = Grapple.ResolveAnchorBlock(hitInfo.hit.pos, hitInfo.hit.blockPos,
                hitInfo.ray.direction);
            int tier = HookCollisionScope.Tier;
            if (Grapple.Attach(HookCollisionScope.Player, hitInfo.hit.pos, anchorBlock,
                    HookMod.Settings.MaxDistance[tier], hitInfo.ray.direction, HookCollisionScope.Launcher, tier))
                HookLine.ShowAnchorBolt(HookCollisionScope.Projectile, hitInfo.hit.pos, hitInfo.ray.direction);
        }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Impact handler: " + ex); }
    }
}

[HarmonyPatch(typeof(ItemActionAttack), "GetDamageBlock")]
internal static class HookBlockDamagePatch
{
    private static void Postfix(ref float __result)
    {
        if (HookCollisionScope.Active) __result = HookMod.Settings.BlockDamage;
    }
}

[HarmonyPatch(typeof(ItemActionAttack), "GetDamageEntity")]
internal static class HookEntityDamagePatch
{
    private static void Postfix(ref float __result)
    {
        if (HookCollisionScope.Active) __result = 0f;
    }
}

[HarmonyPatch(typeof(EntityAlive), "DamageEntity")]
internal static class HookNoEntityDamagePatch
{
    private static bool Prefix(DamageSource _damageSource, ref int __result)
    {
        if (!HookCollisionScope.Active &&
            (_damageSource == null || !HookMod.IsHook(_damageSource.AttackingItem))) return true;
        __result = 0;
        return false;
    }
}

/// <summary>
/// On a fresh game launch the held crossbow's StartHolding runs before the
/// player's stats are ready: GetMaxAmmoCount returns 0, so the game skips
/// creating the loaded bolt even though the saved ammo count is 1. The round
/// is then fired with no projectile and lost. Remember that case and create
/// the hook bolt once the magazine size is valid.
/// </summary>
[HarmonyPatch(typeof(ItemActionLauncher), "StartHolding")]
internal static class HookLoadedBoltRestorePatch
{
    private static readonly System.Reflection.MethodInfo instantiate =
        AccessTools.Method(typeof(ItemActionLauncher), "instantiateProjectile");
    private static ItemActionLauncher pendingAction;
    private static ItemActionLauncher.ItemActionDataLauncher pendingData;

    private static void Postfix(ItemActionLauncher __instance, ItemActionData __0)
    {
        pendingAction = null;
        pendingData = null;
        var data = __0 as ItemActionLauncher.ItemActionDataLauncher;
        if (data == null || !(data.invData?.holdingEntity is EntityPlayerLocal)) return;
        if (!MissingHookBolt(__instance, data)) return;
        pendingAction = __instance;
        pendingData = data;
    }

    private static bool MissingHookBolt(ItemActionLauncher action, ItemActionLauncher.ItemActionDataLauncher data)
    {
        ItemValue weapon = data.invData?.itemValue;
        if (weapon == null || weapon.Meta <= 0 || data.projectileTs == null || data.projectileTs.Count > 0) return false;
        string[] ammo = action.MagazineItemNames;
        int index = weapon.SelectedAmmoTypeIndex;
        return ammo != null && index >= 0 && index < ammo.Length && HookMod.IsHookName(ammo[index]);
    }

    /// <summary>Runs every frame for the local player; does nothing unless a bolt is owed.</summary>
    internal static void Update(EntityPlayerLocal player)
    {
        if (pendingData == null) return;
        ItemInventoryData held = player.inventory != null ? player.inventory.holdingItemData : null;
        if (held == null || held.actionData == null || !held.actionData.Contains(pendingData) ||
            pendingData.isReloading || !MissingHookBolt(pendingAction, pendingData))
        {
            pendingAction = null;
            pendingData = null;
            return;
        }
        int max = pendingAction.GetMaxAmmoCount(pendingData);
        if (max <= 0) return; // stats not ready yet
        ItemActionLauncher action = pendingAction;
        ItemActionLauncher.ItemActionDataLauncher data = pendingData;
        pendingAction = null;
        pendingData = null;
        int count = Mathf.Min(data.invData.itemValue.Meta, max);
        for (int i = 0; i < count; i++)
        {
            var bolt = instantiate.Invoke(action, new object[] { data, Vector3.zero }) as Transform;
            if (bolt != null) data.projectileTs.Add(bolt);
        }
        HookMod.Verbose("[GrapplingHook] Restored the loaded hook the game skipped at load (" +
            data.projectileTs.Count + " of " + count + ").");
    }
}
