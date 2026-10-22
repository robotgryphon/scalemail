using System.Text.Json.Serialization;

namespace ScaleMail.Core.Model;

/// <summary>Points at one scale inside one section.</summary>
public sealed class ScaleRef
{
    public int Section { get; set; }
    public int Scale { get; set; }
    public override string ToString() => $"{Section}:{Scale}";
    public bool Same(ScaleRef o) => Section == o.Section && Scale == o.Scale;
}

/// <summary>A ring joining two scales, typically across the gap between two sections.</summary>
public sealed class Ring
{
    public int Id { get; set; }
    public ScaleRef A { get; set; } = new();
    public ScaleRef B { get; set; } = new();
    /// <summary>Where the ring sits, in the local template space of scale A's section.</summary>
    public Pt Anchor { get; set; }
}

/// <summary>A patch of scales with its own 2D mesh, placed (moved and rotated) on the layout canvas.</summary>
public sealed class Section
{
    public int Id { get; set; }
    public string Name { get; set; } = "Section";
    /// <summary>Canvas position of the section's local origin.</summary>
    public double X { get; set; }
    public double Y { get; set; }
    /// <summary>Clockwise rotation in degrees (the canvas y axis points down).</summary>
    public double Rotation { get; set; }
    public ScaleMesh Mesh { get; set; } = new();
    /// <summary>Reference image placement in local space, when the section came from an imported template.</summary>
    public double RefX { get; set; }
    public double RefY { get; set; }
    public double RefScale { get; set; } = 1;
    public int RefWidth { get; set; }
    public int RefHeight { get; set; }

    /// <summary>Bumped on every edit so views can skip redrawing untouched sections.</summary>
    [JsonIgnore] public int Revision { get; set; }

    private double Cos => Math.Cos(Rotation * Math.PI / 180);
    private double Sin => Math.Sin(Rotation * Math.PI / 180);

    public Pt ToWorld(Pt p) => new(X + p.X * Cos - p.Y * Sin, Y + p.X * Sin + p.Y * Cos);
    public Pt ToLocal(Pt w)
    {
        double dx = w.X - X, dy = w.Y - Y;
        return new Pt(dx * Cos + dy * Sin, -dx * Sin + dy * Cos);
    }
    /// <summary>Rotates a direction/delta from canvas space into local space.</summary>
    public Pt VectorToLocal(double dx, double dy) => new(dx * Cos + dy * Sin, -dx * Sin + dy * Cos);

    public void Touch() => Revision++;

    /// <summary>Local bounding box of the scale outlines: (minX, minY, maxX, maxY).</summary>
    public (double minX, double minY, double maxX, double maxY) LocalBounds(double scaleWidth, double scaleLength)
    {
        if (Mesh.Scales.Count == 0) return (-scaleWidth / 2, -scaleLength / 2, scaleWidth / 2, scaleLength / 2);
        return (Mesh.Scales.Min(s => s.Center.X) - scaleWidth / 2, Mesh.Scales.Min(s => s.Tip.Y) - scaleLength,
                Mesh.Scales.Max(s => s.Center.X) + scaleWidth / 2, Mesh.Scales.Max(s => s.Tip.Y));
    }
}

/// <summary>The working document: sections placed on a canvas, joined by rings.</summary>
public sealed class Layout
{
    /// <summary>Lattice shared by every section, in canvas units.</summary>
    public double ScaleWidth { get; set; } = 33.5;
    public double RowPitch { get; set; } = 13.9;
    public double ScaleLength { get; set; } = 41.7;

    public List<Section> Sections { get; set; } = new();
    public List<Ring> Rings { get; set; } = new();

    public Section? FindSection(int id) => Sections.FirstOrDefault(s => s.Id == id);
    public Scale? FindScale(ScaleRef r) => FindSection(r.Section)?.Mesh.Find(r.Scale);
    public int NewSectionId() => Sections.Count == 0 ? 1 : Sections.Max(s => s.Id) + 1;
    public int NewRingId() => Rings.Count == 0 ? 1 : Rings.Max(r => r.Id) + 1;

    public Pt ScaleWorldCenter(ScaleRef r)
    {
        var sec = FindSection(r.Section)!; var sc = sec.Mesh.Find(r.Scale)!;
        return sec.ToWorld(sc.Center);
    }

    public Pt RingWorld(Ring ring) => FindSection(ring.A.Section)!.ToWorld(ring.Anchor);
}
