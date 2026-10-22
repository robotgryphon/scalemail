using Microsoft.Playwright;

namespace ScaleMail.IntegrationTests;

[TestFixture]
public class ToolboxToThreeDTests
{
    // Pattern names as shown in the toolbox, in order.
    private static readonly string[] Shapes =
        { "Sheet", "Row", "Triangle", "Inverted triangle", "Trapezoid", "Diamond", "Single scale" };

    private AppServer server = null!;
    private IPlaywright playwright = null!;
    private IBrowser browser = null!;
    private string shots = null!;

    [OneTimeSetUp]
    public async Task StartEverything()
    {
        shots = Path.Combine(AppServer.RepoRoot(), "tests", "ScaleMail.IntegrationTests", "screenshots");
        Directory.CreateDirectory(shots);

        server = new AppServer();
        await server.StartAsync();

        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            // WebGL in headless Chromium goes through the software renderer.
            Args = new[] { "--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist" },
        });
    }

    [OneTimeTearDown]
    public async Task StopEverything()
    {
        if (browser is not null) await browser.CloseAsync();
        playwright?.Dispose();
        server?.Dispose();
    }

    [Test]
    public async Task EveryToolboxShape_CanBeDraggedOut_AndBuiltInto3D()
    {
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1600, Height = 1000 },
        });
        var page = await context.NewPageAsync();
        var consoleErrors = new List<string>();
        page.Console += (_, m) => { if (m.Type == "error") consoleErrors.Add(m.Text); };
        page.PageError += (_, e) => consoleErrors.Add(e);

        // 1. Open the app and start from scratch.
        await page.GotoAsync(server.Url);
        await page.GetByRole(AriaRole.Button, new() { Name = "Open the layout canvas" }).ClickAsync(new() { Timeout = 90_000 });
        await Expect(page.Locator(".tool-card")).ToHaveCountAsync(Shapes.Length);

        // 2. Drag every shape out of the toolbox onto its own spot on the canvas.
        var stage = page.Locator(".stage");
        var box = (await stage.BoundingBoxAsync())!;
        for (int i = 0; i < Shapes.Length; i++)
        {
            // Four shapes on the top row, three below.
            int col = i < 4 ? i : i - 4, perRow = i < 4 ? 4 : 3;
            float x = box.Width * (col + 0.5f) / perRow;
            float y = box.Height * (i < 4 ? 0.28f : 0.7f);
            await page.Locator(".tool-card").Filter(new() { Has = page.GetByText(Shapes[i], new() { Exact = true }) })
                      .DragToAsync(stage, new() { TargetPosition = new TargetPosition { X = x, Y = y } });
        }

        var sections = page.Locator("svg.canvas g[transform]").Filter(new() { Has = page.Locator("path.scale-path") });
        await Expect(sections).ToHaveCountAsync(Shapes.Length);
        var scaleCount = await page.Locator("svg.canvas path.scale-path").CountAsync();
        Assert.That(scaleCount, Is.EqualTo(30 + 6 + 15 + 15 + 25 + 25 + 1), "scales from all seven default patterns");
        Assert.That(await page.Locator("svg.canvas .ring").CountAsync(), Is.Zero, "the meshes are not linked");

        // Deselect and fit everything into view for the screenshot.
        await page.GetByRole(AriaRole.Button, new() { Name = "Fit" }).ClickAsync();
        await page.Mouse.ClickAsync(box.X + 4, box.Y + 4);
        await page.WaitForTimeoutAsync(400);
        await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "1-layout-all-shapes.png") });

        // 3. Convert to 3D.
        await page.GetByRole(AriaRole.Button, new() { Name = "Continue: build 3D" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Build 3D mesh" }).ClickAsync();
        await Expect(page.Locator(".stat")).ToContainTextAsync("triangles", new() { Timeout = 60_000 });
        await page.WaitForSelectorAsync("canvas");
        await page.WaitForTimeoutAsync(800);
        await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "2-3d-default-view.png") });

        // 4. Side profile.
        await page.GetByRole(AriaRole.Button, new() { Name = "Side", Exact = true }).ClickAsync();
        await page.WaitForTimeoutAsync(800);
        await page.ScreenshotAsync(new() { Path = Path.Combine(shots, "3-3d-side-profile.png") });

        Assert.That(consoleErrors, Is.Empty, string.Join("\n", consoleErrors));
    }

    private static ILocatorAssertions Expect(ILocator locator) => Assertions.Expect(locator);
}
