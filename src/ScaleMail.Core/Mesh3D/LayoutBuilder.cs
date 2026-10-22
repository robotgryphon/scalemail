using System.Numerics;
using ScaleMail.Core.Model;

namespace ScaleMail.Core.Mesh3D;

/// <summary>
/// Builds the 3D model of a whole layout: every section as flat stacked plates, plus a torus for each ring
/// passing through a hole cut into each of the two scales it joins.
/// </summary>
public static class LayoutBuilder
{
    public static TriMesh Build(Layout layout, Build3DOptions? options = null)
    {
        options ??= new Build3DOptions();
        var result = new TriMesh();
        if (layout.Sections.Count == 0 || layout.ScaleWidth <= 0) return result;

        double mm = options.ScaleWidthMm / layout.ScaleWidth;
        double lenMm = layout.ScaleLength * mm;
        double t = options.ThicknessMm;
        int layers = Math.Max(1, (int)Math.Ceiling(layout.ScaleLength / Math.Max(1e-6, layout.RowPitch) - 0.01));
        var outline = LinkPlanner.Outline(options.OutlineSegments)
            .Select(p => (x: p.u * options.ScaleWidthMm / 2, y: p.v * lenMm)).ToList();
        double holeR = options.HoleDiameterMm / 2;

        // Hole for each ring end, in the local mm frame of the scale it passes through.
        var holeOf = new Dictionary<(int ring, int end), (double x, double y)>();
        var holesPerScale = new Dictionary<(int sec, int scale), List<(double x, double y)>>();
        foreach (var ring in layout.Rings)
        {
            var pos = layout.RingWorld(ring);
            for (int end = 0; end < 2; end++)
            {
                var r = end == 0 ? ring.A : ring.B;
                var sec = layout.FindSection(r.Section); var sc = sec?.Mesh.Find(r.Scale);
                if (sec is null || sc is null) continue;
                var local = sec.ToLocal(pos);
                var p = ((local.X - sc.Center.X) * mm, (local.Y - (sc.Tip.Y - layout.ScaleLength)) * mm);
                var h = HolePosition(outline, p, holeR);
                var list = holesPerScale.TryGetValue((r.Section, r.Scale), out var l) ? l : holesPerScale[(r.Section, r.Scale)] = new();
                if (list.Any(o => double.Hypot(o.x - h.x, o.y - h.y) < holeR * 2.4)) continue;
                list.Add(h);
                holeOf[(ring.Id, end)] = h;
            }
        }

        int nextId = 0;
        foreach (var sec in layout.Sections)
        {
            double phi = sec.Rotation * Math.PI / 180;
            // Canvas y points down, model Y up.
            var T = new Vector3((float)Math.Cos(phi), (float)-Math.Sin(phi), 0);
            var V = new Vector3((float)-Math.Sin(phi), (float)-Math.Cos(phi), 0);
            foreach (var sc in sec.Mesh.Scales)
            {
                var top = sec.ToWorld(new Pt(sc.Center.X, sc.Tip.Y - layout.ScaleLength));
                var origin = new Vector3((float)(top.X * mm), (float)(-top.Y * mm), (float)((layers - 1 - sc.Row % layers) * t));
                var holes = (holesPerScale.GetValueOrDefault((sec.Id, sc.Id)) ?? new())
                    .Select(h => (IReadOnlyList<(double x, double y)>)Circle(h, holeR, options.HoleSegments)).ToList();
                ScaleMeshBuilder.AddScale(result, sc, nextId++, outline, holes, origin, T, V, Vector3.UnitZ, options, 0);
            }
        }

        // Rings: a torus in the vertical plane through both holes.
        double zMid = layers * t / 2;
        foreach (var ring in layout.Rings)
        {
            if (!holeOf.TryGetValue((ring.Id, 0), out var ha) || !holeOf.TryGetValue((ring.Id, 1), out var hb)) continue;
            var a = HoleWorldMm(layout, ring.A, ha, mm);
            var b = HoleWorldMm(layout, ring.B, hb, mm);
            AddTorus(result, -(ring.Id + 1), new Vector3(a.X, a.Y, 0), new Vector3(b.X, b.Y, 0), (float)zMid, (float)(options.RingWireMm / 2));
        }
        return result;
    }

    private static (float X, float Y) HoleWorldMm(Layout layout, ScaleRef r, (double x, double y) hole, double mm)
    {
        var sec = layout.FindSection(r.Section)!; var sc = sec.Mesh.Find(r.Scale)!;
        var top = new Pt(sc.Center.X + hole.x / mm, sc.Tip.Y - layout.ScaleLength + hole.y / mm);
        var w = sec.ToWorld(top);
        return ((float)(w.X * mm), (float)(-w.Y * mm));
    }

    private static List<(double x, double y)> Circle((double x, double y) c, double r, int n) =>
        Enumerable.Range(0, n).Select(i => (c.x + r * Math.Cos(2 * Math.PI * i / n), c.y + r * Math.Sin(2 * Math.PI * i / n))).ToList();

    /// <summary>Where a hole goes for a ring placed at <paramref name="p"/>: there if well inside, else inset from the nearest edge.</summary>
    internal static (double x, double y) HolePosition(List<(double x, double y)> outline, (double x, double y) p, double holeR)
    {
        double inset = holeR * 2.2;
        double cx = outline.Average(o => o.x), cy = outline.Average(o => o.y);
        // Nearest point on the outline (edges, not just vertices).
        double best = double.MaxValue; (double x, double y) q = outline[0];
        for (int i = 0; i < outline.Count; i++)
        {
            var a = outline[i]; var b = outline[(i + 1) % outline.Count];
            double dx = b.x - a.x, dy = b.y - a.y, len2 = dx * dx + dy * dy;
            double u = len2 < 1e-12 ? 0 : Math.Clamp(((p.x - a.x) * dx + (p.y - a.y) * dy) / len2, 0, 1);
            var c = (x: a.x + u * dx, y: a.y + u * dy);
            double d = double.Hypot(p.x - c.x, p.y - c.y);
            if (d < best) { best = d; q = c; }
        }
        bool inside = Inside(outline, p);
        if (inside && best >= inset) return p;
        // Pull the boundary point toward the centre by the inset.
        double vx = cx - q.x, vy = cy - q.y, vl = double.Hypot(vx, vy);
        return vl < 1e-9 ? (cx, cy) : (q.x + vx / vl * inset, q.y + vy / vl * inset);
    }

    private static bool Inside(List<(double x, double y)> poly, (double x, double y) p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                inside = !inside;
        return inside;
    }

    private static void AddTorus(TriMesh m, int id, Vector3 a, Vector3 b, float zMid, float tube)
    {
        var mid = (a + b) / 2;
        var d = b - a;
        float dist = d.Length();
        d = dist < 1e-4 ? Vector3.UnitX : d / dist;
        float R = Math.Max(dist / 2, tube * 3);
        var center = new Vector3(mid.X, mid.Y, zMid);
        var binormal = Vector3.Normalize(Vector3.Cross(d, Vector3.UnitZ));
        const int major = 24, minor = 8;
        int baseIdx = m.Vertices.Count;
        for (int i = 0; i < major; i++)
        {
            double phi = 2 * Math.PI * i / major;
            var n = (float)Math.Cos(phi) * d + (float)Math.Sin(phi) * Vector3.UnitZ;
            for (int j = 0; j < minor; j++)
            {
                double psi = 2 * Math.PI * j / minor;
                m.AddVertex(center + n * (R + tube * (float)Math.Cos(psi)) + binormal * (tube * (float)Math.Sin(psi)));
            }
        }
        for (int i = 0; i < major; i++)
            for (int j = 0; j < minor; j++)
            {
                int i2 = (i + 1) % major, j2 = (j + 1) % minor;
                int p00 = baseIdx + i * minor + j, p01 = baseIdx + i * minor + j2, p10 = baseIdx + i2 * minor + j, p11 = baseIdx + i2 * minor + j2;
                // Outward: away from the tube centreline.
                var phi = 2 * Math.PI * i / major; var psi = 2 * Math.PI * j / minor;
                var nOut = (float)Math.Cos(psi) * ((float)Math.Cos(phi) * d + (float)Math.Sin(phi) * Vector3.UnitZ) + (float)Math.Sin(psi) * binormal;
                var tri1 = Vector3.Cross(m.Vertices[p10] - m.Vertices[p00], m.Vertices[p11] - m.Vertices[p00]);
                if (Vector3.Dot(tri1, nOut) >= 0)
                {
                    m.AddTriangle(p00, p10, p11, id, true); m.AddTriangle(p00, p11, p01, id, true);
                }
                else
                {
                    m.AddTriangle(p00, p11, p10, id, true); m.AddTriangle(p00, p01, p11, id, true);
                }
            }
    }
}
