using System.Text.Json;

namespace ScaleMail.Core.Model;

/// <summary>Save/load of the layout (sections, scales, rings) as JSON.</summary>
public static class ProjectFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Save(ScaleMesh mesh) => JsonSerializer.Serialize(mesh, Options);

    public static ScaleMesh Load(string json) =>
        JsonSerializer.Deserialize<ScaleMesh>(json, Options) ?? throw new InvalidDataException("Empty project file.");

    public static string SaveLayout(Layout layout) => JsonSerializer.Serialize(layout, Options);

    /// <summary>Loads a layout; an older single-mesh project becomes a layout with one section.</summary>
    public static Layout LoadLayout(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Layout layout;
        if (doc.RootElement.TryGetProperty("sections", out _) || doc.RootElement.TryGetProperty("Sections", out _))
            layout = JsonSerializer.Deserialize<Layout>(json, Options) ?? throw new InvalidDataException("Empty project file.");
        else
        {
            layout = new Layout();
            var mesh = JsonSerializer.Deserialize<ScaleMesh>(json, Options) ?? throw new InvalidDataException("Empty project file.");
            LayoutOps.AddImported(layout, mesh, "Imported template", new Pt(0, 0));
        }
        foreach (var s in layout.Sections) s.Mesh.Reindex();
        return layout;
    }
}
