using System.Globalization;
using System.Numerics;
using System.Text;
using ScaleMail.Core.Mesh3D;

namespace ScaleMail.Core.Export;

public static class MeshExporter
{
    /// <summary>Binary STL. <paramref name="include"/> filters by scale id (e.g. only the coloured scales).</summary>
    public static byte[] ToStl(TriMesh mesh, Func<int, bool>? include = null)
    {
        var tris = Enumerable.Range(0, mesh.TriangleCount).Where(t => include?.Invoke(mesh.TriScale[t]) ?? true).ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var header = new byte[80];
        Encoding.ASCII.GetBytes("ScaleMail export").CopyTo(header, 0);
        w.Write(header);
        w.Write((uint)tris.Count);
        foreach (int t in tris)
        {
            var a = mesh.Vertices[mesh.Indices[t * 3]]; var b = mesh.Vertices[mesh.Indices[t * 3 + 1]]; var c = mesh.Vertices[mesh.Indices[t * 3 + 2]];
            var n = Vector3.Cross(b - a, c - a);
            n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitZ;
            Write(w, n); Write(w, a); Write(w, b); Write(w, c);
            w.Write((ushort)0);
        }
        return ms.ToArray();
    }

    private static void Write(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }

    /// <summary>Wavefront OBJ with one object per scale and a material per pattern colour.</summary>
    public static string ToObj(TriMesh mesh)
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine("# ScaleMail export");
        sb.AppendLine("mtllib scalemail.mtl");
        foreach (var v in mesh.Vertices) sb.Append("v ").Append(v.X.ToString("0.####", ci)).Append(' ').Append(v.Y.ToString("0.####", ci)).Append(' ').Append(v.Z.ToString("0.####", ci)).Append('\n');
        int current = -1;
        for (int t = 0; t < mesh.TriangleCount; t++)
        {
            int sid = mesh.TriScale[t];
            if (sid != current)
            {
                current = sid;
                sb.Append(sid < 0 ? "o ring_" : "o scale_").Append(Math.Abs(sid)).Append('\n');
                sb.Append("usemtl ").Append(sid < 0 ? "ring" : mesh.ScaleColored.GetValueOrDefault(sid) ? "pattern" : "base").Append('\n');
            }
            sb.Append("f ").Append(mesh.Indices[t * 3] + 1).Append(' ').Append(mesh.Indices[t * 3 + 1] + 1).Append(' ').Append(mesh.Indices[t * 3 + 2] + 1).Append('\n');
        }
        return sb.ToString();
    }

    public static string ObjMaterials() =>
        "newmtl base\nKd 0.85 0.85 0.85\nnewmtl pattern\nKd 0.45 0.45 0.5\nnewmtl ring\nKd 0.9 0.7 0.25\n";
}
