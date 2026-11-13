using System.Net;
using System.Text.Json;
using VpnPass;

internal static class Program
{
    private static int passed;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Run("Normalizes and deduplicates domains", () => Equal("chatgpt.com", TargetRules.ParseLines(" ChatGPT.COM.\nchatgpt.com").Single()));
            Run("Accepts IPv4 and IPv6", () => Equal(2, TargetRules.ParseLines("104.18.32.47\n2606:4700:4408::ac40:9bd1").Count));
            Run("Normalizes international names", () => Equal("xn--bcher-kva.de", TargetRules.Normalize("bücher.de")));
            foreach (var value in new[] { "https://chatgpt.com", "*.example.com", "8.8.8.8/32", "localhost", "x.local", "x..com", "-bad.com", "test.com;whoami", "10.1.1.1", "192.168.1.254", "127.0.0.1", "169.254.1.1", "100.64.0.1", "224.0.0.1", "::1", "fe80::1", "fc00::1", "::ffff:8.8.8.8", "2001:db8::1" })
                Run("Rejects " + value, () => Throws(() => TargetRules.Normalize(value)));
            Run("Rejects empty profile", () => Throws(() => TargetRules.ParseLines("\r\n ")));
            Run("Rejects oversized profile", () => Throws(() => TargetRules.ParseLines(string.Join('\n', Enumerable.Range(0, 33).Select(i => $"a{i}.com")))));
            Run("Rejects duplicate IDs", () => { var s = new Settings(); s.Profiles.Add(s.Profiles[0]); Throws(() => TargetRules.Validate(s)); });
            Run("Profile persistence and corruption handling", () =>
            {
                var path = Path.Combine(Path.GetTempPath(), $"vpnPass-test-{Guid.NewGuid():N}", "profiles.json");
                try
                {
                    var settings = new Settings();
                    settings.Profiles.Add(new Profile { Name = "Custom", Targets = ["example.com", "8.8.8.8"] });
                    SettingsStore.Save(path, settings);
                    Equal(2, SettingsStore.Load(path).Profiles.Count);
                    var json = File.ReadAllText(path);
                    if (json.Contains("Enabled", StringComparison.OrdinalIgnoreCase)) throw new Exception("Enabled state should not persist.");
                    File.WriteAllText(path, "broken");
                    Throws(() => SettingsStore.Load(path));
                    Equal("broken", File.ReadAllText(path));
                }
                finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
            });
            Run("Shared addresses resolved once and deduplicated", () =>
            {
                var lookups = 0;
                var result = Resolver.ResolveAsync(["chatgpt.com", "chatgpt.com", "example.com"], _ =>
                {
                    Interlocked.Increment(ref lookups);
                    return Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") });
                }).GetAwaiter().GetResult();
                Equal(2, lookups); Equal(1, result.Addresses.Length); Equal(0, result.Warnings.Length);
            });
            Run("DNS failures do not retain failed addresses", () =>
            {
                var result = Resolver.ResolveAsync(["example.com", "8.8.8.8"], _ => Task.FromException<IPAddress[]>(new IOException("DNS unavailable"))).GetAwaiter().GetResult();
                Equal("8.8.8.8", result.Addresses.Single()); Equal(1, result.Warnings.Length);
            });
            Run("Private DNS answers never become routes", () =>
            {
                var result = Resolver.ResolveAsync(["example.com"], _ => Task.FromResult(new[] { IPAddress.Parse("10.1.2.3"), IPAddress.Parse("127.0.0.1") })).GetAwaiter().GetResult();
                Equal(0, result.Addresses.Length); Equal(1, result.Warnings.Length);
            });
            Run("Empty profile selection resolves nothing", () =>
            {
                var result = Resolver.ResolveAsync([], _ => throw new Exception("Unexpected DNS lookup")).GetAwaiter().GetResult();
                Equal(0, result.Addresses.Length);
            });
            Run("DNS timeout is bounded", () =>
            {
                var never = new TaskCompletionSource<IPAddress[]>();
                var result = Resolver.ResolveAsync(["example.com"], _ => never.Task).GetAwaiter().GetResult();
                Equal(0, result.Addresses.Length); Equal(1, result.Warnings.Length);
            });
            if (args.Contains("--inspect-network"))
                Run("Real Windows helper read-only inspection", () =>
                {
                    using var client = new RouteClient();
                    var result = client.SyncAsync("", []).GetAwaiter().GetResult();
                    Equal(0, result.Owned.Length);
                    Console.WriteLine("Physical networks: " + string.Join(", ", result.Adapters.Select(a => a.Name)));
                });
            if (args.Contains("--render"))
                Run("Window layout preview", () =>
                {
                    Application.SetHighDpiMode(HighDpiMode.SystemAware);
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using var form = new MainForm(preview: true);
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    Application.DoEvents();
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                    var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts"));
                    Directory.CreateDirectory(directory);
                    bitmap.Save(Path.Combine(directory, "vpnPass-preview.png"));
                    // Simulate a route update in flight. A timer tick must stay quiet,
                    // and a user action must wait and subsequently complete, not vanish.
                    var privateFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                    var gate = (SemaphoreSlim)typeof(MainForm).GetField("operation", privateFlags)!.GetValue(form)!;
                    gate.Wait();
                    try
                    {
                        var backgroundTask = (Task)typeof(MainForm).GetMethod("RefreshRoutesAsync", privateFlags)!.Invoke(form, [true, "Test timer tick"])!;
                        Equal(true, backgroundTask.IsCompletedSuccessfully);
                        var onButton = Descendants(form).OfType<Button>().Single(b => b.Text == "Turn all on");
                        if (onButton.Enabled)
                        {
                            onButton.PerformClick();
                            Equal(false, onButton.Enabled);
                            Equal(true, Descendants(form).OfType<RouteSwitch>().All(s => s.Checked));
                        }
                    }
                    finally { gate.Release(); }
                    var wait = System.Diagnostics.Stopwatch.StartNew();
                    while (!Descendants(form).OfType<Button>().Single(b => b.Text == "+ Add rule").Enabled && wait.Elapsed.TotalSeconds < 3)
                    {
                        Application.DoEvents();
                        Thread.Sleep(1);
                    }
                    Equal(true, Descendants(form).OfType<Button>().Single(b => b.Text == "+ Add rule").Enabled);
                    Descendants(form).OfType<Button>().Single(b => b.Text == "Turn all off").PerformClick();
                    Equal(true, Descendants(form).OfType<RouteSwitch>().All(s => !s.Checked));
                    var firstSwitch = Descendants(form).OfType<RouteSwitch>().FirstOrDefault();
                    if (firstSwitch is not null)
                    {
                        typeof(CheckBox).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(firstSwitch, [EventArgs.Empty]);
                        Application.DoEvents();
                        Equal(true, Descendants(form).OfType<RouteSwitch>().First().Checked);
                        Equal(AccessibleRole.CheckButton, Descendants(form).OfType<RouteSwitch>().First().AccessibleRole);
                        using var onBitmap = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(onBitmap, new Rectangle(Point.Empty, form.Size));
                        onBitmap.Save(Path.Combine(directory, "vpnPass-switch-on.png"));
                        typeof(CheckBox).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(Descendants(form).OfType<RouteSwitch>().First(), [EventArgs.Empty]);
                        Application.DoEvents();
                        Equal(false, Descendants(form).OfType<RouteSwitch>().First().Checked);
                    }
                    using var captureTimer = new System.Windows.Forms.Timer { Interval = 100 };
                    captureTimer.Tick += (_, _) =>
                    {
                        var detailsForm = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Name == "ConnectionDetails");
                        if (detailsForm is null) return;
                        captureTimer.Stop();
                        using var detailsBitmap = new Bitmap(detailsForm.Width, detailsForm.Height);
                        detailsForm.DrawToBitmap(detailsBitmap, new Rectangle(Point.Empty, detailsForm.Size));
                        detailsBitmap.Save(Path.Combine(directory, "vpnPass-details-preview.png"));
                        detailsForm.Close();
                    };
                    captureTimer.Start();
                    Descendants(form).OfType<Button>().Single(b => b.Text == "Connection details").PerformClick();
                    using var editor = new ProfileDialog(new Profile { Name = "ChatGPT", Targets = ["chatgpt.com"] });
                    editor.ShowInTaskbar = false;
                    editor.Opacity = 0;
                    editor.Show();
                    Application.DoEvents();
                    using var editorBitmap = new Bitmap(editor.Width, editor.Height);
                    editor.DrawToBitmap(editorBitmap, new Rectangle(Point.Empty, editor.Size));
                    editorBitmap.Save(Path.Combine(directory, "profile-editor-preview.png"));
                    Console.WriteLine("Preview images: " + directory);
                });
            Console.WriteLine($"PASS: {passed} checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Run(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void Throws(Action action) { try { action(); } catch (Exception) { return; } throw new Exception("Expected rejection"); }
}
