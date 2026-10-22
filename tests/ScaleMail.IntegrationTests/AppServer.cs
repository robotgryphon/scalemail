using System.Diagnostics;

namespace ScaleMail.IntegrationTests;

/// <summary>Runs the Blazor app with the dev server for the duration of the test run.</summary>
public sealed class AppServer : IDisposable
{
    private Process? process;
    public string Url { get; }

    public AppServer(int port = 5201) => Url = $"http://localhost:{port}";

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.GetFiles(dir.FullName, "ScaleMail.sln*").Any()) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the ScaleMail solution file above " + AppContext.BaseDirectory);
    }

    public async Task StartAsync()
    {
        var project = Path.Combine(RepoRoot(), "src", "ScaleMail.Web");
        var psi = new ProcessStartInfo("dotnet", $"run --project \"{project}\" --no-launch-profile --urls {Url}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the app.");

        var ready = new TaskCompletionSource();
        process.OutputDataReceived += (_, e) => { if (e.Data?.Contains("Now listening") == true) ready.TrySetResult(); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) TestContext.Progress.WriteLine("[app] " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var finished = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromMinutes(2)));
        if (finished != ready.Task) throw new TimeoutException("The app did not start listening within 2 minutes.");
    }

    public void Dispose()
    {
        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        process?.Dispose();
    }
}
