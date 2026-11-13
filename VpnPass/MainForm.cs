using System.Diagnostics;
using System.Net;

namespace VpnPass;

internal sealed class MainForm : Form
{
    private readonly string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vpnPass");
    private string SettingsPath => Path.Combine(dataDirectory, "profiles.json");
    private string LogPath => Path.Combine(dataDirectory, "activity.log");
    private Settings settings;
    private readonly HashSet<string> enabled = [];
    private readonly RouteClient routes = new();
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Shield, Text = "vpnPass — off", Visible = true };
    private readonly System.Windows.Forms.Timer refreshTimer = new() { Interval = 60000 };
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly FlowLayoutPanel ruleCards = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    private readonly ComboBox network = new() { Width = 360, DropDownWidth = 440, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = "Starting…", Font = new Font("Segoe UI", 11, FontStyle.Bold) };
    private readonly TextBox activity = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
    private readonly Button add = new() { Text = "+ Add rule" };
    private readonly TableLayoutPanel details = new() { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Visible = false };
    private readonly Button showDetails = new() { Text = "Connection details" };
    private readonly Button refresh = new() { Text = "Refresh now", Width = 120, Height = 32 };
    private readonly Button allOff = new() { Text = "Turn all off", Width = 115, Height = 32 };
    private readonly Button allOn = new() { Text = "Turn all on", Width = 115, Height = 32 };
    private readonly bool preview;
    private bool busy, exiting, allowClose, fillingNetwork;
    private string lastWarnings = "";

    public MainForm(bool preview)
    {
        this.preview = preview;
        Directory.CreateDirectory(dataDirectory);
        try { settings = SettingsStore.Load(SettingsPath); }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Could not load {SettingsPath}. Fix or rename that file, then reopen vpnPass. It has not been overwritten.\n\n{ex.Message}", ex);
        }
        if (!File.Exists(SettingsPath) && !preview) SettingsStore.Save(SettingsPath, settings);
        Text = preview ? "vpnPass — preview (no route changes)" : "vpnPass";
        Icon = SystemIcons.Shield;
        Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(247, 249, 252);
        ClientSize = new Size(920, 640);
        MinimumSize = new Size(860, 620);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        BuildLayout();
        FillProfiles();
        RebuildTray();
        tray.DoubleClick += (_, _) => ShowWindow();
        refreshTimer.Tick += async (_, _) =>
        {
            if (!busy && !exiting && (enabled.Count > 0 || routes.OwnedCount > 0))
                await RefreshRoutesAsync(background: true);
        };
        Shown += async (_, _) => { await RefreshRoutesAsync(reason: "Startup"); if (!preview) refreshTimer.Start(); };
        FormClosing += OnClosing;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) Hide(); };
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 20), RowCount = 7, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 7; row++)
            layout.RowStyles.Add(row == 4 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "vpnPass", Font = new Font("Segoe UI", 10, FontStyle.Regular), AutoSize = true, ForeColor = Color.FromArgb(110, 120, 132) }, 0, 0);
        layout.Controls.Add(new Label { Text = "Route selected destinations through your home connection.", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 4, 3, 8) }, 0, 1);
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 8, 0, 10) };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        heading.Controls.Add(new Label { Text = "Routing rules", Font = new Font("Segoe UI", 14, FontStyle.Bold), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        heading.Controls.Add(add, 1, 0);
        layout.Controls.Add(heading, 0, 2);
        layout.Controls.Add(new Label { Text = "On: home connection    •    Off: normal routing", AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 0, 3, 12) }, 0, 3);
        ruleCards.SizeChanged += (_, _) => ResizeCards();
        layout.Controls.Add(ruleCards, 0, 4);

        var networkRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoSize = true, Padding = new Padding(0, 8, 0, 8) };
        networkRow.Controls.Add(new Label { Text = "Home connection", AutoSize = true, Margin = new Padding(0, 7, 12, 0) });
        networkRow.Controls.Add(network);
        networkRow.Controls.Add(refresh);
        status.AutoSize = true;
        status.Margin = new Padding(3, 10, 3, 10);
        layout.Controls.Add(status, 0, 5);
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        details.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        details.Controls.Add(networkRow, 0, 0);
        details.Controls.Add(new Label { Text = "Recent activity", AutoSize = true, ForeColor = Color.DimGray }, 0, 1);
        details.Controls.Add(activity, 0, 2);
        var folder = new Button { Text = "Open settings folder", Anchor = AnchorStyles.Left };
        details.Controls.Add(folder, 0, 3);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        var exit = new Button { Text = "Exit", Width = 80, Height = 32 };
        var hide = new Button { Text = "Hide to tray", Width = 120, Height = 32 };
        footer.Controls.AddRange([exit, hide, allOff, allOn, showDetails]);
        foreach (var button in new[] { add, allOn, allOff, refresh, exit, hide, folder, showDetails }) StyleButton(button);
        layout.Controls.Add(footer, 0, 6);
        Controls.Add(layout);
        add.Click += async (_, _) => await EditProfileAsync();
        showDetails.Click += (_, _) => ShowConnectionDetails();
        allOn.Click += async (_, _) => await SetAllRulesAsync(true);
        allOff.Click += async (_, _) => await SetAllRulesAsync(false);
        refresh.Click += async (_, _) => await RefreshRoutesAsync();
        exit.Click += async (_, _) => await ExitAsync();
        hide.Click += (_, _) => Hide();
        folder.Click += (_, _) => Process.Start(new ProcessStartInfo(dataDirectory) { UseShellExecute = true });
        network.SelectedIndexChanged += async (_, _) =>
        {
            if (fillingNetwork || busy || exiting) return;
            var selected = (HomeAdapter?)network.SelectedItem;
            var previous = settings.AdapterId;
            settings.AdapterId = selected?.Id ?? "";
            if (!SaveSettings()) { settings.AdapterId = previous; return; }
            await RefreshRoutesAsync(reason: "Home connection changed");
        };
    }

    private static void StyleButton(Button button)
    {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(76, 34);
        button.Padding = new Padding(10, 4, 10, 4);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(212, 220, 228);
        button.BackColor = Color.White;
        button.ForeColor = Color.FromArgb(23, 54, 82);
        button.Cursor = Cursors.Hand;
    }

    private void ShowConnectionDetails()
    {
        using var dialog = new Form { Text = "Connection details", Name = "ConnectionDetails", Font = Font,
            ClientSize = new Size(860, 430), MinimumSize = new Size(820, 380), StartPosition = FormStartPosition.CenterParent,
            BackColor = BackColor, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false, AutoScaleMode = AutoScaleMode.Dpi };
        if (preview) dialog.Opacity = 0;
        details.AutoSize = false;
        details.Padding = new Padding(18);
        details.Visible = true;
        dialog.Controls.Add(details);
        try { dialog.ShowDialog(this); }
        finally { dialog.Controls.Remove(details); details.Visible = false; }
    }

    private void FillProfiles()
    {
        ruleCards.SuspendLayout();
        foreach (Control old in ruleCards.Controls.Cast<Control>().ToArray()) old.Dispose();
        foreach (var profile in settings.Profiles)
        {
            var isOn = enabled.Contains(profile.Id);
            var card = new TableLayoutPanel { AutoSize = true, BackColor = Color.White, ColumnCount = 4, RowCount = 2,
                Padding = new Padding(18, 14, 18, 14), Margin = new Padding(0, 0, 0, 12), Tag = profile.Id };
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            card.Controls.Add(new Label { Text = profile.Name, Font = new Font("Segoe UI", 13, FontStyle.Bold), AutoSize = true,
                ForeColor = Color.FromArgb(23, 54, 82), Anchor = AnchorStyles.Left, UseMnemonic = false }, 0, 0);
            card.Controls.Add(new Label { Text = profile.Targets.Count == 1 ? profile.Targets[0] : $"{profile.Targets.Count} destinations",
                ForeColor = Color.DimGray, AutoSize = true, Margin = new Padding(3, 6, 3, 0) }, 0, 1);
            var state = new Label { Text = isOn ? "On" : "Off", AutoSize = true, Anchor = AnchorStyles.None, ForeColor = Color.DimGray };
            card.Controls.Add(state, 1, 0);
            card.SetRowSpan(state, 2);
            var routeSwitch = new RouteSwitch { Checked = isOn, Anchor = AnchorStyles.None, BackColor = Color.White,
                AccessibleName = $"Enable home routing for {profile.Name}", Margin = new Padding(10, 3, 16, 3) };
            routeSwitch.CheckedChanged += async (_, _) => await ToggleAsync(profile.Id);
            card.Controls.Add(routeSwitch, 2, 0);
            card.SetRowSpan(routeSwitch, 2);
            var edit = new Button { Text = "Edit", Anchor = AnchorStyles.None, AccessibleName = $"Edit routing rule {profile.Name}" };
            StyleButton(edit);
            edit.Click += async (_, _) => await EditProfileAsync(profile.Id);
            card.Controls.Add(edit, 3, 0);
            card.SetRowSpan(edit, 2);
            card.Enabled = !busy && !exiting;
            ruleCards.Controls.Add(card);
        }
        if (settings.Profiles.Count == 0)
            ruleCards.Controls.Add(new Label { Text = "No routing rules yet. Choose Add rule to get started.", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(16) });
        ResizeCards();
        ruleCards.ResumeLayout();
        RebuildTray();
    }

    private void ResizeCards()
    {
        foreach (Control card in ruleCards.Controls)
        {
            var width = Math.Max(100, ruleCards.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
            card.MinimumSize = new Size(width, 0);
            card.MaximumSize = new Size(width, 0);
            card.Width = width;
        }
    }

    private async Task ToggleAsync(string id)
    {
        if (busy || exiting) return;
        if (!enabled.Add(id)) enabled.Remove(id);
        FillProfiles();
        var name = settings.Profiles.First(p => p.Id == id).Name;
        await RefreshRoutesAsync(reason: (enabled.Contains(id) ? "Enable " : "Disable ") + name);
    }

    private async Task SetAllRulesAsync(bool turnOn)
    {
        if (busy || exiting) return;
        enabled.Clear();
        if (turnOn) enabled.UnionWith(settings.Profiles.Select(p => p.Id));
        FillProfiles();
        await RefreshRoutesAsync(reason: turnOn ? "Enable all rules" : "Disable all rules");
    }

    private bool SaveSettings()
    {
        try { if (!preview) SettingsStore.Save(SettingsPath, settings); return true; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Could not save routing rules", MessageBoxButtons.OK, MessageBoxIcon.Error); return false; }
    }

    private async Task EditProfileAsync(string? id = null)
    {
        if (busy || exiting) return;
        var original = id is null ? null : settings.Profiles.FirstOrDefault(p => p.Id == id);
        if (id is not null && original is null) return;
        using var dialog = new ProfileDialog(original);
        var dialogResult = dialog.ShowDialog(this);
        if (dialogResult == DialogResult.Abort && original is not null) { await RemoveProfileAsync(original.Id); return; }
        if (dialogResult != DialogResult.OK) return;
        var before = settings.Profiles.ToList();
        if (original is null) settings.Profiles.Add(dialog.Result);
        else settings.Profiles[settings.Profiles.IndexOf(original)] = dialog.Result;
        if (!SaveSettings()) { settings.Profiles = before; return; }
        FillProfiles();
        await RefreshRoutesAsync(reason: "Save rule " + dialog.Result.Name);
    }

    private async Task RemoveProfileAsync(string id)
    {
        if (busy || exiting) return;
        var profile = settings.Profiles.First(p => p.Id == id);
        var before = settings.Profiles.ToList();
        settings.Profiles.Remove(profile);
        if (!SaveSettings()) { settings.Profiles = before; return; }
        enabled.Remove(profile.Id);
        FillProfiles();
        await RefreshRoutesAsync(reason: "Remove rule " + profile.Name);
    }

    private void SetBusy(bool value)
    {
        busy = value;
        add.Enabled = network.Enabled = refresh.Enabled = !value && !exiting;
        allOn.Enabled = allOff.Enabled = !value && !exiting && settings.Profiles.Count > 0;
        foreach (Control card in ruleCards.Controls) card.Enabled = !value && !exiting;
        RebuildTray();
    }

    private async Task RefreshRoutesAsync(bool background = false, string reason = "Manual refresh")
    {
        if (exiting) return;
        var totalTime = Stopwatch.StartNew();
        if (background)
        {
            if (!await operation.WaitAsync(0)) return;
        }
        else
        {
            // A click during an automatic refresh waits its turn; it must not be lost.
            SetBusy(true);
            status.Text = "Updating…";
            await operation.WaitAsync();
        }
        var queueTime = totalTime.Elapsed;
        try
        {
            if (exiting) return;
            if (preview)
            {
                status.Text = "Preview — no network changes";
                FillNetworks([]);
                Log("Preview mode. Rules start off. Close or minimize to hide to the tray; Exit closes the app.");
                return;
            }
            // Capture the requested state before awaiting DNS. User changes can queue
            // while a background refresh is running and will be applied next.
            var targets = settings.Profiles.Where(p => enabled.Contains(p.Id)).SelectMany(p => p.Targets).ToArray();
            var adapterId = settings.AdapterId;
            var phaseTime = Stopwatch.StartNew();
            var resolution = await Resolver.ResolveAsync(targets, host => Dns.GetHostAddressesAsync(host));
            var dnsTime = phaseTime.Elapsed;
            if (resolution.Addresses.Length > 256) throw new IOException("More than 256 addresses resolved. Reduce the enabled rules.");
            phaseTime.Restart();
            var result = await routes.SyncAsync(adapterId, resolution.Addresses);
            var routeTime = phaseTime.Elapsed;
            if (!background)
                Log($"{reason}: total {totalTime.Elapsed.TotalSeconds:F2}s; waiting {queueTime.TotalSeconds:F2}s; DNS {dnsTime.TotalSeconds:F2}s; Windows routes {routeTime.TotalSeconds:F2}s; {result.Owned.Length} routes; {result.Warnings.Length + resolution.Warnings.Length} warnings.");
            // Do not overwrite the status or network selection of a queued user action.
            if (exiting || (background && busy)) return;
            if (!network.DroppedDown) FillNetworks(result.Adapters);
            var warnings = resolution.Warnings.Concat(result.Warnings).ToArray();
            status.ForeColor = warnings.Length == 0 ? Color.FromArgb(0, 104, 91) : Color.FromArgb(145, 76, 0);
            status.Text = warnings.Length > 0 ? "Some destinations need attention — open details" :
                enabled.Count == 0 && result.Owned.Length == 0 ? "All rules off • normal routing" :
                $"Home routing enabled • {result.NetworkName}";
            tray.Text = warnings.Length > 0 ? "vpnPass — needs attention" : enabled.Count == 0 ? "vpnPass — off" : $"vpnPass — {result.Owned.Length} routes active";
            var warningText = string.Join(Environment.NewLine, warnings);
            if (warningText != lastWarnings || activity.TextLength == 0 || (!background && warnings.Length > 0))
            {
                Log(warnings.Length > 0 ? warningText : enabled.Count == 0 ? "All rules are off. Enable a switch to use home routing for its destinations." : $"{result.Owned.Length} routes refreshed. Existing connections may need the program to restart.");
                lastWarnings = warningText;
            }
        }
        catch (Exception ex)
        {
            Log($"{(background ? "Background refresh" : reason)} failed after {totalTime.Elapsed.TotalSeconds:F2}s: {ex.Message} Unrenewed routes expire within five minutes.");
            if (exiting || (background && busy)) return;
            status.ForeColor = Color.Firebrick;
            status.Text = "Update failed — see recent activity";
            tray.Text = "vpnPass — update failed";
        }
        finally
        {
            if (!background) SetBusy(false);
            operation.Release();
        }
    }

    private void FillNetworks(HomeAdapter[] adapters)
    {
        fillingNetwork = true;
        try
        {
            network.Items.Clear();
            network.Items.Add(new HomeAdapter { Name = "Automatic", Id = "" });
            foreach (var adapter in adapters) network.Items.Add(adapter);
            if (settings.AdapterId.Length > 0 && !adapters.Any(a => a.Id == settings.AdapterId))
                network.Items.Add(new HomeAdapter { Name = "Selected connection (unavailable)", Id = settings.AdapterId });
            network.SelectedItem = network.Items.Cast<HomeAdapter>().First(a => a.Id == settings.AdapterId);
        }
        finally { fillingNetwork = false; }
    }

    private void RebuildTray()
    {
        var old = tray.ContextMenuStrip;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open vpnPass", null, (_, _) => ShowWindow());
        menu.Items.Add(new ToolStripSeparator());
        foreach (var profile in settings.Profiles)
        {
            var item = new ToolStripMenuItem(profile.Name.Replace("&", "&&")) { Checked = enabled.Contains(profile.Id), Enabled = !busy && !exiting && !preview };
            item.Click += async (_, _) => await ToggleAsync(profile.Id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        var on = new ToolStripMenuItem("Turn all on") { Enabled = !busy && !exiting && !preview && settings.Profiles.Count > 0 };
        on.Click += async (_, _) => await SetAllRulesAsync(true);
        menu.Items.Add(on);
        var off = new ToolStripMenuItem("Turn all off") { Enabled = !busy && !exiting && !preview && settings.Profiles.Count > 0 };
        off.Click += async (_, _) => await SetAllRulesAsync(false);
        menu.Items.Add(off);
        menu.Items.Add("Exit and remove routes", null, async (_, _) => await ExitAsync());
        tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    private void Log(string message)
    {
        var text = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        if (activity.TextLength > 24000) activity.Clear();
        activity.AppendText(text);
        if (preview) return;
        try
        {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 512000) File.Move(LogPath, LogPath + ".previous", overwrite: true);
            File.AppendAllText(LogPath, text);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ShowWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (allowClose) return;
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            // Windows may not wait for async cleanup; finite route lifetimes remain the fallback.
            refreshTimer.Stop();
            tray.Visible = false;
            return;
        }
        e.Cancel = true;
        if (!exiting) Hide();
        await Task.CompletedTask;
    }

    private async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        refreshTimer.Stop();
        ShowWindow();
        SetBusy(true);
        status.Text = "Removing routes…";
        await operation.WaitAsync();
        try
        {
            if (!preview)
            {
                var result = await routes.SyncAsync(settings.AdapterId, []);
                if (result.Owned.Length > 0) throw new IOException(string.Join(Environment.NewLine, result.Warnings));
            }
        }
        catch (Exception ex)
        {
            Log(ex.Message);
            MessageBox.Show(this, "Some routes could not be removed immediately. They expire within five minutes.\n\n" + ex.Message,
                "vpnPass cleanup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally
        {
            operation.Release();
            allowClose = true;
            tray.Visible = false;
            Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { refreshTimer.Dispose(); tray.Dispose(); routes.Dispose(); operation.Dispose(); details.Dispose(); }
        base.Dispose(disposing);
    }
}
