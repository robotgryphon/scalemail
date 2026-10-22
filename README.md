# ScaleMail Designer

Blazor WebAssembly tool for designing scale mail the way it is made by hand: build sections, then join them with rings.

```
export DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec
dotnet run --project src/ScaleMail.Web      # open the printed URL
dotnet test                                  # core logic tests
```

## Workflow
1. **Import** (optional): a template image is detected into a section (dots over the reference image help you check it),
   or start from scratch. Projects save as JSON.
2. **Layout**: drag prebuilt patterns (sheet, row, triangle, inverted triangle, trapezoid, diamond, single scale) from the
   toolbox onto the canvas. Select/move/rotate/duplicate sections. *Edit scales* adds, deletes, moves and paints scales
   inside a section. *Link*: click a section, click the section to join it to (or the same one), then click a dashed
   spot in the gap to put a ring there, or click one scale on each side.
3. **Build 3D**: flat stacked plates per section; each ring cuts a hole in both scales and is modelled as a torus
   through them. Export STL / OBJ.

## Code
- `ScaleMail.Core/Model`: `Layout` (sections + rings), `Section` (mesh + canvas transform), `Patterns`, `LayoutOps`,
  `MeshTopology` (rows, neighbours, free slots).
- `ScaleMail.Core/Detection`: image to 2D mesh. `ScaleMail.Core/Mesh3D`: triangulation, plates, `LayoutBuilder`.
- Edge/zip helpers in `MeshTopology` are kept for seam pairing inside one section but are not in the UI; rings replace them.
