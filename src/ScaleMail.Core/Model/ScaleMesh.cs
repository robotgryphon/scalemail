namespace ScaleMail.Core.Model;

/// <summary>A point in template (pixel) space. Y grows downward, like the image.</summary>
public readonly record struct Pt(double X, double Y)
{
    public static double Distance(Pt a, Pt b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>
/// A linking point (hole / ring attachment) in a scale's local frame.
/// U runs -1 (left edge) .. +1 (right edge); V runs 0 (top edge, hidden under the row above) .. 1 (tip).
/// </summary>
public sealed class LinkPoint
{
    public double U { get; set; }
    public double V { get; set; }
}

/// <summary>Seam neighbours join across the seam when the template is zipped into a tube.</summary>
public enum NeighborKind { Left, Right, Above, Below, Seam }

/// <summary>Which template edge a scale was painted onto; edge scales are the ones that get zipped together.</summary>
public enum EdgeSide { None, Left, Right }

/// <summary>Two scales that will sit next to each other once the template is zipped.</summary>
public sealed class ZipPair
{
    public int A { get; set; }
    public int B { get; set; }
}

public sealed class Neighbor
{
    public int ScaleId { get; set; }
    public NeighborKind Kind { get; set; }
}

public sealed class Scale
{
    public int Id { get; set; }
    public int Row { get; set; }
    public int Col { get; set; }

    /// <summary>Centroid of the visible part of the scale in the template.</summary>
    public Pt Center { get; set; }
    /// <summary>Lowest point of the visible region; the scale's tip sits here.</summary>
    public Pt Tip { get; set; }
    public double VisibleArea { get; set; }

    /// <summary>Mean brightness (0-255) of the filled area in the template.</summary>
    public double Shade { get; set; }
    /// <summary>True when the template coloured this scale (the pattern colour).</summary>
    public bool Colored { get; set; }
    public EdgeSide Edge { get; set; }

    public List<LinkPoint> Links { get; set; } = new();
    public List<Neighbor> Neighbors { get; set; } = new();
}

/// <summary>The 2D mesh: every scale found in the template plus shared lattice metrics.</summary>
public sealed class ScaleMesh
{
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }

    /// <summary>Full scale width in template pixels (median spacing of neighbours in a row).</summary>
    public double ScaleWidth { get; set; }
    /// <summary>Full scale length in template pixels (top edge to tip).</summary>
    public double ScaleLength { get; set; }
    /// <summary>Median vertical distance between successive rows in pixels.</summary>
    public double RowPitch { get; set; }
    public int RowCount { get; set; }

    public List<Scale> Scales { get; set; } = new();
    /// <summary>Scale pairs that meet across the seam when zipped.</summary>
    public List<ZipPair> Zips { get; set; } = new();
    /// <summary>Small enclosed circles found in the template, usually pre-drawn holes.</summary>
    public List<Pt> DetectedHoles { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    private Dictionary<int, Scale>? index;

    /// <summary>Scale by its stable id (ids survive adding and removing scales).</summary>
    public Scale? Find(int id)
    {
        index ??= Scales.ToDictionary(s => s.Id);
        return index.GetValueOrDefault(id);
    }

    /// <summary>Call after changing the scale list.</summary>
    public void Reindex() => index = null;

    public int NewId() => Scales.Count == 0 ? 0 : Scales.Max(s => s.Id) + 1;
}
