using System;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Rope and anchor-bolt visuals. Everything here is local rendering only and is
/// created once per attachment and destroyed on detach.
/// </summary>
internal static class HookLine
{
    private const int Segments = 24;
    private const int Sides = 10;
    private const float RopeRadius = 0.008f;
    // One texture tile is one lay length of the rope (the baked tile matches a
    // 1.6 cm rope with a 5.4 cm lay); u runs along the rope, v around it.
    private const float RopeLayMeters = 0.054f;
    // Tail of the stock bolt behind the anchor point, used when the hook model
    // (with its rope eye) is not available.
    private const float BoltTailOffset = 0.3f;
    // Waist rope point without the crossbow in hand: this fraction of the eye
    // height below the camera, and this far ahead of it. Zero keeps the rope
    // end against the body; 0.2 m read as floating a foot out when looking down.
    private const float WaistDrop = 0.4f;
    private const float WaistForward = 0f;
    // How far the hook's nose sits inside the block it hit.
    private const float HookTipPenetration = 0.03f;

    private static GameObject ropeObject;
    private static Mesh ropeMesh;
    private static Material material;
    private static bool ownsMaterial;
    private static Texture2D texture;
    private static Texture2D normalMap;
    private static GameObject anchorBolt;
    private static Vector3 anchorBoltWorld;
    private static Vector3 ropeEyeOffset;
    private static bool hasRopeEye;
    private static readonly Vector3[] points = new Vector3[Segments + 1];
    private static readonly Vector3[] vertices = new Vector3[(Segments + 1) * (Sides + 1) + 2 * (Sides + 1)];
    private static readonly Vector3[] normals = new Vector3[vertices.Length];
    private static readonly Vector4[] tangents = new Vector4[vertices.Length];
    private static readonly Vector2[] uvs = new Vector2[vertices.Length];

    internal static void Show()
    {
        if (ropeObject != null) return;
        material = HookAssets.RopeMaterial;
        ownsMaterial = material == null;
        if (ownsMaterial) material = CreateRopeMaterial();
        else Log.Out("[GrapplingHook] Rope uses the bundled rope material.");
        if (material == null)
        {
            Log.Warning("[GrapplingHook] No rope shader found.");
            return;
        }
        ropeObject = new GameObject("GrapplingHookRope");
        ropeMesh = new Mesh { name = "GrapplingHookRope" };
        ropeMesh.MarkDynamic();
        ropeMesh.vertices = vertices;
        ropeMesh.triangles = BuildTriangles();
        ropeObject.AddComponent<MeshFilter>().sharedMesh = ropeMesh;
        var renderer = ropeObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.On;
        renderer.receiveShadows = true;
        ropeObject.AddComponent<HookLineDriver>();
        // The camera moves late in the frame. Updating just before the main
        // camera renders keeps the rope from trailing it by a frame.
        Camera.onPreCull += OnPreCull;
    }

    /// <summary>Tube quads, then a fan cap at each end.</summary>
    private static int[] BuildTriangles()
    {
        int ring = Sides + 1;
        var tris = new int[Segments * Sides * 6 + 2 * Sides * 3];
        int t = 0;
        for (int i = 0; i < Segments; i++)
        for (int j = 0; j < Sides; j++)
        {
            int a = i * ring + j, b = a + 1, c = a + ring, d = c + 1;
            tris[t++] = a; tris[t++] = c; tris[t++] = b;
            tris[t++] = b; tris[t++] = c; tris[t++] = d;
        }
        int startCap = (Segments + 1) * ring, endCap = startCap + ring;
        for (int j = 0; j < Sides; j++)
        {
            tris[t++] = startCap + Sides; tris[t++] = startCap + j; tris[t++] = startCap + j + 1;
            tris[t++] = endCap + Sides; tris[t++] = endCap + j + 1; tris[t++] = endCap + j;
        }
        return tris;
    }

    /// <summary>
    /// Fallback when the bundle is missing: a lit Standard material with the
    /// procedural texture, or the unlit sprite shader with a darker tint.
    /// </summary>
    private static Material CreateRopeMaterial()
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
            Log.Out("[GrapplingHook] Rope uses the lit Standard shader with the procedural texture.");
            return m;
        }
        Shader unlit = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Texture");
        if (unlit == null) return null;
        Log.Out("[GrapplingHook] Rope uses the unlit " + unlit.name + " shader.");
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
                Log.Out("[GrapplingHook] Bolt model had no meshes to show at the anchor.");
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
            Log.Out("[GrapplingHook] Anchor bolt shown with " + copy.transform.childCount + " mesh part(s)" +
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
            if (ropeMesh == null) return;
            Vector3 start = RopeStart(state, origin);
            Vector3 anchorWorld = state.Anchor;
            if (anchorBolt != null && hasRopeEye)
                anchorWorld = anchorBoltWorld + ropeEyeOffset;
            else if (anchorBolt != null && state.AnchorDirection.sqrMagnitude > 0.5f)
                anchorWorld -= state.AnchorDirection * BoltTailOffset;
            Vector3 end = anchorWorld - origin;

            // Sag only with slack: a parabola whose arc length matches the
            // unused rope (arc ≈ span + 8h²/3span).
            float span = Vector3.Distance(start, end);
            float slack = Mathf.Max(0f, state.Length -
                Vector3.Distance(Grapple.PlayerAnchorPoint(state.Player), state.Anchor));
            float sag = span > 0.01f ? Mathf.Min(Mathf.Sqrt(3f * span * slack / 8f), span * 0.5f) : 0f;
            for (int i = 0; i <= Segments; i++)
            {
                float t = (float)i / Segments;
                points[i] = Vector3.Lerp(start, end, t) + Vector3.down * (4f * sag * t * (1f - t));
            }
            BuildTube();
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

    /// <summary>
    /// Rings of vertices around the rope path. The ring frame is carried along
    /// the path by parallel transport so the tube doesn't twist; u is metres
    /// along the rope over the lay length and v goes once around. Tangents
    /// follow +u and the binormal +v, which is how the normal map was baked.
    /// </summary>
    private static void BuildTube()
    {
        int ring = Sides + 1;
        Vector3 forward = (points[1] - points[0]).normalized;
        if (forward.sqrMagnitude < 0.5f) forward = Vector3.forward;
        Vector3 side = Vector3.Cross(forward, Mathf.Abs(forward.y) < 0.95f ? Vector3.up : Vector3.right).normalized;
        float arc = 0f;
        float tangentSign = 1f;
        for (int i = 0; i <= Segments; i++)
        {
            Vector3 dir = i < Segments ? points[i + 1] - points[i] : points[i] - points[i - 1];
            if (i > 0) arc += (points[i] - points[i - 1]).magnitude;
            if (dir.sqrMagnitude > 1e-10f)
            {
                Vector3 next = dir.normalized;
                side = Quaternion.FromToRotation(forward, next) * side;
                forward = next;
            }
            Vector3 up = Vector3.Cross(side, forward);
            float u = arc / RopeLayMeters;
            for (int j = 0; j <= Sides; j++)
            {
                float a = 2f * Mathf.PI * j / Sides;
                Vector3 n = Mathf.Cos(a) * side + Mathf.Sin(a) * up;
                int k = i * ring + j;
                vertices[k] = points[i] + n * RopeRadius;
                normals[k] = n;
                if (i == 0 && j == 0)
                {
                    // Unity's binormal is cross(normal, tangent) * w; make it +v.
                    Vector3 aroundV = -Mathf.Sin(a) * side + Mathf.Cos(a) * up;
                    tangentSign = Vector3.Dot(Vector3.Cross(n, forward), aroundV) >= 0f ? 1f : -1f;
                }
                tangents[k] = new Vector4(forward.x, forward.y, forward.z, tangentSign);
                uvs[k] = new Vector2(u, (float)j / Sides);
            }
        }

        // End caps: a ring facing outward plus a centre vertex at index Sides.
        WriteCap((Segments + 1) * ring, points[0], -(points[1] - points[0]).normalized, 0);
        WriteCap((Segments + 2) * ring, points[Segments],
            (points[Segments] - points[Segments - 1]).normalized, Segments * ring);

        ropeMesh.vertices = vertices;
        ropeMesh.normals = normals;
        ropeMesh.tangents = tangents;
        ropeMesh.uv = uvs;
        ropeMesh.RecalculateBounds();
    }

    private static void WriteCap(int at, Vector3 centre, Vector3 facing, int ringStart)
    {
        Vector3 tangent = (vertices[ringStart] - centre).normalized;
        for (int j = 0; j < Sides; j++)
        {
            vertices[at + j] = vertices[ringStart + j];
            normals[at + j] = facing;
            tangents[at + j] = new Vector4(tangent.x, tangent.y, tangent.z, 1f);
            uvs[at + j] = uvs[ringStart + j];
        }
        vertices[at + Sides] = centre;
        normals[at + Sides] = facing;
        tangents[at + Sides] = new Vector4(tangent.x, tangent.y, tangent.z, 1f);
        uvs[at + Sides] = new Vector2(uvs[ringStart].x, 0.5f);
    }

    internal static void Hide()
    {
        Camera.onPreCull -= OnPreCull;
        HideAnchorBolt();
        if (ropeObject != null) UnityEngine.Object.Destroy(ropeObject);
        if (ropeMesh != null) UnityEngine.Object.Destroy(ropeMesh);
        // The bundled material is a shared asset; only destroy our own.
        if (material != null && ownsMaterial) UnityEngine.Object.Destroy(material);
        ropeObject = null;
        ropeMesh = null;
        material = null;
    }
}

/// <summary>Fallback per-frame update for render pipelines without Camera.onPreCull.</summary>
internal sealed class HookLineDriver : MonoBehaviour
{
    private void LateUpdate() { HookLine.Update(Grapple.State); }
}
