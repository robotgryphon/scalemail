using System.Numerics;
using ScaleMail.Core.Detection;
using ScaleMail.Core.Mesh3D;
using ScaleMail.Core.Model;
using Xunit;

namespace ScaleMail.Tests;

public class LayoutTests
{
    [Theory]
    [InlineData(PatternKind.Sheet, 6, 5, 30)]
    [InlineData(PatternKind.Row, 6, 1, 6)]
    [InlineData(PatternKind.Triangle, 5, 0, 15)]
    [InlineData(PatternKind.InvertedTriangle, 5, 0, 15)]
    [InlineData(PatternKind.Trapezoid, 3, 7, 25)]
    [InlineData(PatternKind.Trapezoid, 7, 3, 25)]
    [InlineData(PatternKind.Diamond, 5, 0, 25)]
    [InlineData(PatternKind.Single, 1, 1, 1)]
    public void Patterns_HaveExpectedScaleCounts(PatternKind kind, int a, int b, int expected)
    {
        var mesh = Patterns.Create(kind, a, b, new Layout());
        Assert.Equal(expected, mesh.Scales.Count);
    }

    [Fact]
    public void Pattern_InteriorScalesHaveSixNeighbours()
    {
        var mesh = Patterns.Create(PatternKind.Sheet, 7, 7, new Layout());
        var middle = mesh.Scales.First(s => s.Row == 3 && s.Col == 3);
        Assert.Equal(6, middle.Neighbors.Count);
        var tri = Patterns.Create(PatternKind.Triangle, 6, 0, new Layout());
        Assert.Equal(6, tri.Scales.First(s => s.Row == 3 && s.Col == 1).Neighbors.Count);
    }

    [Fact]
    public void SectionTransform_RoundTrips()
    {
        var sec = new Section { X = 120, Y = -40, Rotation = 37 };
        var p = new Pt(13, -7);
        var back = sec.ToLocal(sec.ToWorld(p));
        Assert.Equal(p.X, back.X, 6); Assert.Equal(p.Y, back.Y, 6);
        var rotated = new Section { Rotation = 90 }.ToWorld(new Pt(1, 0));
        Assert.Equal(0, rotated.X, 6); Assert.Equal(1, rotated.Y, 6);
    }

    private static (Layout layout, Section left, Section right) TwoSheets()
    {
        var layout = new Layout();
        var left = LayoutOps.AddPattern(layout, PatternKind.Sheet, 4, 4, new Pt(0, 0));
        // Place the second sheet one scale width to the right of the first one's last column.
        var rightSheet = LayoutOps.AddPattern(layout, PatternKind.Sheet, 4, 4, new Pt(4.5 * layout.ScaleWidth + 5, 0));
        return (layout, left, rightSheet);
    }

    [Fact]
    public void RingCandidates_AppearInTheGapBetweenSections()
    {
        var (layout, a, b) = TwoSheets();
        var cands = LayoutOps.RingCandidates(layout, a.Id, b.Id);
        Assert.NotEmpty(cands);
        double gapMid = a.X + (b.X - a.X) / 2;
        Assert.All(cands, c => Assert.InRange(c.World.X, gapMid - layout.ScaleWidth * 1.2, gapMid + layout.ScaleWidth * 1.2));
        Assert.All(cands, c => Assert.NotEqual(c.A.Section, c.B.Section));
    }

    [Fact]
    public void Rings_ToggleAndFollowTheirSection()
    {
        var (layout, a, b) = TwoSheets();
        var c = LayoutOps.RingCandidates(layout, a.Id, b.Id)[0];
        Assert.True(LayoutOps.ToggleRing(layout, c.A, c.B, c.World));
        Assert.Single(layout.Rings);
        Assert.DoesNotContain(LayoutOps.RingCandidates(layout, a.Id, b.Id), x => x.A.Same(c.A) && x.B.Same(c.B));

        var before = layout.RingWorld(layout.Rings[0]);
        a.X += 50;
        var after = layout.RingWorld(layout.Rings[0]);
        Assert.Equal(before.X + 50, after.X, 6);

        a.X -= 50;
        Assert.False(LayoutOps.ToggleRing(layout, c.A, c.B, c.World));
        Assert.Empty(layout.Rings);
    }

    [Fact]
    public void RemovingSectionsOrScales_DropsTheirRings()
    {
        var (layout, a, b) = TwoSheets();
        int n = LayoutOps.AutoRings(layout, a.Id, b.Id);
        Assert.True(n > 2);
        Assert.Equal(n, layout.Rings.Count);
        var r = layout.Rings[0];
        LayoutOps.RemoveScale(layout, r.A.Section, r.A.Scale);
        Assert.True(layout.Rings.Count < n);
        LayoutOps.RemoveSection(layout, b.Id);
        Assert.Empty(layout.Rings);
    }

    [Fact]
    public void SelfLink_ConnectsNonNeighbourScalesOfOneSection()
    {
        var layout = new Layout();
        var s = LayoutOps.AddPattern(layout, PatternKind.Row, 4, 1, new Pt(0, 0));
        // Bring the row's ends together by bending: duplicate positions are not neighbours, so no candidates in a straight row.
        Assert.Empty(LayoutOps.RingCandidates(layout, s.Id, s.Id));
        var first = new ScaleRef { Section = s.Id, Scale = s.Mesh.Scales.First(x => x.Col == 0).Id };
        var last = new ScaleRef { Section = s.Id, Scale = s.Mesh.Scales.First(x => x.Col == 3).Id };
        Assert.True(LayoutOps.ToggleRing(layout, first, last, new Pt(0, 0)));
    }

    [Fact]
    public void AddImported_CentresTheTemplateAndSetsTheLattice()
    {
        var (lum, w, h) = Pgm.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures/tail_template.pgm"));
        var mesh = ScaleDetector.DetectGray(lum, w, h, new DetectionOptions());
        var layout = new Layout();
        var sec = LayoutOps.AddImported(layout, mesh, "tail", new Pt(10, 20));
        Assert.InRange(layout.ScaleWidth, 30, 37);
        Assert.InRange(sec.Mesh.Scales.Average(s => s.Center.X), -10, 10);
        Assert.Equal(sec.Mesh.ScaleWidth, layout.ScaleWidth);

        // A second template at a different size is scaled to the shared lattice.
        var other = ScaleDetector.DetectGray(lum, w, h, new DetectionOptions());
        layout.ScaleWidth = other.ScaleWidth * 2; layout.RowPitch = other.RowPitch * 2; layout.ScaleLength = other.ScaleLength * 2;
        var sec2 = LayoutOps.AddImported(layout, other, "tail2", new Pt(0, 0));
        Assert.Equal(2.0, sec2.RefScale, 6);
        Assert.Equal(layout.ScaleWidth, sec2.Mesh.ScaleWidth, 6);
    }

    [Fact]
    public void Duplicate_IsIndependent()
    {
        var layout = new Layout();
        var a = LayoutOps.AddPattern(layout, PatternKind.Triangle, 3, 0, new Pt(0, 0));
        var b = LayoutOps.Duplicate(layout, a.Id, 100, 0);
        Assert.NotEqual(a.Id, b.Id);
        b.Mesh.Scales[0].Colored = true;
        Assert.False(a.Mesh.Scales[0].Colored);
        Assert.Equal(a.Mesh.Scales.Count, b.Mesh.Scales.Count);
        Assert.NotNull(b.Mesh.Find(b.Mesh.Scales[2].Id));
    }

    [Fact]
    public void LayoutBuild_ProducesClosedSolidsWithRingsThroughHoles()
    {
        var (layout, a, b) = TwoSheets();
        LayoutOps.AutoRings(layout, a.Id, b.Id);
        var m = LayoutBuilder.Build(layout, new Build3DOptions());
        Assert.Contains(m.TriScale, id => id < 0);

        var counts = new Dictionary<((int, int, int), (int, int, int)), int>();
        (int, int, int) Q(Vector3 v) => ((int)Math.Round(v.X * 1000), (int)Math.Round(v.Y * 1000), (int)Math.Round(v.Z * 1000));
        for (int t = 0; t < m.TriangleCount; t++)
            for (int k = 0; k < 3; k++)
            {
                var p = Q(m.Vertices[m.Indices[t * 3 + k]]); var q = Q(m.Vertices[m.Indices[t * 3 + (k + 1) % 3]]);
                if (p == q) continue;
                var key = p.CompareTo(q) < 0 ? (p, q) : (q, p);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        Assert.All(counts, kv => Assert.True(kv.Value % 2 == 0, $"open edge {kv.Key}"));

        // Without rings the same layout has no ring triangles and fewer triangles.
        layout.Rings.Clear();
        var plain = LayoutBuilder.Build(layout, new Build3DOptions());
        Assert.DoesNotContain(plain.TriScale, id => id < 0);
        Assert.True(plain.TriangleCount < m.TriangleCount);
    }

    [Fact]
    public void LayoutBuild_RotatedSectionRotatesThePlates()
    {
        var layout = new Layout();
        var s = LayoutOps.AddPattern(layout, PatternKind.Row, 3, 1, new Pt(0, 0));
        var flat = LayoutBuilder.Build(layout, new Build3DOptions()).Bounds();
        s.Rotation = 90;
        var rot = LayoutBuilder.Build(layout, new Build3DOptions()).Bounds();
        Assert.True(flat.max.X - flat.min.X > flat.max.Y - flat.min.Y);
        Assert.True(rot.max.Y - rot.min.Y > rot.max.X - rot.min.X);
    }

    [Fact]
    public void Layout_RoundTripsAndLoadsLegacyMesh()
    {
        var (layout, a, b) = TwoSheets();
        LayoutOps.AutoRings(layout, a.Id, b.Id);
        a.Rotation = 15;
        var back = ProjectFile.LoadLayout(ProjectFile.SaveLayout(layout));
        Assert.Equal(layout.Sections.Count, back.Sections.Count);
        Assert.Equal(layout.Rings.Count, back.Rings.Count);
        Assert.Equal(15, back.Sections[0].Rotation);
        Assert.NotNull(back.Sections[1].Mesh.Find(3));

        var legacy = ProjectFile.LoadLayout(ProjectFile.Save(Patterns.Create(PatternKind.Triangle, 3, 0, new Layout())));
        Assert.Single(legacy.Sections);
        Assert.Equal(6, legacy.Sections[0].Mesh.Scales.Count);
    }
}
