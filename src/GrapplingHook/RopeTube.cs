using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// One rope drawn as a tube mesh between two points, sagging with slack. The
/// local player's rope (HookLine) and every other player's rope (RopeSync) are
/// each one of these. Points are in Unity space (world minus Origin.position).
/// </summary>
internal sealed class RopeTube
{
    private const int Segments = 24;
    private const int Sides = 10;
    private const float RopeRadius = 0.008f;
    // One texture tile is one lay length of the rope (the baked tile matches a
    // 1.6 cm rope with a 5.4 cm lay); u runs along the rope, v around it.
    private const float RopeLayMeters = 0.054f;

    private static int[] triangles;

    private readonly GameObject gameObject;
    private readonly Mesh mesh;
    private readonly Vector3[] points = new Vector3[Segments + 1];
    private readonly Vector3[] vertices = new Vector3[(Segments + 1) * (Sides + 1) + 2 * (Sides + 1)];
    private readonly Vector3[] normals;
    private readonly Vector4[] tangents;
    private readonly Vector2[] uvs;

    internal GameObject GameObject { get { return gameObject; } }

    internal RopeTube(string name, Material material)
    {
        normals = new Vector3[vertices.Length];
        tangents = new Vector4[vertices.Length];
        uvs = new Vector2[vertices.Length];
        gameObject = new GameObject(name);
        mesh = new Mesh { name = name };
        mesh.MarkDynamic();
        mesh.vertices = vertices;
        mesh.triangles = triangles ?? (triangles = BuildTriangles());
        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = gameObject.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.On;
        renderer.receiveShadows = true;
    }

    internal void SetVisible(bool visible)
    {
        if (gameObject != null && gameObject.activeSelf != visible) gameObject.SetActive(visible);
    }

    internal void Destroy()
    {
        if (gameObject != null) Object.Destroy(gameObject);
        if (mesh != null) Object.Destroy(mesh);
    }

    /// <summary>
    /// Sag only with slack: a parabola whose arc length matches the unused rope
    /// (arc ≈ span + 8h²/3span). slack is rope length minus straight distance.
    /// </summary>
    internal void Draw(Vector3 start, Vector3 end, float slack)
    {
        float span = Vector3.Distance(start, end);
        slack = Mathf.Max(0f, slack);
        float sag = span > 0.01f ? Mathf.Min(Mathf.Sqrt(3f * span * slack / 8f), span * 0.5f) : 0f;
        for (int i = 0; i <= Segments; i++)
        {
            float t = (float)i / Segments;
            points[i] = Vector3.Lerp(start, end, t) + Vector3.down * (4f * sag * t * (1f - t));
        }
        BuildTube();
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
    /// Rings of vertices around the rope path. The ring frame is carried along
    /// the path by parallel transport so the tube doesn't twist; u is metres
    /// along the rope over the lay length and v goes once around. Tangents
    /// follow +u and the binormal +v, which is how the normal map was baked.
    /// </summary>
    private void BuildTube()
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

        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.tangents = tangents;
        mesh.uv = uvs;
        mesh.RecalculateBounds();
    }

    private void WriteCap(int at, Vector3 centre, Vector3 facing, int ringStart)
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
}
