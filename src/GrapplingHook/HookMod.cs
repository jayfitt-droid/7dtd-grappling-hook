using System;
using System.IO;
using System.Reflection;
using HarmonyLib;

public sealed class ModApi : IModApi
{
    public void InitMod(Mod mod)
    {
        try
        {
            HookMod.Directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
            HookMod.Settings = HookSettings.Load(HookMod.Directory);
            new Harmony("jason.grapplinghook.rewrite").PatchAll(Assembly.GetExecutingAssembly());
            HookConsole.Register(SdtdConsole.Instance);
            Log.Out("[GrapplingHook] Rewrite initialized.");
        }
        catch (Exception ex)
        {
            Log.Error("[GrapplingHook] Initialization failed: " + ex);
        }
    }
}

internal static class HookMod
{
    // Three tiers of the same hook: index 0 scrap, 1 iron, 2 steel. They differ
    // in finish, recipe and how far they can grapple.
    internal static readonly string[] TierNames = { "Scrap", "Iron", "Steel" };
    internal static readonly string[] ItemNames =
    {
        "ammoGrapplingHookScrap", "ammoGrapplingHookIron", "ammoGrapplingHookSteel"
    };
    internal static readonly string[] SchematicNames =
    {
        "ammoGrapplingHookIronSchematic", "ammoGrapplingHookSteelSchematic"
    };
    internal static string Directory = "";
    internal static HookSettings Settings = new HookSettings();

    private static object cachedItemList;
    private static readonly int[] cachedTypes = { -1, -1, -1 };

    /// <summary>
    /// The hook tier of an item, or -1 if it is not a grappling hook. Called
    /// for every projectile collision and entity damage, so the item types are
    /// cached. The cache is rebuilt when the game replaces its item list (world
    /// load) or until the hook items exist after the XML merge.
    /// </summary>
    internal static int TierOf(ItemValue item)
    {
        if (item == null || item.IsEmpty()) return -1;
        if (cachedTypes[0] < 0 || !ReferenceEquals(cachedItemList, ItemClass.list))
        {
            for (int i = 0; i < ItemNames.Length; i++)
            {
                ItemValue hook = ItemClass.GetItem(ItemNames[i], false);
                cachedTypes[i] = hook != null && !hook.IsEmpty() ? hook.type : -1;
            }
            cachedItemList = ItemClass.list;
        }
        for (int i = 0; i < cachedTypes.Length; i++)
            if (cachedTypes[i] >= 0 && item.type == cachedTypes[i]) return i;
        return -1;
    }

    internal static bool IsHook(ItemValue item) { return TierOf(item) >= 0; }

    internal static bool IsHookName(string itemName) { return Array.IndexOf(ItemNames, itemName) >= 0; }

    internal static void Reload()
    {
        Settings = HookSettings.Load(Directory);
    }
}
