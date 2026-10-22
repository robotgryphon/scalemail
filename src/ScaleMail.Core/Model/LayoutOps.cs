using System.Text.Json;

namespace ScaleMail.Core.Model;

public sealed record RingCandidate(ScaleRef A, ScaleRef B, Pt World);

/// <summary>Editing operations on a <see cref="Layout"/>.</summary>
public static class LayoutOps
{
    public static Section AddPattern(Layout layout, PatternKind kind, int a, int b, Pt world)
    {
        var info = Patterns.Info(kind);
        var sec = new Section
        {
            Id = layout.NewSectionId(),
            Name = $"{info.Name} {layout.Sections.Count(s => s.Name.StartsWith(info.Name)) + 1}",
            X = world.X, Y = world.Y,
            Mesh = Patterns.Create(kind, a, b, layout),
        };
        layout.Sections.Add(sec);
        return sec;
    }

    /// <summary>
    /// Adds a detected template as a section. The first section sets the shared lattice; later ones are scaled to fit it.
    /// </summary>
    public static Section AddImported(Layout layout, ScaleMesh mesh, string name, Pt world)
    {
        double f = 1;
        if (layout.Sections.Count == 0)
        {
            layout.ScaleWidth = mesh.ScaleWidth > 0 ? mesh.ScaleWidth : layout.ScaleWidth;
            layout.RowPitch = mesh.RowPitch > 0 ? mesh.RowPitch : layout.RowPitch;
            layout.ScaleLength = mesh.ScaleLength > 0 ? mesh.ScaleLength : layout.ScaleLength;
        }
        else if (mesh.ScaleWidth > 0) f = layout.ScaleWidth / mesh.ScaleWidth;

        // Re-origin at the middle of the scales so the section rotates about its centre.
        double cx = mesh.Scales.Count == 0 ? 0 : (mesh.Scales.Min(s => s.Center.X) + mesh.Scales.Max(s => s.Center.X)) / 2;
        double cy = mesh.Scales.Count == 0 ? 0 : (mesh.Scales.Min(s => s.Center.Y) + mesh.Scales.Max(s => s.Center.Y)) / 2;
        foreach (var s in mesh.Scales)
        {
            s.Center = new Pt((s.Center.X - cx) * f, (s.Center.Y - cy) * f);
            s.Tip = new Pt((s.Tip.X - cx) * f, (s.Tip.Y - cy) * f);
        }
        mesh.ScaleWidth = layout.ScaleWidth; mesh.RowPitch = layout.RowPitch; mesh.ScaleLength = layout.ScaleLength;
        MeshTopology.Rebuild(mesh);

        var sec = new Section
        {
            Id = layout.NewSectionId(), Name = name, X = world.X, Y = world.Y, Mesh = mesh,
            RefX = -cx * f, RefY = -cy * f, RefScale = f, RefWidth = mesh.ImageWidth, RefHeight = mesh.ImageHeight,
        };
        layout.Sections.Add(sec);
        return sec;
    }

    public static Section Duplicate(Layout layout, int sectionId, double dx, double dy)
    {
        var src = layout.FindSection(sectionId)!;
        var clone = JsonSerializer.Deserialize<Section>(JsonSerializer.Serialize(src))!;
        clone.Id = layout.NewSectionId();
        clone.Name = src.Name + " copy";
        clone.X += dx; clone.Y += dy;
        clone.Mesh.Reindex();
        layout.Sections.Add(clone);
        return clone;
    }

    public static void RemoveSection(Layout layout, int sectionId)
    {
        layout.Sections.RemoveAll(s => s.Id == sectionId);
        layout.Rings.RemoveAll(r => r.A.Section == sectionId || r.B.Section == sectionId);
    }

    public static void RemoveScale(Layout layout, int sectionId, int scaleId)
    {
        var sec = layout.FindSection(sectionId);
        if (sec is null) return;
        MeshTopology.RemoveScale(sec.Mesh, scaleId);
        layout.Rings.RemoveAll(r => (r.A.Section == sectionId && r.A.Scale == scaleId) || (r.B.Section == sectionId && r.B.Scale == scaleId));
        sec.Touch();
    }

    // ---- rings ----------------------------------------------------------------------------

    public static Ring? FindRing(Layout layout, ScaleRef a, ScaleRef b) =>
        layout.Rings.FirstOrDefault(r => (r.A.Same(a) && r.B.Same(b)) || (r.A.Same(b) && r.B.Same(a)));

    /// <summary>Adds a ring joining two scales at a canvas position; removes the existing one instead. Returns true when added.</summary>
    public static bool ToggleRing(Layout layout, ScaleRef a, ScaleRef b, Pt world)
    {
        if (a.Same(b)) return false;
        var existing = FindRing(layout, a, b);
        if (existing is not null) { layout.Rings.Remove(existing); return false; }
        var secA = layout.FindSection(a.Section)!;
        layout.Rings.Add(new Ring { Id = layout.NewRingId(), A = a, B = b, Anchor = secA.ToLocal(world) });
        return true;
    }

    /// <summary>
    /// Pairs of scales, one in each section (or two in the same section), close enough to be joined by a ring
    /// but not already joined. Same-section pairs that are lattice neighbours are skipped.
    /// </summary>
    public static List<RingCandidate> RingCandidates(Layout layout, int sectionA, int sectionB)
    {
        var result = new List<RingCandidate>();
        var a = layout.FindSection(sectionA); var b = layout.FindSection(sectionB);
        if (a is null || b is null) return result;
        double limit = layout.ScaleWidth * 1.4;

        var worldB = b.Mesh.Scales.Select(s => (s, w: b.ToWorld(s.Center))).ToList();
        var grid = new Dictionary<(int, int), List<(Scale s, Pt w)>>();
        foreach (var e in worldB)
        {
            var key = ((int)Math.Floor(e.w.X / limit), (int)Math.Floor(e.w.Y / limit));
            if (!grid.TryGetValue(key, out var l)) grid[key] = l = new();
            l.Add(e);
        }
        foreach (var sa in a.Mesh.Scales)
        {
            var wa = a.ToWorld(sa.Center);
            int gx = (int)Math.Floor(wa.X / limit), gy = (int)Math.Floor(wa.Y / limit);
            for (int x = gx - 1; x <= gx + 1; x++)
                for (int y = gy - 1; y <= gy + 1; y++)
                {
                    if (!grid.TryGetValue((x, y), out var cell)) continue;
                    foreach (var (sb, wb) in cell)
                    {
                        if (Pt.Distance(wa, wb) > limit) continue;
                        if (sectionA == sectionB)
                        {
                            if (sa.Id >= sb.Id) continue;
                            if (sa.Neighbors.Any(n => n.ScaleId == sb.Id && n.Kind != NeighborKind.Seam)) continue;
                        }
                        var ra = new ScaleRef { Section = sectionA, Scale = sa.Id };
                        var rb = new ScaleRef { Section = sectionB, Scale = sb.Id };
                        if (FindRing(layout, ra, rb) is not null) continue;
                        result.Add(new RingCandidate(ra, rb, new Pt((wa.X + wb.X) / 2, (wa.Y + wb.Y) / 2)));
                    }
                }
        }
        return result;
    }

    public static int AutoRings(Layout layout, int sectionA, int sectionB)
    {
        var list = RingCandidates(layout, sectionA, sectionB);
        foreach (var c in list) ToggleRing(layout, c.A, c.B, c.World);
        return list.Count;
    }

    public static int RemoveRingsBetween(Layout layout, int sectionA, int sectionB) =>
        layout.Rings.RemoveAll(r => (r.A.Section == sectionA && r.B.Section == sectionB) || (r.A.Section == sectionB && r.B.Section == sectionA));
}
