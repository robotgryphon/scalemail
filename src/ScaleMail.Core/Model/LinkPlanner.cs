namespace ScaleMail.Core.Model;

public enum LinkPreset
{
    None,
    /// <summary>One hole centred near the top edge.</summary>
    TopCenter,
    /// <summary>Two holes near the upper corners.</summary>
    TopCorners,
    /// <summary>Top centre plus one on each side.</summary>
    TopAndSides,
}

public static class LinkPlanner
{
    public static void Apply(ScaleMesh mesh, LinkPreset preset, bool keepExisting = false)
    {
        foreach (var s in mesh.Scales)
        {
            if (!keepExisting) s.Links.Clear();
            switch (preset)
            {
                case LinkPreset.TopCenter:
                    s.Links.Add(new LinkPoint { U = 0, V = 0.12 });
                    break;
                case LinkPreset.TopCorners:
                    s.Links.Add(new LinkPoint { U = -0.55, V = 0.14 });
                    s.Links.Add(new LinkPoint { U = 0.55, V = 0.14 });
                    break;
                case LinkPreset.TopAndSides:
                    s.Links.Add(new LinkPoint { U = 0, V = 0.12 });
                    s.Links.Add(new LinkPoint { U = -0.75, V = 0.4 });
                    s.Links.Add(new LinkPoint { U = 0.75, V = 0.4 });
                    break;
            }
        }
    }

    /// <summary>Scale outline in local (u,v) space: u in [-1,1], v in [0,1]; flat top, round shoulders, pointed tip.</summary>
    public static (double u, double v)[] Outline(int segments = 32)
    {
        var pts = new List<(double, double)>();
        // Left side down to tip along a rounded-shield curve: u = ±sin-ish profile.
        // Top edge: straight line from (-1,0) to (1,0). Sides: bulge, then taper to the tip at (0,1).
        for (int i = 0; i <= segments; i++)
        {
            double t = (double)i / segments; // 0 at top-left .. 1 at tip
            pts.Add((-Profile(t), t));
        }
        for (int i = segments - 1; i >= 0; i--)
        {
            double t = (double)i / segments;
            pts.Add((Profile(t), t));
        }
        // Drop the duplicated tip and top corners naturally; polygon is (left top .. tip .. right top).
        pts.RemoveAt(segments + 1); // duplicate tip
        return pts.ToArray();
    }

    // Half-width at normalised depth t: full width through the top 30%, then a parabolic-ish taper to a point.
    private static double Profile(double t)
    {
        const double flat = 0.25;
        if (t <= flat) return 1;
        double k = (t - flat) / (1 - flat);
        return Math.Sqrt(Math.Max(0, 1 - k * k)) * 0.98 + 0.02 * (1 - k);
    }
}
