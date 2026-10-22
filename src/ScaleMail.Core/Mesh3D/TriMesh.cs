using System.Numerics;

namespace ScaleMail.Core.Mesh3D;

/// <summary>Indexed triangle mesh, millimetres. Triangles are tagged with the scale that produced them.</summary>
public sealed class TriMesh
{
    public List<Vector3> Vertices { get; } = new();
    public List<int> Indices { get; } = new();
    /// <summary>Scale id per triangle.</summary>
    public List<int> TriScale { get; } = new();
    /// <summary>True for triangles on the outward-facing surface (used for the light preview).</summary>
    public List<bool> TriTop { get; } = new();
    /// <summary>Colour group per scale id.</summary>
    public Dictionary<int, bool> ScaleColored { get; } = new();

    public int TriangleCount => Indices.Count / 3;

    public int AddVertex(Vector3 v) { Vertices.Add(v); return Vertices.Count - 1; }

    public void AddTriangle(int a, int b, int c, int scaleId, bool top)
    {
        Indices.Add(a); Indices.Add(b); Indices.Add(c);
        TriScale.Add(scaleId); TriTop.Add(top);
    }

    public (Vector3 min, Vector3 max) Bounds()
    {
        if (Vertices.Count == 0) return (Vector3.Zero, Vector3.Zero);
        var min = Vertices[0]; var max = Vertices[0];
        foreach (var v in Vertices) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        return (min, max);
    }
}

public static class TriMeshExtensions
{
    /// <summary>Triangles as flat xyz floats plus a colour flag per triangle (0 base, 1 pattern, 2 ring).</summary>
    public static (float[] xyz, byte[] colored) ToPreview(this TriMesh mesh, bool topOnly = false)
    {
        var xyz = new List<float>();
        var colored = new List<byte>();
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            if (topOnly && !mesh.TriTop[t]) continue;
            for (int k = 0; k < 3; k++)
            {
                var v = mesh.Vertices[mesh.Indices[t * 3 + k]];
                xyz.Add(v.X); xyz.Add(v.Y); xyz.Add(v.Z);
            }
            int sid = mesh.TriScale[t];
            colored.Add(sid < 0 ? (byte)2 : mesh.ScaleColored.GetValueOrDefault(sid) ? (byte)1 : (byte)0);
        }
        return (xyz.ToArray(), colored.ToArray());
    }
}
