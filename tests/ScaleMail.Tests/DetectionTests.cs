using ScaleMail.Core.Detection;
using ScaleMail.Core.Model;
using Xunit;

namespace ScaleMail.Tests;

public class DetectionTests
{
    private static ScaleMesh DetectSample()
    {
        var (lum, w, h) = Pgm.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures/tail_template.pgm"));
        return ScaleDetector.DetectGray(lum, w, h, new DetectionOptions());
    }

    [Fact]
    public void SampleTemplate_FindsScalesRowsAndPattern()
    {
        var m = DetectSample();
        Assert.InRange(m.Scales.Count, 500, 600);
        Assert.InRange(m.RowCount, 75, 90);
        Assert.InRange(m.RowPitch, 12, 16);
        Assert.InRange(m.ScaleWidth, 30, 37);
        // Roughly a fifth of the template is grey.
        Assert.InRange(m.Scales.Count(s => s.Colored), 80, 170);
        Assert.NotEmpty(m.DetectedHoles);
    }

    [Fact]
    public void SampleTemplate_RowsNarrowTowardTheTip()
    {
        var m = DetectSample();
        var counts = m.Scales.GroupBy(s => s.Row).OrderBy(g => g.Key).Select(g => g.Count()).ToList();
        Assert.True(counts.Take(10).Max() < counts.Skip(10).Take(20).Max());
        Assert.True(counts.Skip(counts.Count - 5).Max() < counts.Max());
    }

    [Fact]
    public void SampleTemplate_NeighborsAreSymmetric()
    {
        var m = DetectSample();
        foreach (var s in m.Scales)
            foreach (var n in s.Neighbors)
                Assert.Contains(m.Find(n.ScaleId)!.Neighbors, x => x.ScaleId == s.Id);
        Assert.True(m.Scales.Average(s => s.Neighbors.Count) > 3);
    }

    [Fact]
    public void SyntheticGrid_IsDetectedExactly()
    {
        // 4 columns x 3 rows of 20x20 cells separated by 2px black lines.
        int cell = 20, line = 2, cols = 4, rows = 3, pad = 10;
        int w = pad * 2 + cols * (cell + line) + line, h = pad * 2 + rows * (cell + line) + line;
        var lum = Enumerable.Repeat((byte)255, w * h).ToArray();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int lx = x - pad, ly = y - pad;
                if (lx < 0 || ly < 0 || lx >= cols * (cell + line) + line || ly >= rows * (cell + line) + line) continue;
                bool wall = lx % (cell + line) < line || ly % (cell + line) < line;
                if (wall) lum[y * w + x] = 0;
                else if ((lx / (cell + line) + ly / (cell + line)) % 2 == 0) lum[y * w + x] = 190;
            }
        var m = ScaleDetector.DetectGray(lum, w, h, new DetectionOptions());
        Assert.Equal(12, m.Scales.Count);
        Assert.Equal(3, m.RowCount);
        Assert.Equal(6, m.Scales.Count(s => s.Colored));
        Assert.All(m.Scales, s => Assert.Equal(4, m.Scales.Count(o => o.Row == s.Row)));
    }
}
