namespace ScaleMail.Core.Mesh3D;

/// <summary>Ear-clipping triangulation of a simple polygon with optional holes (holes are bridged in).</summary>
public static class Triangulator
{
    /// <summary>
    /// Returns vertices (outer first, then each hole; bridge duplicates are re-used indices, so every
    /// index refers into the returned vertex list) and triangle indices (counter-clockwise).
    /// </summary>
    public static (List<(double x, double y)> verts, List<int> tris) Triangulate(
        IReadOnlyList<(double x, double y)> outer, IReadOnlyList<IReadOnlyList<(double x, double y)>> holes)
    {
        var verts = new List<(double x, double y)>();
        var ring = new List<int>(); // polygon as vertex indices, possibly repeating bridge vertices

        AddRing(outer, ccw: true);
        foreach (var hole in holes.OrderByDescending(h => h.Max(p => p.x)))
        {
            int start = verts.Count;
            var hv = hole.ToList();
            if (SignedArea(hv) > 0) hv.Reverse(); // holes run clockwise
            foreach (var p in hv) verts.Add(p);
            int holeCount = hv.Count;

            // Bridge: rightmost hole vertex to a visible outer vertex.
            int hi = 0;
            for (int i = 1; i < holeCount; i++) if (verts[start + i].x > verts[start + hi].x) hi = i;
            int bridge = FindVisible(ring, verts, verts[start + hi], start + hi);
            if (bridge < 0) continue; // give up on this hole rather than corrupt the polygon
            var inserted = new List<int>();
            for (int i = 0; i <= holeCount; i++) inserted.Add(start + (hi + i) % holeCount);
            inserted.Add(ring[bridge]);
            ring.InsertRange(bridge + 1, inserted);
        }

        var tris = new List<int>();
        EarClip(ring, verts, tris);
        ImproveQuality(verts, tris);
        return (verts, tris);

        void AddRing(IReadOnlyList<(double x, double y)> pts, bool ccw)
        {
            var l = pts.ToList();
            if ((SignedArea(l) > 0) != ccw) l.Reverse();
            foreach (var p in l) { ring.Add(verts.Count); verts.Add(p); }
        }
    }

    /// <summary>Lawson edge flips: swaps diagonals whose opposite angles sum past 180°, removing ear-clipping slivers.</summary>
    private static void ImproveQuality(List<(double x, double y)> v, List<int> tris)
    {
        for (int pass = 0; pass < 60; pass++)
        {
            var edges = new Dictionary<(int, int), List<(int tri, int opp)>>();
            for (int t = 0; t < tris.Count / 3; t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = tris[t * 3 + k], b = tris[t * 3 + (k + 1) % 3], o = tris[t * 3 + (k + 2) % 3];
                    var key = a < b ? (a, b) : (b, a);
                    if (!edges.TryGetValue(key, out var l)) edges[key] = l = new();
                    l.Add((t, o));
                }
            var touched = new HashSet<int>();
            bool flipped = false;
            foreach (var (key, l) in edges)
            {
                if (l.Count != 2) continue;
                var (t1, p) = l[0]; var (t2, q) = l[1];
                if (touched.Contains(t1) || touched.Contains(t2) || p == q) continue;
                var (a, b) = key;
                if (Angle(v[p], v[a], v[b]) + Angle(v[q], v[a], v[b]) <= Math.PI + 1e-9) continue;
                // New diagonal p-q must lie inside the quad p,a,q,b: both new triangles need the same winding.
                double s1 = Cross(v[p], v[a], v[q]), s2 = Cross(v[p], v[q], v[b]);
                double orient = Cross(v[tris[t1 * 3]], v[tris[t1 * 3 + 1]], v[tris[t1 * 3 + 2]]);
                if (Math.Sign(s1) != Math.Sign(orient) || Math.Sign(s2) != Math.Sign(orient) || Math.Abs(s1) < 1e-12 || Math.Abs(s2) < 1e-12) continue;
                tris[t1 * 3] = p; tris[t1 * 3 + 1] = a; tris[t1 * 3 + 2] = q;
                tris[t2 * 3] = p; tris[t2 * 3 + 1] = q; tris[t2 * 3 + 2] = b;
                touched.Add(t1); touched.Add(t2);
                flipped = true;
            }
            if (!flipped) break;
        }
    }

    /// <summary>Angle at <paramref name="p"/> between the two edge endpoints.</summary>
    private static double Angle((double x, double y) p, (double x, double y) a, (double x, double y) b)
    {
        double ax = a.x - p.x, ay = a.y - p.y, bx = b.x - p.x, by = b.y - p.y;
        double d = Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by));
        return d < 1e-18 ? 0 : Math.Acos(Math.Clamp((ax * bx + ay * by) / d, -1, 1));
    }

    private static double SignedArea(IReadOnlyList<(double x, double y)> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var (x1, y1) = p[i]; var (x2, y2) = p[(i + 1) % p.Count];
            a += x1 * y2 - x2 * y1;
        }
        return a / 2;
    }

    private static int FindVisible(List<int> ring, List<(double x, double y)> v, (double x, double y) from, int selfIndex)
    {
        // Prefer the nearest ring vertex whose connecting segment crosses no ring edge.
        var order = Enumerable.Range(0, ring.Count)
            .Where(i => ring[i] != selfIndex)
            .OrderBy(i => Dist(v[ring[i]], from));
        foreach (int i in order)
        {
            var to = v[ring[i]];
            bool ok = true;
            for (int e = 0; e < ring.Count && ok; e++)
            {
                int a = ring[e], b = ring[(e + 1) % ring.Count];
                if (a == ring[i] || b == ring[i]) continue;
                if (Intersect(from, to, v[a], v[b])) ok = false;
            }
            if (ok) return i;
        }
        return -1;
    }

    private static double Dist((double x, double y) a, (double x, double y) b) =>
        (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y);

    private static double Cross((double x, double y) o, (double x, double y) a, (double x, double y) b) =>
        (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

    private static bool Intersect((double x, double y) p1, (double x, double y) p2, (double x, double y) p3, (double x, double y) p4)
    {
        double d1 = Cross(p3, p4, p1), d2 = Cross(p3, p4, p2), d3 = Cross(p1, p2, p3), d4 = Cross(p1, p2, p4);
        return ((d1 > 1e-12 && d2 < -1e-12) || (d1 < -1e-12 && d2 > 1e-12)) &&
               ((d3 > 1e-12 && d4 < -1e-12) || (d3 < -1e-12 && d4 > 1e-12));
    }

    private static void EarClip(List<int> ring, List<(double x, double y)> v, List<int> tris)
    {
        var idx = new List<int>(ring);
        int guard = idx.Count * idx.Count + 10;
        int i = 0;
        while (idx.Count > 3 && guard-- > 0)
        {
            int n = idx.Count;
            int ia = idx[(i + n - 1) % n], ib = idx[i % n], ic = idx[(i + 1) % n];
            if (IsEar(idx, v, (i + n - 1) % n, i % n, (i + 1) % n))
            {
                tris.Add(ia); tris.Add(ib); tris.Add(ic);
                idx.RemoveAt(i % n);
                i = i % Math.Max(1, idx.Count);
            }
            else i = (i + 1) % n;
        }
        if (idx.Count == 3) { tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]); }
    }

    private static bool IsEar(List<int> idx, List<(double x, double y)> v, int ia, int ib, int ic)
    {
        var a = v[idx[ia]]; var b = v[idx[ib]]; var c = v[idx[ic]];
        double area = Cross(a, b, c);
        if (area <= 1e-14) return false; // reflex or degenerate (bridge slits collapse here)
        for (int k = 0; k < idx.Count; k++)
        {
            if (k == ia || k == ib || k == ic) continue;
            var p = v[idx[k]];
            if (p == a || p == b || p == c) continue;
            if (Cross(a, b, p) >= -1e-14 && Cross(b, c, p) >= -1e-14 && Cross(c, a, p) >= -1e-14) return false;
        }
        return true;
    }
}
