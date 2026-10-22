using System.Numerics;
using ScaleMail.Core.Model;

namespace ScaleMail.Core.Mesh3D;

public sealed class Build3DOptions
{
    /// <summary>Real-world width of one scale; sets the size of the whole model.</summary>
    public double ScaleWidthMm { get; set; } = 12;
    public double ThicknessMm { get; set; } = 0.8;
    /// <summary>
    /// Flat plates: no wrap, tilt or cup. Overlapping scales sit on a few stacked layers (row index modulo the
    /// overlap depth) so every plate is a printable flat slab and none intersect.
    /// </summary>
    public bool Flat { get; set; } = true;
    /// <summary>Angle of the whole template wrapped around the tube; 0 keeps it flat.</summary>
    public double WrapDegrees { get; set; } = 180;
    /// <summary>Lift of each scale's tip off the surface; null derives it so rows stack without collisions.</summary>
    public double? TiltDegrees { get; set; }
    /// <summary>How far the scale edges curve toward the body.</summary>
    public double CupMm { get; set; } = 0.6;
    public double HoleDiameterMm { get; set; } = 1.6;
    public int OutlineSegments { get; set; } = 12;
    public int HoleSegments { get; set; } = 10;
    /// <summary>Thickness of the wire the rings are made from (layout builds).</summary>
    public double RingWireMm { get; set; } = 1.0;
}

/// <summary>Builds the 3D scale model from a 2D <see cref="ScaleMesh"/> and its link points.</summary>
public static class ScaleMeshBuilder
{
    public static TriMesh Build(ScaleMesh mesh, Build3DOptions? options = null)
    {
        options ??= new Build3DOptions();
        var result = new TriMesh();
        if (mesh.Scales.Count == 0 || mesh.ScaleWidth <= 0) return result;

        double mmPerPx = options.ScaleWidthMm / mesh.ScaleWidth;
        double scaleLenMm = mesh.ScaleLength * mmPerPx;
        double pitchMm = Math.Max(0.01, mesh.RowPitch * mmPerPx);
        bool flat = options.Flat;
        int layers = Math.Max(1, (int)Math.Ceiling(mesh.ScaleLength / Math.Max(1e-6, mesh.RowPitch) - 0.01));
        double tilt = flat ? 0 : options.TiltDegrees.HasValue
            ? options.TiltDegrees.Value * Math.PI / 180
            : Math.Asin(Math.Min(0.9, 1.05 * options.ThicknessMm / pitchMm));
        double wrap = flat ? 0 : options.WrapDegrees * Math.PI / 180;
        double cupMm = flat ? 0 : options.CupMm;

        // Per-row extent drives the tube radius at that height.
        var rows = mesh.Scales.GroupBy(s => s.Row).ToDictionary(
            g => g.Key,
            g => (min: g.Min(s => s.Center.X) - mesh.ScaleWidth / 2, max: g.Max(s => s.Center.X) + mesh.ScaleWidth / 2));
        double midX = mesh.Scales.Average(s => s.Center.X);

        var outline = LinkPlanner.Outline(options.OutlineSegments)
            .Select(p => (x: p.u * options.ScaleWidthMm / 2, y: p.v * scaleLenMm)).ToList();

        foreach (var s in mesh.Scales.OrderBy(s => s.Row))
        {
            var (rowMin, rowMax) = rows[s.Row];
            double rowWidthMm = (rowMax - rowMin) * mmPerPx;
            double xc = (rowMin + rowMax) / 2;
            double xMm = (s.Center.X - xc) * mmPerPx;
            double yMm = -(s.Tip.Y - mesh.ScaleLength) * mmPerPx; // template y grows down; model Y is up

            Vector3 origin, N, T;
            if (wrap < 1e-4)
            {
                origin = new Vector3((float)((xc - midX) * mmPerPx + xMm), (float)yMm, 0);
                N = Vector3.UnitZ; T = Vector3.UnitX;
            }
            else
            {
                double R = rowWidthMm / wrap;
                double th = xMm / R;
                origin = new Vector3((float)((xc - midX) * mmPerPx + R * Math.Sin(th)), (float)yMm, (float)(R * (Math.Cos(th) - 1)));
                N = new Vector3((float)Math.Sin(th), 0, (float)Math.Cos(th));
                T = new Vector3((float)Math.Cos(th), 0, (float)-Math.Sin(th));
            }
            // Upper rows lie on top of the rows they overlap.
            if (flat) origin += N * (float)((layers - 1 - s.Row % layers) * options.ThicknessMm);
            var D = -Vector3.UnitY;
            var vdir = D * (float)Math.Cos(tilt) + N * (float)Math.Sin(tilt);
            var nrm = N * (float)Math.Cos(tilt) - D * (float)Math.Sin(tilt);

            var holes = s.Links.Select(l =>
            {
                double cx = l.U * options.ScaleWidthMm / 2, cy = l.V * scaleLenMm;
                double r = options.HoleDiameterMm / 2;
                return (IReadOnlyList<(double x, double y)>)Enumerable.Range(0, options.HoleSegments)
                    .Select(i => (cx + r * Math.Cos(2 * Math.PI * i / options.HoleSegments), cy + r * Math.Sin(2 * Math.PI * i / options.HoleSegments)))
                    .ToList();
            }).ToList();

            AddScale(result, s, s.Id, outline, holes, origin, T, vdir, nrm, options, cupMm);
        }
        return result;
    }

    internal static void AddScale(TriMesh m, Scale s, int id, List<(double x, double y)> outline,
        List<IReadOnlyList<(double x, double y)>> holes, Vector3 origin, Vector3 T, Vector3 V, Vector3 Nrm, Build3DOptions o, double cupMm)
    {
        var (verts, tris) = Triangulator.Triangulate(outline, holes);
        double halfW = o.ScaleWidthMm / 2;
        Vector3 World(double u, double v, double z)
        {
            double cup = -cupMm * (u / halfW) * (u / halfW);
            return origin + T * (float)u + V * (float)v + Nrm * (float)(cup + z);
        }

        int baseIdx = m.Vertices.Count;
        foreach (var (x, y) in verts) m.AddVertex(World(x, y, o.ThicknessMm)); // top surface
        int bottomIdx = m.Vertices.Count;
        foreach (var (x, y) in verts) m.AddVertex(World(x, y, 0));              // underside
        m.ScaleColored[id] = s.Colored;

        for (int i = 0; i < tris.Count; i += 3)
        {
            int a = tris[i], b = tris[i + 1], c = tris[i + 2];
            // Orient the top so its normal points outward (along Nrm), the bottom opposite.
            var va = m.Vertices[baseIdx + a]; var vb = m.Vertices[baseIdx + b]; var vc = m.Vertices[baseIdx + c];
            if (Vector3.Dot(Vector3.Cross(vb - va, vc - va), Nrm) < 0) (b, c) = (c, b);
            m.AddTriangle(baseIdx + a, baseIdx + b, baseIdx + c, id, true);
            m.AddTriangle(bottomIdx + a, bottomIdx + c, bottomIdx + b, id, false);
        }

        // Walls: every boundary edge appears exactly once among triangle edges.
        var edgeCount = new Dictionary<(int, int), int>();
        var directed = new Dictionary<(int, int), (int, int)>();
        for (int i = 0; i < tris.Count; i += 3)
            for (int k = 0; k < 3; k++)
            {
                int a = tris[i + k], b = tris[i + (k + 1) % 3];
                var key = a < b ? (a, b) : (b, a);
                edgeCount[key] = edgeCount.GetValueOrDefault(key) + 1;
                directed[key] = (a, b);
            }
        var centroid = Vector3.Zero;
        foreach (var (x, y) in outline) centroid += World(x, y, o.ThicknessMm / 2);
        centroid /= outline.Count;
        var holeCenters = holes.Select(h =>
        {
            var c = Vector3.Zero;
            foreach (var (x, y) in h) c += World(x, y, o.ThicknessMm / 2);
            return c / h.Count;
        }).ToList();

        foreach (var (key, count) in edgeCount)
        {
            if (count != 1) continue; // interior edge, or a bridge slit (shared twice)
            var (a, b) = directed[key];
            var ta = baseIdx + a; var tb = baseIdx + b; var ba = bottomIdx + a; var bb = bottomIdx + b;
            var mid = (m.Vertices[ta] + m.Vertices[tb]) / 2;
            // Which loop is this edge on: a hole when its midpoint lies within a hole's radius.
            Vector3 outward = mid - centroid;
            for (int h = 0; h < holes.Count; h++)
                if ((mid - holeCenters[h]).Length() < o.HoleDiameterMm) { outward = holeCenters[h] - mid; break; }
            AddWall(m, id, ta, tb, bb, ba, outward);
        }
    }

    private static void AddWall(TriMesh m, int sid, int ta, int tb, int bb, int ba, Vector3 outward)
    {
        var n = Vector3.Cross(m.Vertices[tb] - m.Vertices[ta], m.Vertices[ba] - m.Vertices[ta]);
        if (Vector3.Dot(n, outward) >= 0)
        {
            m.AddTriangle(ta, tb, bb, sid, false); m.AddTriangle(ta, bb, ba, sid, false);
        }
        else
        {
            m.AddTriangle(ta, bb, tb, sid, false); m.AddTriangle(ta, ba, bb, sid, false);
        }
    }
}
