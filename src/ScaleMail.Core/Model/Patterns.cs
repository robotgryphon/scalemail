namespace ScaleMail.Core.Model;

public enum PatternKind { Sheet, Row, Triangle, InvertedTriangle, Trapezoid, Diamond, Single }

/// <summary>A prebuilt section shape for the toolbox. Parameter labels are null when unused.</summary>
public sealed record PatternInfo(PatternKind Kind, string Name, string? LabelA, string? LabelB, int DefaultA, int DefaultB);

public static class Patterns
{
    public static readonly IReadOnlyList<PatternInfo> All = new[]
    {
        new PatternInfo(PatternKind.Sheet, "Sheet", "Columns", "Rows", 6, 5),
        new PatternInfo(PatternKind.Row, "Row", "Scales", null, 6, 1),
        new PatternInfo(PatternKind.Triangle, "Triangle", "Rows", null, 5, 0),
        new PatternInfo(PatternKind.InvertedTriangle, "Inverted triangle", "Rows", null, 5, 0),
        new PatternInfo(PatternKind.Trapezoid, "Trapezoid", "Top", "Bottom", 3, 7),
        new PatternInfo(PatternKind.Diamond, "Diamond", "Widest row", null, 5, 0),
        new PatternInfo(PatternKind.Single, "Single scale", null, null, 1, 0),
    };

    public static PatternInfo Info(PatternKind kind) => All.First(p => p.Kind == kind);

    /// <summary>Scales per row for the pattern, top to bottom. Sheet rows alternate with a half-scale shift.</summary>
    public static List<int> RowCounts(PatternKind kind, int a, int b)
    {
        a = Math.Clamp(a, 1, 60); b = Math.Clamp(b, 1, 60);
        var rows = new List<int>();
        switch (kind)
        {
            case PatternKind.Sheet: for (int r = 0; r < b; r++) rows.Add(a); break;
            case PatternKind.Row: rows.Add(a); break;
            case PatternKind.Triangle: for (int r = 1; r <= a; r++) rows.Add(r); break;
            case PatternKind.InvertedTriangle: for (int r = a; r >= 1; r--) rows.Add(r); break;
            case PatternKind.Trapezoid:
                int step = b >= a ? 1 : -1;
                for (int c = a; c != b + step; c += step) rows.Add(c);
                break;
            case PatternKind.Diamond:
                for (int r = 1; r <= a; r++) rows.Add(r);
                for (int r = a - 1; r >= 1; r--) rows.Add(r);
                break;
            default: rows.Add(1); break;
        }
        return rows;
    }

    /// <summary>Builds the 2D mesh for a pattern, centred on the local origin.</summary>
    public static ScaleMesh Create(PatternKind kind, int a, int b, Layout lattice)
    {
        double w = lattice.ScaleWidth, pitch = lattice.RowPitch;
        var rows = RowCounts(kind, a, b);
        var mesh = new ScaleMesh { ScaleWidth = w, RowPitch = pitch, ScaleLength = lattice.ScaleLength };
        double tipOffset = pitch * 0.55;
        for (int r = 0; r < rows.Count; r++)
        {
            double shift = kind == PatternKind.Sheet && r % 2 == 1 ? w / 2 : 0;
            for (int i = 0; i < rows[r]; i++)
            {
                double x = (i - (rows[r] - 1) / 2.0) * w + shift;
                double tipY = r * pitch;
                mesh.Scales.Add(new Scale
                {
                    Id = mesh.Scales.Count,
                    Center = new Pt(x, tipY - tipOffset),
                    Tip = new Pt(x, tipY),
                });
            }
        }
        Recenter(mesh);
        MeshTopology.Rebuild(mesh);
        return mesh;
    }

    /// <summary>Moves the mesh so the middle of its scale centres sits at the origin.</summary>
    public static void Recenter(ScaleMesh mesh)
    {
        if (mesh.Scales.Count == 0) return;
        double cx = (mesh.Scales.Min(s => s.Center.X) + mesh.Scales.Max(s => s.Center.X)) / 2;
        double cy = (mesh.Scales.Min(s => s.Center.Y) + mesh.Scales.Max(s => s.Center.Y)) / 2;
        foreach (var s in mesh.Scales) MeshTopology.Move(s, -cx, -cy);
    }
}
