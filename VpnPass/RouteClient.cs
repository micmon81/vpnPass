using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace VpnPass;

public sealed class OwnedRoute
{
    public string DestinationPrefix { get; set; } = "";
    public int InterfaceIndex { get; set; }
    public string NextHop { get; set; } = "";
    public int RouteMetric { get; set; } = 37;
}
public sealed class HomeAdapter
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Index { get; set; }
    public string Gateway4 { get; set; } = "";
    public string Gateway6 { get; set; } = "";
    public override string ToString() => Name;
}
public sealed class RouteResult
{
    public OwnedRoute[] Owned { get; set; } = [];
    public string[] Warnings { get; set; } = [];
    public HomeAdapter[] Adapters { get; set; } = [];
    public string NetworkName { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class RouteClient : IDisposable
{
    private readonly string helperPath;
    private OwnedRoute[] owned = [];
    public int OwnedCount => owned.Length;

    public RouteClient()
    {
        helperPath = Path.Combine(Path.GetTempPath(), $"vpnPass-{Guid.NewGuid():N}.ps1");
        var assembly = Assembly.GetExecutingAssembly();
        using var source = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(".Routes.ps1")))!;
        using var destination = new FileStream(helperPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(destination);
    }

    // Caller serializes updates and cleanup; arguments are JSON data, never shell code.
    public async Task<RouteResult> SyncAsync(string adapterId, string[] addresses)
    {
        var payload = JsonSerializer.Serialize(new { AdapterId = adapterId, Addresses = addresses, Owned = owned });
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helperPath,
            "-RequestBase64", Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start Windows route helper.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new IOException("Windows route update timed out. Any untracked routes expire within five minutes.");
        }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        RouteResult result;
        try { result = JsonSerializer.Deserialize<RouteResult>(stdout, SettingsStore.JsonOptions) ?? throw new JsonException(); }
        catch (JsonException) { throw new IOException("Windows route helper returned an invalid response. " + stderr.Trim()); }
        if (!string.IsNullOrEmpty(result.Error)) throw new IOException(result.Error);
        if (process.ExitCode != 0) throw new IOException("Windows route helper failed. " + stderr.Trim());
        owned = result.Owned;
        return result;
    }

    public void Dispose()
    {
        try { File.Delete(helperPath); } catch (IOException) { }
    }
}
