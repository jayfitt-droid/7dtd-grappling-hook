using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Batch build for the mod's art. Run from build-assets.ps1:
///   Unity -batchmode -projectPath unity/GrapplingHookAssets -executeMethod GrapplingHookBuild.BuildAll
/// Imports the Blender outputs in Assets/Source, makes the Standard materials
/// and the bolt prefab, renders the 160×160 inventory icon, and builds one
/// asset bundle for Windows into the modlet's Resources folder.
/// </summary>
public static class GrapplingHookBuild
{
    private const string Source = "Assets/Source/";
    private const string Generated = "Assets/Generated/";
    private const string BundleName = "grapplinghook.unity3d";
    private const string BoltAsset = "GrapplingHookBolt";
    // One mesh, three finishes. Assets are GrapplingHookBolt<Tier>; icons are
    // named after the items, ammoGrapplingHook<Tier>.
    private static readonly string[] Tiers = { "Scrap", "Iron", "Steel" };
    private const string RopeAsset = "GrapplingHookRope";
    private const int IconSize = 160;
    private const int IconSupersample = 4;

    private static string ModletDir =>
        Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "../../modlet/GrapplingHook"));

    public static void BuildAll()
    {
        int exitCode = 0;
        try
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            if (!AssetDatabase.IsValidFolder("Assets/Generated")) AssetDatabase.CreateFolder("Assets", "Generated");
            ConfigureImports();
            CreateRopeMaterial();
            foreach (string stale in new[] { BoltAsset + ".mat", BoltAsset + ".prefab" })
                AssetDatabase.DeleteAsset(Generated + stale);
            foreach (string tier in Tiers)
            {
                GameObject prefab = CreateBoltPrefab(tier, CreateHookMaterial(tier));
                RenderIcon(prefab, Path.Combine(ModletDir, "UIAtlases/ItemIconAtlas/ammoGrapplingHook" + tier + ".png"), tier);
            }
            BuildBundle();
            Debug.Log("[GrapplingHookBuild] Done.");
        }
        catch (Exception ex)
        {
            Debug.LogError("[GrapplingHookBuild] Failed: " + ex);
            exitCode = 1;
        }
        if (Application.isBatchMode) EditorApplication.Exit(exitCode);
    }

    // ------------------------------------------------------------------ import

    private static void ConfigureImports()
    {
        foreach (string tier in Tiers)
        {
            string prefix = Source + "hook_" + tier.ToLowerInvariant();
            Texture(prefix + "_albedo.png", TextureImporterType.Default, true, 1024);
            Texture(prefix + "_normal.png", TextureImporterType.NormalMap, false, 1024);
            Texture(prefix + "_metallic.png", TextureImporterType.Default, false, 1024);
        }
        Texture(Source + "hook_occlusion.png", TextureImporterType.Default, false, 512);
        Texture(Source + "rope_albedo.png", TextureImporterType.Default, true, 512);
        Texture(Source + "rope_normal.png", TextureImporterType.NormalMap, false, 512);

        var model = (ModelImporter)AssetImporter.GetAtPath(Source + "GrapplingHookBolt.fbx");
        model.globalScale = 1f;
        model.useFileScale = true;
        model.materialImportMode = ModelImporterMaterialImportMode.None;
        model.importNormals = ModelImporterNormals.Import;
        // Blender bakes against MikkTSpace tangents.
        model.importTangents = ModelImporterTangents.CalculateMikk;
        model.importAnimation = false;
        model.animationType = ModelImporterAnimationType.None;
        model.importCameras = false;
        model.importLights = false;
        model.isReadable = false;
        model.SaveAndReimport();
    }

    private static void Texture(string path, TextureImporterType type, bool srgb, int maxSize)
    {
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = type;
        importer.sRGBTexture = srgb;
        importer.mipmapEnabled = true;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.filterMode = FilterMode.Trilinear;
        importer.anisoLevel = 4;
        importer.maxTextureSize = maxSize;
        importer.textureCompression = TextureImporterCompression.CompressedHQ;
        importer.SaveAndReimport();
    }

    // --------------------------------------------------------------- materials

    private static Material CreateHookMaterial(string tier)
    {
        string prefix = Source + "hook_" + tier.ToLowerInvariant();
        var m = new Material(Shader.Find("Standard")) { name = BoltAsset + tier };
        m.SetTexture("_MainTex", Load<Texture2D>(prefix + "_albedo.png"));
        m.SetTexture("_BumpMap", Load<Texture2D>(prefix + "_normal.png"));
        m.SetFloat("_BumpScale", 1f);
        m.SetTexture("_MetallicGlossMap", Load<Texture2D>(prefix + "_metallic.png"));
        m.SetFloat("_GlossMapScale", 1f);
        m.SetFloat("_SmoothnessTextureChannel", 0f);   // smoothness in the metallic map's alpha
        m.SetTexture("_OcclusionMap", Load<Texture2D>(Source + "hook_occlusion.png"));
        m.SetFloat("_OcclusionStrength", 0.8f);
        m.EnableKeyword("_NORMALMAP");
        m.EnableKeyword("_METALLICGLOSSMAP");
        return Save(m, Generated + BoltAsset + tier + ".mat");
    }

    private static Material CreateRopeMaterial()
    {
        var m = new Material(Shader.Find("Standard")) { name = RopeAsset };
        m.SetTexture("_MainTex", Load<Texture2D>(Source + "rope_albedo.png"));
        m.SetTexture("_BumpMap", Load<Texture2D>(Source + "rope_normal.png"));
        m.SetFloat("_BumpScale", 1f);
        m.SetFloat("_Metallic", 0f);
        m.SetFloat("_Glossiness", 0.12f);
        m.EnableKeyword("_NORMALMAP");
        return Save(m, Generated + RopeAsset + ".mat");
    }

    private static Material Save(Material m, string path)
    {
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(m, path);
        return AssetDatabase.LoadAssetAtPath<Material>(path);
    }

    private static T Load<T>(string path) where T : UnityEngine.Object
    {
        T asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset == null) throw new FileNotFoundException("Missing asset " + path);
        return asset;
    }

    // ------------------------------------------------------------------ prefab

    /// <summary>
    /// Root pivot is the hook's tip with the bolt pointing along +Z, matching
    /// how the game flies projectiles (it LookAts the velocity). The mod lines
    /// this tip up with the stock bolt's tip. Tip and RopeAttach stay as
    /// children for the mod to read.
    /// </summary>
    private static GameObject CreateBoltPrefab(string tier, Material material)
    {
        GameObject modelAsset = Load<GameObject>(Source + "GrapplingHookBolt.fbx");
        var root = new GameObject(BoltAsset + tier);
        GameObject model = UnityEngine.Object.Instantiate(modelAsset);
        model.name = "Model";
        foreach (MeshRenderer r in model.GetComponentsInChildren<MeshRenderer>(true))
        {
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.On;
            r.receiveShadows = true;
        }

        Transform tip = Find(model.transform, "Tip");
        Transform attach = Find(model.transform, "RopeAttach");
        Vector3 axis = (tip.position - attach.position).normalized;
        model.transform.rotation = Quaternion.FromToRotation(axis, Vector3.forward) * model.transform.rotation;
        model.transform.position -= tip.position;
        model.transform.SetParent(root.transform, true);

        // Promote the markers to the root so the mod finds them by name.
        tip.SetParent(root.transform, true);
        attach.SetParent(root.transform, true);
        Bounds bounds = RendererBounds(root);
        Debug.Log("[GrapplingHookBuild] Bolt bounds " + bounds + ", length " +
            Vector3.Distance(tip.position, attach.position).ToString("F3") + " m tip→rope eye, rope eye at " +
            attach.localPosition.ToString("F4"));

        string path = Generated + BoltAsset + tier + ".prefab";
        AssetDatabase.DeleteAsset(path);
        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, path);
        UnityEngine.Object.DestroyImmediate(root);
        return saved;
    }

    private static Transform Find(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        throw new InvalidOperationException("Model has no " + name + " marker");
    }

    private static Bounds RendererBounds(GameObject go)
    {
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
        Bounds b = renderers[0].bounds;
        foreach (Renderer r in renderers) b.Encapsulate(r.bounds);
        return b;
    }

    // -------------------------------------------------------------------- icon

    /// <summary>
    /// Matches the stock bolt icons: a close-up of the head at the top right,
    /// the shaft leaving the frame at the bottom left, transparent background.
    /// </summary>
    private static void RenderIcon(GameObject prefab, string outPath, string tier)
    {
        EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        foreach (Camera c in UnityEngine.Object.FindObjectsOfType<Camera>()) UnityEngine.Object.DestroyImmediate(c.gameObject);
        foreach (Light l in UnityEngine.Object.FindObjectsOfType<Light>()) UnityEngine.Object.DestroyImmediate(l.gameObject);

        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.24f, 0.25f, 0.27f);
        RenderSettings.ambientEquatorColor = new Color(0.15f, 0.145f, 0.14f);
        RenderSettings.ambientGroundColor = new Color(0.06f, 0.055f, 0.05f);
        RenderSettings.fog = false;

        GameObject hook = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        // Tip toward the upper right, turned a little toward the camera and
        // rolled so two claws sit either side of the shaft.
        hook.transform.rotation = Quaternion.LookRotation(new Vector3(1f, 1f, -0.25f).normalized, Vector3.forward) *
            Quaternion.AngleAxis(20f, Vector3.forward);
        hook.transform.position = Vector3.zero;

        Light key = NewLight("Key", new Color(1.0f, 0.96f, 0.9f), 0.75f, new Vector3(40f, 60f, 0f));
        key.shadows = LightShadows.Soft;
        NewLight("Fill", new Color(0.75f, 0.82f, 1.0f), 0.12f, new Vector3(10f, -50f, 0f));
        NewLight("Rim", new Color(1.0f, 1.0f, 1.0f), 0.6f, new Vector3(25f, -150f, 0f));

        // Metal shows its reflections, and a batch-mode probe captures nothing,
        // so reflect a generated studio environment instead.
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
        RenderSettings.customReflectionTexture = StudioCubemap();
        RenderSettings.reflectionIntensity = 1f;

        // Frame the head, the shank and the start of the rubber grip, so the
        // icon reads as the hook the player sees in game.
        Vector3 axis = hook.transform.forward;
        Vector3 focus = hook.transform.position - axis * 0.13f;
        var camGo = new GameObject("IconCamera");
        Camera cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 22f;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = 5f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.allowHDR = false;
        cam.allowMSAA = true;
        cam.transform.position = focus + new Vector3(0f, 0f, -0.62f);
        cam.transform.LookAt(focus, Vector3.up);

        int size = IconSize * IconSupersample;
        var rt = new RenderTexture(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
        {
            antiAliasing = 8
        };
        cam.targetTexture = rt;
        cam.Render();
        RenderTexture.active = rt;
        var big = new Texture2D(size, size, TextureFormat.RGBA32, false, false);
        big.ReadPixels(new Rect(0, 0, size, size), 0, 0);
        big.Apply();
        RenderTexture.active = null;
        cam.targetTexture = null;
        rt.Release();

        File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), "Build/icon_large_" + tier + ".png"), big.EncodeToPNG());
        Texture2D icon = Downsample(big, IconSupersample);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        File.WriteAllBytes(outPath, icon.EncodeToPNG());
        Debug.Log("[GrapplingHookBuild] Icon written to " + outPath);
    }

    /// <summary>
    /// Sky-to-floor gradient with two soft boxes (key upper left, strip on the
    /// right) so brushed steel picks up highlights like a studio product shot.
    /// </summary>
    private static Cubemap StudioCubemap()
    {
        const int size = 64;
        var cube = new Cubemap(size, TextureFormat.RGBAHalf, true);
        Vector3 keyDir = new Vector3(-0.6f, 0.7f, -0.4f).normalized;
        Vector3 stripDir = new Vector3(0.9f, 0.2f, -0.3f).normalized;
        var faces = new[]
        {
            CubemapFace.PositiveX, CubemapFace.NegativeX, CubemapFace.PositiveY,
            CubemapFace.NegativeY, CubemapFace.PositiveZ, CubemapFace.NegativeZ
        };
        var pixels = new Color[size * size];
        foreach (CubemapFace face in faces)
        {
            for (int row = 0; row < size; row++)
            for (int col = 0; col < size; col++)
            {
                float u = (col + 0.5f) / size * 2f - 1f;
                float v = (row + 0.5f) / size * 2f - 1f;
                Vector3 d;
                switch (face)
                {
                    case CubemapFace.PositiveX: d = new Vector3(1f, -v, -u); break;
                    case CubemapFace.NegativeX: d = new Vector3(-1f, -v, u); break;
                    case CubemapFace.PositiveY: d = new Vector3(u, 1f, v); break;
                    case CubemapFace.NegativeY: d = new Vector3(u, -1f, -v); break;
                    case CubemapFace.PositiveZ: d = new Vector3(u, -v, 1f); break;
                    default: d = new Vector3(-u, -v, -1f); break;
                }
                d.Normalize();
                Color c = Color.Lerp(new Color(0.04f, 0.04f, 0.035f), new Color(0.13f, 0.135f, 0.145f),
                    Mathf.SmoothStep(0f, 1f, d.y * 0.6f + 0.45f));
                c += new Color(0.5f, 0.48f, 0.46f) * Mathf.SmoothStep(0.90f, 0.96f, Vector3.Dot(d, keyDir));
                c += new Color(0.3f, 0.31f, 0.34f) * Mathf.SmoothStep(0.88f, 0.94f, Vector3.Dot(d, stripDir));
                c.a = 1f;
                pixels[row * size + col] = c;
            }
            cube.SetPixels(pixels, face);
        }
        cube.Apply(true);
        return cube;
    }

    private static Light NewLight(string name, Color color, float intensity, Vector3 euler)
    {
        var go = new GameObject(name);
        Light l = go.AddComponent<Light>();
        l.type = LightType.Directional;
        l.color = color;
        l.intensity = intensity;
        go.transform.rotation = Quaternion.Euler(euler);
        return l;
    }

    /// <summary>Box filter with premultiplied alpha so edges don't pick up a dark fringe.</summary>
    private static Texture2D Downsample(Texture2D src, int factor)
    {
        int w = src.width / factor, h = src.height / factor;
        Color[] input = src.GetPixels();
        var output = new Color[w * h];
        float n = factor * factor;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            float r = 0, g = 0, b = 0, a = 0;
            for (int dy = 0; dy < factor; dy++)
            for (int dx = 0; dx < factor; dx++)
            {
                Color c = input[(y * factor + dy) * src.width + x * factor + dx];
                r += c.r * c.a; g += c.g * c.a; b += c.b * c.a; a += c.a;
            }
            output[y * w + x] = a > 0f ? new Color(r / a, g / a, b / a, a / n) : new Color(0, 0, 0, 0);
        }
        var dst = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
        dst.SetPixels(output);
        dst.Apply();
        return dst;
    }

    // ------------------------------------------------------------------ bundle

    private static void BuildBundle()
    {
        string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Build/Bundles");
        Directory.CreateDirectory(outDir);
        var builds = new[]
        {
            new AssetBundleBuild
            {
                assetBundleName = BundleName,
                assetNames = new[]
                {
                    Generated + BoltAsset + Tiers[0] + ".prefab", Generated + BoltAsset + Tiers[1] + ".prefab",
                    Generated + BoltAsset + Tiers[2] + ".prefab", Generated + RopeAsset + ".mat"
                },
                addressableNames = new[] { BoltAsset + Tiers[0], BoltAsset + Tiers[1], BoltAsset + Tiers[2], RopeAsset }
            }
        };
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(outDir, builds,
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode,
            BuildTarget.StandaloneWindows64);
        if (manifest == null) throw new InvalidOperationException("Asset bundle build failed");
        string target = Path.Combine(ModletDir, "Resources/GrapplingHook.unity3d");
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        File.Copy(Path.Combine(outDir, BundleName), target, true);
        Debug.Log("[GrapplingHookBuild] Bundle written to " + target + " (" + new FileInfo(target).Length + " bytes)");
    }
}
