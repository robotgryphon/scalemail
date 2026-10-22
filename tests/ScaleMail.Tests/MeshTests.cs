using System.Numerics;
using ScaleMail.Core.Detection;
using ScaleMail.Core.Export;
using ScaleMail.Core.Mesh3D;
using ScaleMail.Core.Model;
using Xunit;

namespace ScaleMail.Tests;

public class MeshTests
{
    private static ScaleMesh Sample()
    {
        var (lum, w, h) = Pgm.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures/tail_template.pgm"));
        var m = ScaleDetector.DetectGray(lum, w, h, new DetectionOptions());
        LinkPlanner.Apply(m, LinkPreset.TopCorners);
        return m;
    }

    [Fact]
    public void Triangulator_PreservesAreaWithHole()
    {
        var outer = new List<(double, double)> { (0, 0), (10, 0), (10, 10), (0, 10) };
        var hole = (IReadOnlyList<(double x, double y)>)Enumerable.Range(0, 16)
            .Select(i => (5 + 2 * Math.Cos(i * Math.PI / 8), 5 + 2 * Math.Sin(i * Math.PI / 8))).ToList();
        var (v, t) = Triangulator.Triangulate(outer, new[] { hole });
        double area = 0;
        for (int i = 0; i < t.Count; i += 3)
        {
            var a = v[t[i]]; var b = v[t[i + 1]]; var c = v[t[i + 2]];
            area += Math.Abs((b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y)) / 2;
        }
        double holeArea = 0.5 * 16 * 4 * Math.Sin(2 * Math.PI / 16);
        Assert.Equal(100 - holeArea, area, 3);
    }

    [Fact]
    public void Build_ProducesClosedSolidPerScale()
    {
        var mesh = Sample();
        var m = ScaleMeshBuilder.Build(mesh, new Build3DOptions());
        Assert.True(m.TriangleCount > mesh.Scales.Count * 40);

        // Closed 2-manifold check on welded positions: each undirected edge used by an even >0 number of triangles.
        var counts = new Dictionary<((int, int, int), (int, int, int)), int>();
        (int, int, int) Q(Vector3 v) => ((int)Math.Round(v.X * 1000), (int)Math.Round(v.Y * 1000), (int)Math.Round(v.Z * 1000));
        for (int t = 0; t < m.TriangleCount; t++)
            for (int k = 0; k < 3; k++)
            {
                var a = Q(m.Vertices[m.Indices[t * 3 + k]]); var b = Q(m.Vertices[m.Indices[t * 3 + (k + 1) % 3]]);
                if (a == b) continue;
                var key = a.CompareTo(b) < 0 ? (a, b) : (b, a);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        Assert.All(counts, kv => Assert.True(kv.Value % 2 == 0, $"open edge {kv.Key}"));
    }

    [Fact]
    public void Build_WrapMakesModelDeeperThanFlat()
    {
        var mesh = Sample();
        var flat = ScaleMeshBuilder.Build(mesh, new Build3DOptions { Flat = false, WrapDegrees = 0 }).Bounds();
        var wrapped = ScaleMeshBuilder.Build(mesh, new Build3DOptions { Flat = false, WrapDegrees = 270 }).Bounds();
        Assert.True(wrapped.max.Z - wrapped.min.Z > (flat.max.Z - flat.min.Z) * 2);
    }

    [Fact]
    public void Build_FlatIsPlanarLayeredPlates()
    {
        var mesh = Sample();
        var m = ScaleMeshBuilder.Build(mesh, new Build3DOptions { ThicknessMm = 1 });
        var (min, max) = m.Bounds();
        // Three overlap layers of 1mm plates.
        Assert.Equal(3.0, max.Z - min.Z, 3);
        foreach (var g in Enumerable.Range(0, m.TriangleCount).GroupBy(t => m.TriScale[t]))
        {
            var zs = g.SelectMany(t => Enumerable.Range(0, 3).Select(k => m.Vertices[m.Indices[t * 3 + k]].Z)).Distinct().Select(z => Math.Round(z, 4)).ToList();
            Assert.Equal(2, zs.Count);
        }
    }

    [Fact]
    public void Triangulator_ImprovesMinimumAngle()
    {
        var outline = LinkPlanner.Outline(12).Select(p => (p.u * 6, p.v * 15)).ToList();
        var (v, t) = Triangulator.Triangulate(outline, Array.Empty<IReadOnlyList<(double x, double y)>>());
        double minAngle = double.MaxValue;
        for (int i = 0; i < t.Count; i += 3)
        {
            var a = v[t[i]]; var b = v[t[i + 1]]; var c = v[t[i + 2]];
            foreach (var (p, q, r) in new[] { (a, b, c), (b, c, a), (c, a, b) })
            {
                double ax = q.x - p.x, ay = q.y - p.y, bx = r.x - p.x, by = r.y - p.y;
                minAngle = Math.Min(minAngle, Math.Acos((ax * bx + ay * by) / Math.Sqrt((ax * ax + ay * ay) * (bx * bx + by * by))));
            }
        }
        Assert.True(minAngle > 5 * Math.PI / 180, $"min angle {minAngle * 180 / Math.PI:F1}°");
    }

    [Fact]
    public void StlExport_HasExpectedSize()
    {
        var m = ScaleMeshBuilder.Build(Sample(), new Build3DOptions());
        var stl = MeshExporter.ToStl(m);
        Assert.Equal(84 + 50 * m.TriangleCount, stl.Length);
        var onlyColored = MeshExporter.ToStl(m, id => m.ScaleColored[id]);
        Assert.True(onlyColored.Length < stl.Length);
    }

    [Fact]
    public void ProjectFile_RoundTrips()
    {
        var mesh = Sample();
        mesh.Scales[3].Links.Add(new LinkPoint { U = 0.2, V = 0.7 });
        var back = ProjectFile.Load(ProjectFile.Save(mesh));
        Assert.Equal(mesh.Scales.Count, back.Scales.Count);
        Assert.Equal(mesh.Scales[3].Links.Count, back.Scales[3].Links.Count);
        Assert.Equal(mesh.Scales[7].Tip, back.Scales[7].Tip);
    }
}
