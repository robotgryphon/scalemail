using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using ScaleMail.Core.Detection;
using ScaleMail.Core.Export;
using ScaleMail.Core.Mesh3D;
using ScaleMail.Core.Model;
using ScaleMail.Web.Components;
using ScaleLayout = ScaleMail.Core.Model.Layout;

namespace ScaleMail.Web.Pages;

public partial class Home : IDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private HttpClient Http { get; set; } = default!;

    private enum Mode { Select, Link, Edit }
    private enum EditTool { Select, Add, Delete, Move, Paint }
    private enum ViewMode { Scales, Markers }

    private sealed class ImportData
    {
        public byte[] Rgba = Array.Empty<byte>();
        public int W, H;
        public string Uri = "";
        public DetectionOptions Options = new();
    }

    private static readonly (int n, string label)[] Steps = { (1, "Import"), (2, "Layout"), (3, "Build 3D") };

    private ScaleLayout doc = new();
    private readonly Dictionary<int, ImportData> imports = new();
    private readonly Build3DOptions b3 = new();
    private TriMesh? tri;
    private string? error;
    private int step = 1;
    private bool busy, previewDirty;

    private Mode mode = Mode.Select;
    private EditTool editTool = EditTool.Select;
    private ViewMode viewMode = ViewMode.Scales;
    private bool showRef = true;
    private double refOpacity = 1;
    private int? selectedSection, editSection, selectedScale;

    private ElementReference stageEl, svgEl, previewCanvas;
    private DotNetObjectReference<Home>? self;

    // ---- view ----------------------------------------------------------------------------
    private double cx, cy, k = 1, stageW = 900, stageH = 600;
    private double vw => stageW / k;
    private double vh => stageH / k;
    private double vx => cx - vw / 2;
    private double vy => cy - vh / 2;
    private string ViewBox() => $"{F(vx)} {F(vy)} {F(vw)} {F(vh)}";
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    [JSInvokable]
    public void OnStageSize(double w, double h)
    {
        if (w < 10 || h < 10 || (Math.Abs(w - stageW) < 1 && Math.Abs(h - stageH) < 1)) return;
        stageW = w; stageH = h;
        InvokeAsync(StateHasChanged);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            self = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("scalemail.observe", stageEl, self);
        }
        if (previewDirty && step == 3 && tri is not null)
        {
            previewDirty = false;
            var (xyz, colored) = tri.ToPreview();
            var bytes = new byte[xyz.Length * 4];
            Buffer.BlockCopy(xyz, 0, bytes, 0, bytes.Length);
            await JS.InvokeVoidAsync("scalemail.setMesh", previewCanvas, bytes, colored);
        }
    }

    public void Dispose() => self?.Dispose();

    private void ZoomBy(double f) => k = Math.Clamp(k * f, 0.05, 8);

    private void Fit()
    {
        if (doc.Sections.Count == 0) { cx = cy = 0; k = 0.5; return; }
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var s in doc.Sections)
        {
            var b = s.LocalBounds(doc.ScaleWidth, doc.ScaleLength);
            foreach (var c in new[] { new Pt(b.minX, b.minY), new Pt(b.maxX, b.minY), new Pt(b.minX, b.maxY), new Pt(b.maxX, b.maxY) })
            {
                var w = s.ToWorld(c);
                minX = Math.Min(minX, w.X); maxX = Math.Max(maxX, w.X); minY = Math.Min(minY, w.Y); maxY = Math.Max(maxY, w.Y);
            }
        }
        cx = (minX + maxX) / 2; cy = (minY + maxY) / 2;
        k = Math.Clamp(Math.Min(stageW / Math.Max(1, (maxX - minX) * 1.15), stageH / Math.Max(1, (maxY - minY) * 1.15)), 0.05, 4);
    }

    private async Task<Pt> ToWorld(double clientX, double clientY)
    {
        var p = await JS.InvokeAsync<double[]>("scalemail.svgPoint", svgEl, clientX, clientY);
        return new Pt(p[0], p[1]);
    }

    private async Task OnWheel(WheelEventArgs e)
    {
        if (step != 2) return;
        var w = await ToWorld(e.ClientX, e.ClientY);
        double nk = Math.Clamp(k * Math.Exp(-e.DeltaY * 0.0015), 0.05, 8);
        cx = w.X - (w.X - cx) * k / nk;
        cy = w.Y - (w.Y - cy) * k / nk;
        k = nk;
    }

    // ---- steps / modes -------------------------------------------------------------------

    private bool StepDone(int n) => n switch { 1 => doc.Sections.Count > 0, 2 => step > 2, _ => tri is not null };

    private void GoStep(int n)
    {
        step = n;
        if (n != 2) mode = Mode.Select;
        if (n == 3 && tri is not null) previewDirty = true;
    }

    private Section? SelectedSection => selectedSection is int i ? doc.FindSection(i) : null;
    private Section? EditingSection => editSection is int i ? doc.FindSection(i) : null;
    private string ET(EditTool t) => editTool == t ? "primary" : "";

    private void SetMode(Mode m)
    {
        pendingScale = null;
        if (m == Mode.Edit)
        {
            if (selectedSection is null) return;
            editSection = selectedSection; editTool = EditTool.Select; selectedScale = null;
        }
        if (m == Mode.Link)
        {
            linkA = selectedSection; linkB = null; candidates = new();
        }
        mode = m;
    }

    private bool SectionActive(Section s) => mode switch
    {
        Mode.Edit => s.Id == editSection,
        Mode.Link when LinkStage == 3 => s.Id == linkA || s.Id == linkB,
        _ => true,
    };

    private string CursorFor(Section s) => mode switch
    {
        Mode.Select => "c-move",
        Mode.Edit when s.Id == editSection => editTool switch { EditTool.Paint => "c-paint", EditTool.Delete => "c-del", EditTool.Move => "c-move", _ => "" },
        _ => "",
    };

    private IReadOnlyCollection<int> HighlightFor(Section s)
    {
        var ids = new List<int>();
        if (pendingScale is { } p && p.Section == s.Id) ids.Add(p.Scale);
        if (mode == Mode.Edit && s.Id == editSection && selectedScale is int sc) ids.Add(sc);
        return ids;
    }

    private string? RefFor(Section s) => showRef && imports.TryGetValue(s.Id, out var i) ? i.Uri : null;

    // ---- undo ----------------------------------------------------------------------------

    private readonly List<string> undo = new();
    private string? pendingSnapshot;

    private void Snapshot()
    {
        undo.Add(ProjectFile.SaveLayout(doc));
        if (undo.Count > 30) undo.RemoveAt(0);
    }

    /// <summary>Snapshot taken at pointer-down but only kept once the drag actually changes something.</summary>
    private void CommitPendingSnapshot()
    {
        if (pendingSnapshot is null) return;
        undo.Add(pendingSnapshot); pendingSnapshot = null;
        if (undo.Count > 30) undo.RemoveAt(0);
    }

    private void Undo()
    {
        if (undo.Count == 0) return;
        doc = ProjectFile.LoadLayout(undo[^1]); undo.RemoveAt(undo.Count - 1);
        foreach (var s in doc.Sections) s.Touch();
        if (selectedSection is int i && doc.FindSection(i) is null) selectedSection = null;
        if (editSection is int e && doc.FindSection(e) is null) { editSection = null; if (mode == Mode.Edit) mode = Mode.Select; }
        pendingScale = null; selectedScale = null; tri = null;
        RefreshCandidates();
    }

    // ---- import --------------------------------------------------------------------------

    private async Task OnFile(InputFileChangeEventArgs e)
    {
        using var ms = new MemoryStream();
        await using (var st = e.File.OpenReadStream(30 * 1024 * 1024)) await st.CopyToAsync(ms);
        await Import(ms.ToArray(), e.File.ContentType, Path.GetFileNameWithoutExtension(e.File.Name));
    }

    private async Task LoadSample() => await Import(await Http.GetByteArrayAsync("sample/tail_template.jpg"), "image/jpeg", "Tail template");

    private async Task Import(byte[] bytes, string mime, string name)
    {
        try
        {
            error = null; busy = true;
            if (string.IsNullOrEmpty(mime)) mime = "image/png";
            var dims = await JS.InvokeAsync<int[]>("scalemail.decodeImage", bytes, mime);
            var data = new ImportData
            {
                Rgba = await JS.InvokeAsync<byte[]>("scalemail.takePixels"), W = dims[0], H = dims[1],
                Uri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}",
            };
            var mesh = ScaleDetector.Detect(data.Rgba, data.W, data.H, data.Options);
            if (mesh.Scales.Count == 0) { error = "No scales found in that image."; return; }
            Snapshot();
            var sec = LayoutOps.AddImported(doc, mesh, name, NextFreeSpot(mesh));
            imports[sec.Id] = data;
            selectedSection = sec.Id; tri = null;
            // Markers over the reference make detection mistakes easy to spot.
            viewMode = ViewMode.Markers; showRef = true; refOpacity = 1;
            step = 2; mode = Mode.Select;
            Fit();
        }
        catch (Exception ex) { error = "Could not read the image: " + ex.Message; }
        finally { busy = false; }
    }

    private Pt NextFreeSpot(ScaleMesh incoming)
    {
        if (doc.Sections.Count == 0) return new Pt(0, 0);
        double maxX = doc.Sections.Max(s => s.ToWorld(new Pt(s.LocalBounds(doc.ScaleWidth, doc.ScaleLength).maxX, 0)).X);
        double half = incoming.Scales.Count == 0 ? 0 : (incoming.Scales.Max(s => s.Center.X) - incoming.Scales.Min(s => s.Center.X)) / 2 + doc.ScaleWidth;
        return new Pt(maxX + 120 + half, 0);
    }

    private void Redetect()
    {
        if (SelectedSection is not { } old || !imports.TryGetValue(old.Id, out var imp)) return;
        try
        {
            error = null;
            var mesh = ScaleDetector.Detect(imp.Rgba, imp.W, imp.H, imp.Options);
            if (mesh.Scales.Count == 0) { error = "No scales found. Try raising the outline darkness limit or closing line gaps."; return; }
            Snapshot();
            var (id, x, y, rot, name) = (old.Id, old.X, old.Y, old.Rotation, old.Name);
            LayoutOps.RemoveSection(doc, id);
            var fresh = LayoutOps.AddImported(doc, mesh, name, new Pt(x, y));
            fresh.Id = id; fresh.Rotation = rot;
            tri = null;
        }
        catch (Exception ex) { error = ex.Message; }
    }

    private async Task OnProject(InputFileChangeEventArgs e)
    {
        try
        {
            using var r = new StreamReader(e.File.OpenReadStream(30 * 1024 * 1024));
            doc = ProjectFile.LoadLayout(await r.ReadToEndAsync());
            imports.Clear(); undo.Clear(); tri = null; error = null;
            selectedSection = null; editSection = null; mode = Mode.Select; viewMode = ViewMode.Scales;
            step = 2; Fit();
        }
        catch (Exception ex) { error = "Could not open project: " + ex.Message; }
    }

    // ---- toolbox -------------------------------------------------------------------------

    private PatternKind? dragKind;
    private readonly Dictionary<PatternKind, (int a, int b)> patternParams = Patterns.All.ToDictionary(p => p.Kind, p => (p.DefaultA, p.DefaultB));
    private readonly Dictionary<PatternKind, (string viewBox, List<(string x, string y)> dots, string r)> previews = new();

    private (int a, int b) Param(PatternKind kind) => patternParams[kind];

    private void SetParam(PatternKind kind, object? value, bool isA)
    {
        if (!int.TryParse(value?.ToString(), out int v)) return;
        v = Math.Clamp(v, 1, 40);
        var (a, b) = patternParams[kind];
        patternParams[kind] = isA ? (v, b) : (a, v);
        previews.Remove(kind);
    }

    private (string viewBox, List<(string x, string y)> dots, string r) Preview(PatternKind kind)
    {
        if (previews.TryGetValue(kind, out var cached)) return cached;
        var (a, b) = patternParams[kind];
        var mesh = Patterns.Create(kind, a, b, new ScaleLayout());
        double w = mesh.ScaleWidth, pad = w * 0.6;
        double minX = mesh.Scales.Min(s => s.Center.X) - pad, maxX = mesh.Scales.Max(s => s.Center.X) + pad;
        double minY = mesh.Scales.Min(s => s.Center.Y) - pad, maxY = mesh.Scales.Max(s => s.Center.Y) + pad;
        var result = ($"{F(minX)} {F(minY)} {F(maxX - minX)} {F(maxY - minY)}",
            mesh.Scales.Select(s => (F(s.Center.X), F(s.Center.Y))).ToList(), F(w * 0.36));
        previews[kind] = result;
        return result;
    }

    private void PlaceAtCenter(PatternKind kind) => AddPatternAt(kind, new Pt(cx, cy));

    private void AddPatternAt(PatternKind kind, Pt world)
    {
        Snapshot();
        var (a, b) = patternParams[kind];
        var sec = LayoutOps.AddPattern(doc, kind, a, b, world);
        selectedSection = sec.Id; mode = Mode.Select; tri = null;
    }

    private async Task OnDrop(DragEventArgs e)
    {
        if (dragKind is not PatternKind kind || step != 2) return;
        dragKind = null;
        AddPatternAt(kind, await ToWorld(e.ClientX, e.ClientY));
    }

    // ---- section actions -----------------------------------------------------------------

    private void Rotate(double deg)
    {
        if (SelectedSection is not { } s) return;
        Snapshot();
        s.Rotation = ((s.Rotation + deg) % 360 + 360) % 360;
        tri = null;
    }

    private void DuplicateSection()
    {
        if (SelectedSection is not { } s) return;
        Snapshot();
        var b = s.LocalBounds(doc.ScaleWidth, doc.ScaleLength);
        var copy = LayoutOps.Duplicate(doc, s.Id, b.maxX - b.minX + doc.ScaleWidth, 0);
        if (imports.TryGetValue(s.Id, out var imp)) imports[copy.Id] = imp;
        selectedSection = copy.Id; tri = null;
    }

    private void DeleteSection()
    {
        if (SelectedSection is not { } s) return;
        Snapshot();
        LayoutOps.RemoveSection(doc, s.Id);
        selectedSection = null; tri = null;
    }

    // ---- pointer handling ----------------------------------------------------------------

    private bool panning, panMoved;
    private double lastX, lastY, panDistance;
    private int? movingSection;
    private (Section sec, Scale scale)? movingScale;
    private bool? paintValue;

    private void OnBackgroundDown(PointerEventArgs e)
    {
        if ((e.Buttons & 1) == 0) return;
        panning = true; panMoved = false; panDistance = 0; lastX = e.ClientX; lastY = e.ClientY;
    }

    private async Task OnBackgroundClick(MouseEventArgs e)
    {
        if (panMoved) return;
        if (mode == Mode.Select) { selectedSection = null; return; }
        if (mode == Mode.Edit && editTool == EditTool.Add && EditingSection is { } sec)
        {
            var local = sec.ToLocal(await ToWorld(e.ClientX, e.ClientY));
            double off = TipOffset(sec);
            var free = new Pt(local.X, local.Y + off); // the click marks the scale's visible centre
            var near = Slots(sec).Where(sl => Math.Abs(sl.X - free.X) < doc.ScaleWidth * 0.5 && Math.Abs(sl.Y - free.Y) < doc.RowPitch * 0.6)
                .OrderBy(sl => Pt.Distance(sl, free)).Cast<Pt?>().FirstOrDefault();
            AddScaleAt(sec, near ?? free);
        }
    }

    private void OnPointerMove(PointerEventArgs e)
    {
        if ((e.Buttons & 1) == 0) return;
        double dx = (e.ClientX - lastX) / k, dy = (e.ClientY - lastY) / k;
        if (panning)
        {
            cx -= dx; cy -= dy;
            panDistance += Math.Abs(e.ClientX - lastX) + Math.Abs(e.ClientY - lastY);
            if (panDistance > 4) panMoved = true;
        }
        else if (movingSection is int id && doc.FindSection(id) is { } sec)
        {
            CommitPendingSnapshot();
            sec.X += dx; sec.Y += dy; tri = null;
        }
        else if (movingScale is { } ms)
        {
            CommitPendingSnapshot();
            var d = ms.sec.VectorToLocal(dx, dy);
            MeshTopology.Move(ms.scale, d.X, d.Y);
            ms.sec.Touch(); tri = null;
        }
        lastX = e.ClientX; lastY = e.ClientY;
    }

    private void OnPointerUp()
    {
        panning = false; paintValue = null; pendingSnapshot = null;
        movingSection = null;
        if (movingScale is { } ms)
        {
            movingScale = null;
            MeshTopology.Rebuild(ms.sec.Mesh);
            ms.sec.Touch();
        }
        RefreshCandidates();
    }

    private Task OnScaleDown((Section sec, Scale sc, MouseEventArgs e) a)
    {
        var (sec, sc, e) = a;
        if ((e.Buttons & 1) == 0) return Task.CompletedTask;
        lastX = e.ClientX; lastY = e.ClientY;
        switch (mode)
        {
            case Mode.Select:
                selectedSection = sec.Id;
                movingSection = sec.Id; pendingSnapshot = ProjectFile.SaveLayout(doc);
                break;
            case Mode.Edit when sec.Id == editSection:
                if (editTool == EditTool.Paint)
                {
                    Snapshot(); paintValue = !sc.Colored; sc.Colored = paintValue.Value; sec.Touch(); tri = null;
                }
                else if (editTool == EditTool.Move)
                {
                    movingScale = (sec, sc); pendingSnapshot = ProjectFile.SaveLayout(doc);
                }
                break;
        }
        return Task.CompletedTask;
    }

    private Task OnScaleEnter((Section sec, Scale sc, MouseEventArgs e) a)
    {
        var (sec, sc, e) = a;
        if ((e.Buttons & 1) == 0) { paintValue = null; return Task.CompletedTask; }
        if (mode == Mode.Edit && sec.Id == editSection && editTool == EditTool.Paint && paintValue is bool v && sc.Colored != v)
        {
            sc.Colored = v; sec.Touch(); tri = null;
        }
        return Task.CompletedTask;
    }

    private Task OnScaleClick((Section sec, Scale sc, MouseEventArgs e) a)
    {
        var (sec, sc, _) = a;
        switch (mode)
        {
            case Mode.Select:
                selectedSection = sec.Id;
                break;
            case Mode.Edit when sec.Id == editSection:
                if (editTool == EditTool.Select) selectedScale = sc.Id;
                else if (editTool == EditTool.Delete)
                {
                    Snapshot();
                    LayoutOps.RemoveScale(doc, sec.Id, sc.Id);
                    selectedScale = null; tri = null;
                }
                break;
            case Mode.Link:
                LinkClick(sec, sc);
                break;
        }
        return Task.CompletedTask;
    }

    // ---- scale editing -------------------------------------------------------------------

    private readonly Dictionary<int, (int rev, double off, List<Pt> slots)> editCache = new();

    private (double off, List<Pt> slots) EditData(Section s)
    {
        if (editCache.TryGetValue(s.Id, out var c) && c.rev == s.Revision) return (c.off, c.slots);
        var data = (s.Revision, MeshTopology.TipOffset(s.Mesh), MeshTopology.FreeSlots(s.Mesh));
        editCache[s.Id] = data;
        return (data.Item2, data.Item3);
    }

    private double TipOffset(Section s) => EditData(s).off;
    private List<Pt> Slots(Section s) => EditData(s).slots;

    private void AddScaleAt(Section sec, Pt slot)
    {
        Snapshot();
        var added = MeshTopology.AddScale(sec.Mesh, slot.X, slot.Y);
        sec.Touch(); selectedScale = added.Id; tri = null;
    }

    private void InvertColors()
    {
        if (EditingSection is not { } s) return;
        Snapshot();
        foreach (var sc in s.Mesh.Scales) sc.Colored = !sc.Colored;
        s.Touch(); tri = null;
    }

    // ---- linking with rings --------------------------------------------------------------

    private int? linkA, linkB;
    private ScaleRef? pendingScale;
    private List<RingCandidate> candidates = new();

    private int LinkStage => linkA is null ? 1 : linkB is null ? 2 : 3;

    private void LinkClick(Section sec, Scale sc)
    {
        switch (LinkStage)
        {
            case 1:
                linkA = sec.Id; selectedSection = sec.Id;
                break;
            case 2:
                linkB = sec.Id; RefreshCandidates();
                break;
            default:
                if (sec.Id != linkA && sec.Id != linkB) return;
                var r = new ScaleRef { Section = sec.Id, Scale = sc.Id };
                if (pendingScale is null) { pendingScale = r; return; }
                var first = pendingScale; pendingScale = null;
                if (first.Same(r)) return;
                var mid = doc.ScaleWorldCenter(first); var other = doc.ScaleWorldCenter(r);
                Snapshot();
                LayoutOps.ToggleRing(doc, first, r, new Pt((mid.X + other.X) / 2, (mid.Y + other.Y) / 2));
                tri = null; RefreshCandidates();
                break;
        }
    }

    private void RefreshCandidates() =>
        candidates = mode == Mode.Link && linkA is int a && linkB is int b ? LayoutOps.RingCandidates(doc, a, b) : new();

    private void AddRingAt(RingCandidate c)
    {
        Snapshot();
        LayoutOps.ToggleRing(doc, c.A, c.B, c.World);
        tri = null; RefreshCandidates();
    }

    private void OnRingClick(Ring r)
    {
        if (mode != Mode.Link) return;
        Snapshot();
        doc.Rings.Remove(r);
        tri = null; RefreshCandidates();
    }

    private int RingsBetween() => linkA is int a && linkB is int b
        ? doc.Rings.Count(r => (r.A.Section == a && r.B.Section == b) || (r.A.Section == b && r.B.Section == a))
        : 0;

    private void FillAllGaps()
    {
        if (linkA is not int a || linkB is not int b) return;
        Snapshot();
        LayoutOps.AutoRings(doc, a, b);
        tri = null; RefreshCandidates();
    }

    private void ClearRings()
    {
        if (linkA is not int a || linkB is not int b) return;
        Snapshot();
        LayoutOps.RemoveRingsBetween(doc, a, b);
        tri = null; RefreshCandidates();
    }

    private void ResetLink()
    {
        linkA = linkB = null; pendingScale = null; candidates = new();
    }

    // ---- 3D / export ---------------------------------------------------------------------

    private async Task Build3D()
    {
        try
        {
            error = null; busy = true;
            await Task.Yield();
            tri = LayoutBuilder.Build(doc, b3);
            previewDirty = true;
        }
        catch (Exception ex) { error = "3D build failed: " + ex.Message; }
        finally { busy = false; }
    }

    private async Task SetCamera(double yaw, double pitch) =>
        await JS.InvokeVoidAsync("scalemail.setCamera", yaw, pitch);

    private string SizeText()
    {
        var (min, max) = tri!.Bounds();
        var d = max - min;
        return $"{d.X:0} × {d.Y:0} × {d.Z:0} mm";
    }

    private async Task ExportStl(bool? colored, string name)
    {
        if (tri is null) return;
        var m = tri;
        var bytes = MeshExporter.ToStl(m, colored is null ? null : id => m.ScaleColored.GetValueOrDefault(id) == colored);
        await JS.InvokeVoidAsync("scalemail.download", name + ".stl", bytes, "model/stl");
    }

    private async Task ExportObj()
    {
        if (tri is null) return;
        await JS.InvokeVoidAsync("scalemail.download", "scalemail.obj", System.Text.Encoding.UTF8.GetBytes(MeshExporter.ToObj(tri)), "text/plain");
        await JS.InvokeVoidAsync("scalemail.download", "scalemail.mtl", System.Text.Encoding.UTF8.GetBytes(MeshExporter.ObjMaterials()), "text/plain");
    }

    private async Task SaveProject() =>
        await JS.InvokeVoidAsync("scalemail.download", "scalemail-project.json", System.Text.Encoding.UTF8.GetBytes(ProjectFile.SaveLayout(doc)), "application/json");
}
