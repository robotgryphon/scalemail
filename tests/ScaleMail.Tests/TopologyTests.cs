using ScaleMail.Core.Detection;
using ScaleMail.Core.Model;
using Xunit;

namespace ScaleMail.Tests;

public class TopologyTests
{
    private static ScaleMesh Sample()
    {
        var (lum, w, h) = Pgm.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures/tail_template.pgm"));
        return ScaleDetector.DetectGray(lum, w, h, new DetectionOptions());
    }

    [Fact]
    public void RemoveThenAddAtFreeSlot_RestoresCount()
    {
        var m = Sample();
        int count = m.Scales.Count, rows = m.RowCount;
        var victim = m.Scales.First(s => s.Row == 20 && s.Col == 4);
        var (x, y) = (victim.Center.X, victim.Tip.Y);
        MeshTopology.RemoveScale(m, victim.Id);
        Assert.Equal(count - 1, m.Scales.Count);
        Assert.Contains(MeshTopology.FreeSlots(m), p => Math.Abs(p.X - x) < 3 && Math.Abs(p.Y - y) < 3);

        var added = MeshTopology.AddScale(m, x, y);
        Assert.Equal(count, m.Scales.Count);
        Assert.Equal(rows, m.RowCount);
        Assert.Equal(20, added.Row);
        Assert.True(added.Neighbors.Count >= 5);
    }

    [Fact]
    public void AddedScaleIdsStayUniqueAfterRemoval()
    {
        var m = Sample();
        MeshTopology.RemoveScale(m, 0);
        MeshTopology.RemoveScale(m, 5);
        var s = MeshTopology.AddScale(m, 100, 300);
        Assert.Equal(m.Scales.Count, m.Scales.Select(x => x.Id).Distinct().Count());
        Assert.Same(s, m.Find(s.Id));
        Assert.Null(m.Find(0));
    }

    [Fact]
    public void FreeSlots_OfFullTemplateAreOnTheBoundary()
    {
        var m = Sample();
        var slots = MeshTopology.FreeSlots(m);
        Assert.InRange(slots.Count, 1, 400);
    }

    [Fact]
    public void EdgesAndZips_PairAcrossTheSeam()
    {
        var m = Sample();
        MeshTopology.AutoDetectEdges(m);
        Assert.Equal(m.RowCount, m.Scales.Count(s => s.Edge == EdgeSide.Left));
        MeshTopology.AutoZip(m);
        Assert.NotEmpty(m.Zips);
        Assert.All(m.Zips, z =>
        {
            Assert.Equal(EdgeSide.Right, m.Find(z.A)!.Edge);
            Assert.Equal(EdgeSide.Left, m.Find(z.B)!.Edge);
            Assert.True(Math.Abs(m.Find(z.A)!.Row - m.Find(z.B)!.Row) <= 1);
        });
        var a = m.Find(m.Zips[0].A)!;
        Assert.Contains(a.Neighbors, n => n.Kind == NeighborKind.Seam);

        // Deleting a scale drops its zips.
        int before = m.Zips.Count;
        MeshTopology.RemoveScale(m, m.Zips[0].B);
        Assert.True(m.Zips.Count < before);
    }

    [Fact]
    public void ToggleZip_AddsAndRemoves()
    {
        var m = Sample();
        Assert.True(MeshTopology.ToggleZip(m, 1, 2));
        Assert.False(MeshTopology.ToggleZip(m, 2, 1));
        Assert.Empty(m.Zips);
    }

    [Fact]
    public void ProjectFile_KeepsEdgesAndZips()
    {
        var m = Sample();
        MeshTopology.AutoDetectEdges(m); MeshTopology.AutoZip(m);
        var back = ProjectFile.Load(ProjectFile.Save(m));
        Assert.Equal(m.Zips.Count, back.Zips.Count);
        Assert.Equal(m.Scales.Count(s => s.Edge == EdgeSide.Right), back.Scales.Count(s => s.Edge == EdgeSide.Right));
        Assert.NotNull(back.Find(m.Scales[3].Id));
    }
}
