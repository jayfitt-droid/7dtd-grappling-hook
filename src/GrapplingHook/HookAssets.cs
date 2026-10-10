using System;
using UnityEngine;

/// <summary>
/// Art from the mod's asset bundle (built by build-assets.ps1): the grappling
/// hook bolt prefab and the rope material. Loaded through the game's
/// DataLoader so the bundle is cached alongside the game's own. Everything here
/// falls back to the stock bolt and the procedural rope if the bundle is missing.
/// </summary>
internal static class HookAssets
{
    private const string BundleUri = "#@modfolder(GrapplingHook):Resources/GrapplingHook.unity3d?";
    internal const string BoltName = "GrapplingHookBolt";
    private const string RopeName = "GrapplingHookRope";

    private static bool loaded;
    private static readonly GameObject[] boltPrefabs = new GameObject[HookMod.TierNames.Length];
    private static Material ropeMaterial;
    private static bool loggedFit;

    internal static GameObject BoltPrefab(int tier) { Load(); return boltPrefabs[tier]; }
    internal static Material RopeMaterial { get { Load(); return ropeMaterial; } }

    private static void Load()
    {
        if (loaded) return;
        loaded = true;
        int models = 0;
        for (int i = 0; i < boltPrefabs.Length; i++)
        {
            // One prefab per tier: the same mesh with that tier's finish.
            try { boltPrefabs[i] = DataLoader.LoadAsset<GameObject>(BundleUri + BoltName + HookMod.TierNames[i], false); }
            catch (Exception ex) { Log.Warning("[GrapplingHook] " + HookMod.TierNames[i] + " hook model failed to load: " + ex.Message); }
            if (boltPrefabs[i] != null) models++;
        }
        try { ropeMaterial = DataLoader.LoadAsset<Material>(BundleUri + RopeName, false); }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Rope material failed to load: " + ex.Message); }
        Log.Out("[GrapplingHook] Asset bundle: " + models + " of " + boltPrefabs.Length + " hook models loaded" +
            ", rope material " + (ropeMaterial != null ? "loaded" : "missing") + ".");
    }

    /// <summary>
    /// Replaces the stock bolt's look on a projectile with the hook model. The
    /// stock renderers stay (the game toggles them) but stop drawing; the hook
    /// is parented under the same object so it shows and hides with them. Its
    /// tip is placed on the stock bolt's tip, pointing along the projectile's
    /// forward axis, which is the flight direction.
    /// </summary>
    internal static void DressBolt(Transform projectile, int tier)
    {
        GameObject prefab = BoltPrefab(tier);
        if (projectile == null || prefab == null) return;
        if (FindChild(projectile, BoltName) != null) return;

        Renderer first = null;
        bool any = false;
        Bounds local = default(Bounds);
        Matrix4x4 toProjectile = projectile.worldToLocalMatrix;
        foreach (Renderer renderer in projectile.GetComponentsInChildren<Renderer>(true))
        {
            Mesh mesh = MeshOf(renderer);
            if (mesh == null) continue;
            Bounds b = Transform(toProjectile * renderer.transform.localToWorldMatrix, mesh.bounds);
            if (any) local.Encapsulate(b); else local = b;
            any = true;
            if (first == null) first = renderer;
            renderer.forceRenderingOff = true;
        }
        if (first == null)
        {
            Log.Out("[GrapplingHook] Projectile has no bolt mesh to replace.");
            return;
        }

        GameObject hook = UnityEngine.Object.Instantiate(prefab, projectile, false);
        hook.name = BoltName;
        // The prefab pivot is the tip. Put the rope eye where the stock bolt's
        // nock is, so the hook rests on the string like a bolt.
        Transform eye = FindChild(hook.transform, "RopeAttach");
        float tipZ = eye != null ? local.min.z - eye.localPosition.z : local.max.z;
        hook.transform.localPosition = new Vector3(local.center.x, local.center.y, tipZ);
        hook.transform.localRotation = Quaternion.identity;
        Vector3 scale = projectile.lossyScale;
        hook.transform.localScale = new Vector3(1f / scale.x, 1f / scale.y, 1f / scale.z);
        // Parent next to the stock mesh, but never outside the projectile: the
        // iron bolt's renderer is on the projectile root, whose parent is the
        // launcher's hand joint.
        Transform parent = first.transform != projectile && first.transform.parent != null
            ? first.transform.parent : projectile;
        hook.transform.SetParent(parent, true);
        foreach (Transform t in hook.GetComponentsInChildren<Transform>(true))
            t.gameObject.layer = first.gameObject.layer;
        hook.AddComponent<HookBoltVisibility>().Stock = first;

        if (!loggedFit)
        {
            loggedFit = true;
            Log.Out("[GrapplingHook] Stock bolt bounds in projectile space: center=" + local.center.ToString("F3") +
                " size=" + local.size.ToString("F3") + " projectile scale=" + scale.ToString("F3") +
                "; hook tip placed at z=" + tipZ.ToString("F3") + " under " + parent.name + ".");
        }
    }

    /// <summary>A marker (Tip or RopeAttach) of the hook on a dressed projectile, or null.</summary>
    internal static Transform Marker(Transform projectile, string name)
    {
        Transform hook = projectile != null ? FindChild(projectile, BoltName) : null;
        return hook != null ? FindChild(hook, name) : null;
    }

    internal static Mesh MeshOf(Renderer renderer)
    {
        var skinned = renderer as SkinnedMeshRenderer;
        if (skinned != null) return skinned.sharedMesh;
        if (!(renderer is MeshRenderer)) return null;
        MeshFilter filter = renderer.GetComponent<MeshFilter>();
        return filter != null ? filter.sharedMesh : null;
    }

    private static Transform FindChild(Transform root, string name)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            if (child.name == name) return child;
        return null;
    }

    private static Bounds Transform(Matrix4x4 m, Bounds b)
    {
        var result = new Bounds(m.MultiplyPoint3x4(b.center), Vector3.zero);
        Vector3 e = b.extents;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
            result.Encapsulate(m.MultiplyPoint3x4(b.center + corner));
        }
        return result;
    }
}

/// <summary>
/// Shows the hook model only while the stock bolt renderer it replaces would
/// draw, since the game may hide the loaded bolt by disabling that renderer.
/// </summary>
internal sealed class HookBoltVisibility : MonoBehaviour
{
    internal Renderer Stock;
    private Renderer[] own;

    private void LateUpdate()
    {
        if (Stock == null) return;
        if (own == null) own = GetComponentsInChildren<Renderer>(true);
        bool show = Stock.enabled && Stock.gameObject.activeInHierarchy;
        foreach (Renderer r in own)
            if (r.enabled != show) r.enabled = show;
    }
}
