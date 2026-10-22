namespace ScaleMail.Core.Model;

/// <summary>Row/column numbering and neighbour links, derived from scale positions.</summary>
public static class MeshTopology
{
    /// <summary>Re-derives rows, columns and neighbours after scales were added, removed or moved.</summary>
    public static void Rebuild(ScaleMesh mesh)
    {
        mesh.Reindex();
        mesh.Zips.RemoveAll(z => mesh.Find(z.A) is null || mesh.Find(z.B) is null || z.A == z.B);
        AssignRows(mesh);
        BuildNeighbors(mesh);
    }

    private static void AssignRows(ScaleMesh mesh)
    {
        var scales = mesh.Scales.OrderBy(s => s.Tip.Y).ToList();
        if (scales.Count == 0) { mesh.RowCount = 0; return; }
        double tol = Math.Max(1, mesh.RowPitch * 0.45);
        var rows = new List<List<Scale>>();
        List<Scale>? cur = null;
        foreach (var s in scales)
        {
            if (cur is null || s.Tip.Y - cur.Average(m => m.Tip.Y) > tol) rows.Add(cur = new List<Scale>());
            cur.Add(s);
        }
        for (int r = 0; r < rows.Count; r++)
        {
            int c = 0;
            foreach (var s in rows[r].OrderBy(s => s.Center.X)) { s.Row = r; s.Col = c++; }
        }
        mesh.RowCount = rows.Count;
    }

    /// <summary>
    /// Side neighbours: the next scale along the row. Above/below: scales in the neighbouring row within
    /// 0.75 of a scale width. Seam neighbours come from the zip pairs.
    /// </summary>
    public static void BuildNeighbors(ScaleMesh mesh)
    {
        mesh.Reindex();
        foreach (var s in mesh.Scales) s.Neighbors = new List<Neighbor>();
        double w = mesh.ScaleWidth;
        var rows = mesh.Scales.GroupBy(s => s.Row).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Center.X).ToList());

        foreach (var (row, list) in rows)
        {
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if (list[i + 1].Center.X - list[i].Center.X > w * 1.6) continue;
                Link(list[i], list[i + 1], NeighborKind.Right, NeighborKind.Left);
            }
            if (!rows.TryGetValue(row + 1, out var below)) continue;
            foreach (var a in list)
                foreach (var b in below)
                    if (Math.Abs(a.Center.X - b.Center.X) < w * 0.75)
                        Link(a, b, NeighborKind.Below, NeighborKind.Above);
        }
        foreach (var z in mesh.Zips)
        {
            var a = mesh.Find(z.A); var b = mesh.Find(z.B);
            if (a is not null && b is not null) Link(a, b, NeighborKind.Seam, NeighborKind.Seam);
        }

        static void Link(Scale a, Scale b, NeighborKind ab, NeighborKind ba)
        {
            a.Neighbors.Add(new Neighbor { ScaleId = b.Id, Kind = ab });
            b.Neighbors.Add(new Neighbor { ScaleId = a.Id, Kind = ba });
        }
    }

    // ---- editing --------------------------------------------------------------------------

    /// <summary>Offset from a scale's visible centre to its tip, typical for this template.</summary>
    public static double TipOffset(ScaleMesh mesh) =>
        mesh.Scales.Count == 0 ? mesh.RowPitch : mesh.Scales.Select(s => s.Tip.Y - s.Center.Y).OrderBy(v => v).ElementAt(mesh.Scales.Count / 2);

    public static Scale AddScale(ScaleMesh mesh, double centerX, double tipY, bool? colored = null)
    {
        var s = new Scale
        {
            Id = mesh.NewId(),
            Center = new Pt(centerX, tipY - TipOffset(mesh)),
            Tip = new Pt(centerX, tipY),
            Colored = colored ?? false,
        };
        mesh.Scales.Add(s);
        Rebuild(mesh);
        return s;
    }

    public static void RemoveScale(ScaleMesh mesh, int id)
    {
        mesh.Scales.RemoveAll(s => s.Id == id);
        Rebuild(mesh);
    }

    public static void Move(Scale s, double dx, double dy)
    {
        s.Center = new Pt(s.Center.X + dx, s.Center.Y + dy);
        s.Tip = new Pt(s.Tip.X + dx, s.Tip.Y + dy);
    }

    /// <summary>
    /// Empty places where a scale would continue the lattice: beside a scale in its row, or half a scale over
    /// in the rows above/below. Positions are (centre x, tip y).
    /// </summary>
    public static List<Pt> FreeSlots(ScaleMesh mesh)
    {
        double w = mesh.ScaleWidth, p = mesh.RowPitch;
        var slots = new List<Pt>();
        if (w <= 0 || p <= 0) return slots;
        var occupied = new SpatialIndex(mesh.Scales.Select(s => new Pt(s.Center.X, s.Tip.Y)), w);
        foreach (var s in mesh.Scales)
            foreach (var (dx, dy) in new[] { (-w, 0.0), (w, 0), (-w / 2, -p), (w / 2, -p), (-w / 2, p), (w / 2, p) })
            {
                var c = new Pt(s.Center.X + dx, s.Tip.Y + dy);
                if (occupied.AnyNear(c, w * 0.45, p * 0.45)) continue;
                slots.Add(c);
                occupied.Add(c);
            }
        return slots;
    }

    /// <summary>Marks the first and last scale of every row as the left/right template edges.</summary>
    public static void AutoDetectEdges(ScaleMesh mesh)
    {
        foreach (var s in mesh.Scales) s.Edge = EdgeSide.None;
        foreach (var g in mesh.Scales.GroupBy(s => s.Row))
        {
            var list = g.OrderBy(s => s.Center.X).ToList();
            list.First().Edge = EdgeSide.Left;
            if (list.Count > 1) list.Last().Edge = EdgeSide.Right;
        }
    }

    /// <summary>
    /// Pairs every right-edge scale with the left-edge scales in its own row and the rows directly above and
    /// below: the neighbours it will have once the template is rolled up and zipped.
    /// </summary>
    public static void AutoZip(ScaleMesh mesh)
    {
        mesh.Zips.Clear();
        var lefts = mesh.Scales.Where(s => s.Edge == EdgeSide.Left).ToList();
        foreach (var r in mesh.Scales.Where(s => s.Edge == EdgeSide.Right))
            foreach (var l in lefts)
                if (Math.Abs(l.Row - r.Row) <= 1)
                    mesh.Zips.Add(new ZipPair { A = r.Id, B = l.Id });
        BuildNeighbors(mesh);
    }

    /// <summary>Adds the pair, or removes it when it already exists. Returns true when added.</summary>
    public static bool ToggleZip(ScaleMesh mesh, int a, int b)
    {
        var existing = mesh.Zips.FirstOrDefault(z => (z.A == a && z.B == b) || (z.A == b && z.B == a));
        if (existing is not null) mesh.Zips.Remove(existing);
        else mesh.Zips.Add(new ZipPair { A = a, B = b });
        BuildNeighbors(mesh);
        return existing is null;
    }

    private sealed class SpatialIndex
    {
        private readonly double cell;
        private readonly Dictionary<(int, int), List<Pt>> grid = new();
        public SpatialIndex(IEnumerable<Pt> pts, double cell) { this.cell = cell; foreach (var p in pts) Add(p); }
        public void Add(Pt p)
        {
            var k = ((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Y / cell));
            if (!grid.TryGetValue(k, out var l)) grid[k] = l = new();
            l.Add(p);
        }
        public bool AnyNear(Pt c, double rx, double ry)
        {
            int cx = (int)Math.Floor(c.X / cell), cy = (int)Math.Floor(c.Y / cell);
            for (int x = cx - 1; x <= cx + 1; x++)
                for (int y = cy - 1; y <= cy + 1; y++)
                    if (grid.TryGetValue((x, y), out var l) && l.Any(p => Math.Abs(p.X - c.X) < rx && Math.Abs(p.Y - c.Y) < ry)) return true;
            return false;
        }
    }
}
