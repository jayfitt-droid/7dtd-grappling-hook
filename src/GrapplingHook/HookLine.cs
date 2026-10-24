using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Rope and anchor-bolt visuals. Everything here is local rendering only and is
/// created once per attachment and destroyed on detach.
/// </summary>
internal static class HookLine
{
    // Tail of the stock bolt behind the anchor point, used when the hook model
    // (with its rope eye) is not available.
    private const float BoltTailOffset = 0.3f;
    // Waist rope point without the crossbow in hand: this fraction of the eye
    // height below the camera, and this far ahead of it. Zero keeps the rope
    // end against the body; 0.2 m read as floating a foot out when looking down.
    private const float WaistDrop = 0.4f;
    private const float WaistForward = 0f;
    // How far the hook's nose sits inside the block it hit.
    internal const float HookTipPenetration = 0.03f;

    private static RopeTube rope;
    private static Material material;
    private static bool ownsMaterial;
    private static Material sharedFallback;
    private static Texture2D texture;
    private static Texture2D normalMap;
    private static GameObject anchorBolt;
    private static Vector3 anchorBoltWorld;
    private static Vector3 ropeEyeOffset;
    private static bool hasRopeEye;

    internal static void Show()
    {
        if (rope != null) return;
        material = HookAssets.RopeMaterial;
        ownsMaterial = material == null;
        if (ownsMaterial) material = CreateRopeMaterial();
        else HookMod.Verbose("[GrapplingHook] Rope uses the bundled rope material.");
        if (material == null)
        {
            Log.Warning("[GrapplingHook] No rope shader found.");
            return;
        }
        rope = new RopeTube("GrapplingHookRope", material);
        rope.GameObject.AddComponent<HookLineDriver>();
        // The camera moves late in the frame. Updating just before the main
        // camera renders keeps the rope from trailing it by a frame.
        Camera.onPreCull += OnPreCull;
    }

    /// <summary>
    /// Material for other players' ropes: the bundled one, or one fallback
    /// material made once and kept (several ropes share it).
    /// </summary>
    internal static Material SharedRopeMaterial()
    {
        Material bundled = HookAssets.RopeMaterial;
        if (bundled != null) return bundled;
        if (sharedFallback == null) sharedFallback = CreateRopeMaterial();
        return sharedFallback;
    }


    /// <summary>
    /// Fallback when the bundle is missing: a lit Standard material with the
    /// procedural texture, or the unlit sprite shader with a darker tint.
    /// </summary>
    internal static Material CreateRopeMaterial()
    {
        if (texture == null) CreateRopeTextures(out texture, out normalMap);
        Shader lit = Shader.Find("Standard");
        if (lit != null)
        {
            var m = new Material(lit) { mainTexture = texture, color = Color.white };
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.05f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_BumpMap"))
            {
                m.SetTexture("_BumpMap", normalMap);
                m.SetFloat("_BumpScale", 1f);
                m.EnableKeyword("_NORMALMAP");
            }
            HookMod.Verbose("[GrapplingHook] Rope uses the lit Standard shader with the procedural texture.");
            return m;
        }
        Shader unlit = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Texture");
        if (unlit == null) return null;
        HookMod.Verbose("[GrapplingHook] Rope uses the unlit " + unlit.name + " shader.");
        return new Material(unlit) { mainTexture = texture, color = new Color(0.75f, 0.75f, 0.75f) };
    }

    /// <summary>
    /// Procedural three-strand laid rope (natural manila) for the fallback
    /// material. One tile is one lay length: u runs along the rope, v around
    /// it, and both wrap, so the strand seams are straight diagonals. Each
    /// strand bulges, carries near-axial yarns with their own tint, and has
    /// loose fibre noise; the height field also produces a normal map.
    /// </summary>
    private static void CreateRopeTextures(out Texture2D albedo, out Texture2D normals)
    {
        const int width = 256, height = 128;
        const int strands = 3;
        const float yarnsPerStrand = 7f;
        var heights = new float[width * height];
        var colors = new Color[width * height];
        Color manila = new Color(0.66f, 0.53f, 0.35f);
        Color groove = new Color(0.20f, 0.15f, 0.09f);
        Color[] strandTint =
        {
            new Color(1.00f, 1.00f, 1.00f),
            new Color(0.95f, 0.94f, 0.92f),
            new Color(1.03f, 1.01f, 0.96f)
        };
        for (int y = 0; y < height; y++)
        {
            float v = (float)y / height;
            for (int x = 0; x < width; x++)
            {
                float u = (float)x / width;
                // Helix seam: one turn around the rope per lay length.
                float phase = strands * (u + v);
                float cell = Mathf.Floor(phase);
                float s = phase - cell;                       // 0..1 across one strand
                int strand = ((int)cell % strands + strands) % strands;
                float bulge = Mathf.Pow(Mathf.Sin(Mathf.PI * s), 0.7f);

                // Yarns lie nearly along the rope, tilted against the strand.
                // Tint is keyed by strand, not cell, so the tile wraps cleanly.
                float yarnCoord = v * yarnsPerStrand * strands - s * 2.5f;
                float yarnIndex = Mathf.Floor(yarnCoord);
                float yarnRidge = Mathf.Sin(Mathf.PI * (yarnCoord - yarnIndex));
                float yarnTint = 0.88f + 0.24f * Hash(strand * 131, ((int)yarnIndex % 21 + 21) % 21);

                // Fibre fuzz: value noise stretched along the rope, wrapping both ways.
                float fuzz = 0.6f * WrappedNoise(x, y, width, height, 16, 2) +
                             0.4f * WrappedNoise(x, y, width, height, 4, 1);

                heights[y * width + x] = 0.8f * bulge + 0.12f * yarnRidge * bulge + 0.08f * fuzz;

                Color c = manila * strandTint[strand] * yarnTint;
                c *= 0.82f + 0.18f * yarnRidge;
                c *= 0.8f + 0.4f * fuzz;
                c = Color.Lerp(groove, c, Mathf.SmoothStep(0f, 1f, bulge * 1.05f));
                c.a = 1f;
                colors[y * width + x] = c;
            }
        }

        albedo = new Texture2D(width, height, TextureFormat.RGBA32, true)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4,
            name = "GrapplingHookRopeTexture"
        };
        albedo.SetPixels(colors);
        albedo.Apply(true, false);

        // Tangent-space normals from the height field. Written as r=1, g=y,
        // b=y, a=x so both the DXT5nm (x in alpha) and RG unpack paths decode.
        const float strength = 6f;
        var packedNormals = new Color[width * height];
        for (int y = 0; y < height; y++)
        {
            int yUp = (y + 1) % height, yDown = (y + height - 1) % height;
            for (int x = 0; x < width; x++)
            {
                int xRight = (x + 1) % width, xLeft = (x + width - 1) % width;
                float dx = heights[y * width + xRight] - heights[y * width + xLeft];
                float dy = heights[yUp * width + x] - heights[yDown * width + x];
                Vector3 n = new Vector3(-dx * strength, -dy * strength, 1f).normalized;
                float nx = n.x * 0.5f + 0.5f, ny = n.y * 0.5f + 0.5f;
                packedNormals[y * width + x] = new Color(1f, ny, ny, nx);
            }
        }
        normals = new Texture2D(width, height, TextureFormat.RGBA32, true, true)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4,
            name = "GrapplingHookRopeNormals"
        };
        normals.SetPixels(packedNormals);
        normals.Apply(true, false);
    }

    /// <summary>Deterministic 0..1 hash of two integers.</summary>
    private static float Hash(int a, int b)
    {
        unchecked
        {
            uint h = (uint)a * 374761393u + (uint)b * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }

    /// <summary>
    /// Bilinear value noise on a grid of cellU × cellV pixels; the lattice
    /// wraps at the texture size so the texture tiles both ways.
    /// </summary>
    private static float WrappedNoise(int x, int y, int width, int height, int cellU, int cellV)
    {
        int cellsU = width / cellU, cellsV = height / cellV;
        float fu = (float)x / cellU, fv = (float)y / cellV;
        int u0 = (int)fu, v0 = (int)fv;
        float tu = fu - u0, tv = fv - v0;
        tu = tu * tu * (3f - 2f * tu);
        tv = tv * tv * (3f - 2f * tv);
        int seed = cellU * 7919;
        int u1 = (u0 + 1) % cellsU, v1 = (v0 + 1) % cellsV;
        u0 %= cellsU;
        v0 %= cellsV;
        float a = Hash(seed + u0, v0), b = Hash(seed + u1, v0);
        float c = Hash(seed + u0, v1), d = Hash(seed + u1, v1);
        return Mathf.Lerp(Mathf.Lerp(a, b, tu), Mathf.Lerp(c, d, tu), tv);
    }

    /// <summary>
    /// Shows a copy of the bolt stuck where it hit. The real non-sticky
    /// projectile is removed by the game on impact, so its meshes are copied
    /// into a plain object posed exactly as the bolt was at the collision.
    /// </summary>
    internal static void ShowAnchorBolt(Transform projectile, Vector3 hitWorld, Vector3 direction)
    {
        HideAnchorBolt();
        if (projectile == null) return;
        try
        {
            GameObject copy = CopyMeshes(projectile, "GrapplingHookAnchorBolt");
            if (copy == null)
            {
                HookMod.Verbose("[GrapplingHook] Bolt model had no meshes to show at the anchor.");
                return;
            }
            anchorBolt = copy;
            // The copy keeps the projectile's flight orientation, so the hook's
            // markers keep their offsets from the pivot. Seat the hook by its
            // own tip: only the nose goes into the block, leaving the hub at
            // the surface and the claws against the face. The stock bolt is
            // seated the way the game seats sticky bolts (StickyOffset .13).
            Vector3 dir = direction.sqrMagnitude > 1e-6f ? direction.normalized : projectile.forward;
            Transform tip = HookAssets.Marker(projectile, "Tip");
            Transform eye = HookAssets.Marker(projectile, "RopeAttach");
            anchorBoltWorld = tip != null
                ? hitWorld + dir * HookTipPenetration - (tip.position - projectile.position)
                : hitWorld - dir * 0.13f;
            copy.transform.position = anchorBoltWorld - Origin.position;
            // The rope ties to the hook's eye.
            hasRopeEye = eye != null;
            if (hasRopeEye) ropeEyeOffset = eye.position - projectile.position;
            HookMod.Verbose("[GrapplingHook] Anchor bolt shown with " + copy.transform.childCount + " mesh part(s)" +
                (tip != null ? "; hook tip " + HookTipPenetration.ToString("F2") + " m into the block" : "") +
                (hasRopeEye ? "; rope eye " + ropeEyeOffset.magnitude.ToString("F2") + " m from the pivot." : "."));
        }
        catch (Exception ex) { Log.Warning("[GrapplingHook] Anchor bolt visual failed: " + ex.Message); }
    }

    internal static void HideAnchorBolt()
    {
        if (anchorBolt != null) UnityEngine.Object.Destroy(anchorBolt);
        anchorBolt = null;
        hasRopeEye = false;
    }

    private static GameObject CopyMeshes(Transform source, string name)
    {
        var root = new GameObject(name);
        root.transform.SetPositionAndRotation(source.position, source.rotation);
        root.transform.localScale = source.lossyScale;
        Matrix4x4 toSource = source.worldToLocalMatrix;
        int count = 0;
        foreach (Renderer renderer in source.GetComponentsInChildren<Renderer>(true))
        {
            // Stock bolt renderers hidden in favour of the hook model.
            if (renderer.forceRenderingOff) continue;
            Mesh mesh = HookAssets.MeshOf(renderer);
            if (mesh == null) continue;
            Matrix4x4 local = toSource * renderer.transform.localToWorldMatrix;
            var part = new GameObject(renderer.name);
            part.transform.SetParent(root.transform, false);
            part.transform.localPosition = local.GetColumn(3);
            part.transform.localRotation = local.rotation;
            part.transform.localScale = local.lossyScale;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var meshRenderer = part.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterials = renderer.sharedMaterials;
            meshRenderer.shadowCastingMode = ShadowCastingMode.On;
            count++;
        }
        if (count == 0)
        {
            UnityEngine.Object.Destroy(root);
            return null;
        }
        return root;
    }

    private static void OnPreCull(Camera camera)
    {
        if (camera != null && camera == Camera.main) Update(Grapple.State);
    }

    internal static void Update(GrappleState state)
    {
        if (state == null || state.Player == null) return;
        try
        {
            // Entity positions and hit points are world coordinates; Unity
            // renderers are relative to the floating origin, which can move.
            Vector3 origin = Origin.position;
            if (anchorBolt != null) anchorBolt.transform.position = anchorBoltWorld - origin;
            if (rope == null) return;
            Vector3 start = RopeStart(state, origin);
            Vector3 anchorWorld = state.Anchor;
            if (anchorBolt != null && hasRopeEye)
                anchorWorld = anchorBoltWorld + ropeEyeOffset;
            else if (anchorBolt != null && state.AnchorDirection.sqrMagnitude > 0.5f)
                anchorWorld -= state.AnchorDirection * BoltTailOffset;
            Vector3 end = anchorWorld - origin;
            rope.Draw(start, end, state.Length -
                Vector3.Distance(Grapple.PlayerAnchorPoint(state.Player), state.Anchor));
        }
        catch (Exception ex)
        {
            Log.Warning("[GrapplingHook] Rope render failed: " + ex.Message);
            Hide();
        }
    }


    /// <summary>
    /// Where the player's end of the rope is drawn, in Unity space. First
    /// person with the firing crossbow in hand: beside the crossbow. First
    /// person with anything else: at the waist, straight below the eyes, so
    /// the rope doesn't hang in mid-air in front of a small weapon or tool. It follows the camera rather than the entity
    /// position, which lags the view. Third person: the body's rope point.
    /// </summary>
    private static Vector3 RopeStart(GrappleState state, Vector3 origin)
    {
        Camera camera = Camera.main;
        if (camera == null || !state.Player.bFirstPersonView)
            return Grapple.PlayerAnchorPoint(state.Player) - origin;
        Transform view = camera.transform;
        if (Grapple.HoldingCrossbow(state))
            return view.position + view.forward * 0.7f + view.right * 0.22f - view.up * 0.26f;
        Vector3 facing = Quaternion.Euler(0f, view.eulerAngles.y, 0f) * Vector3.forward;
        return view.position + Vector3.down * (state.Player.GetEyeHeight() * WaistDrop) + facing * WaistForward;
    }

    internal static void Hide()
    {
        Camera.onPreCull -= OnPreCull;
        HideAnchorBolt();
        if (rope != null) rope.Destroy();
        // The bundled material is a shared asset; only destroy our own.
        if (material != null && ownsMaterial) UnityEngine.Object.Destroy(material);
        rope = null;
        material = null;
    }
}

/// <summary>Fallback per-frame update for render pipelines without Camera.onPreCull.</summary>
internal sealed class HookLineDriver : MonoBehaviour
{
    private void LateUpdate() { HookLine.Update(Grapple.State); }
}
