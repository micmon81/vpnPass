namespace VpnPass;

internal sealed class ProfileDialog : Form
{
    private readonly TextBox nameBox = new() { Dock = DockStyle.Fill, MaxLength = 60 };
    private readonly TextBox targetsBox = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly string profileId;
    public Profile Result { get; private set; } = new();

    public ProfileDialog(Profile? original = null)
    {
        profileId = original?.Id ?? Guid.NewGuid().ToString("N");
        Text = original is null ? "Add routing rule" : "Edit routing rule";
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(640, 520);
        MinimumSize = new Size(580, 490);
        BackColor = Color.FromArgb(247, 249, 252);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        nameBox.Text = original?.Name ?? "";
        targetsBox.Text = string.Join(Environment.NewLine, original?.Targets ?? []);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 7, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 7; row++)
            layout.RowStyles.Add(row == 3 ? new RowStyle(SizeType.Percent, 100) :
                row == 5 ? new RowStyle(SizeType.Absolute, 8) : new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = "Rule name", AutoSize = true }, 0, 0);
        layout.Controls.Add(nameBox, 0, 1);
        layout.Controls.Add(new Label { Text = "Destination domains or IP addresses — one per line", AutoSize = true, Margin = new Padding(3, 12, 3, 3) }, 0, 2);
        layout.Controls.Add(targetsBox, 0, 3);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(3, 8, 3, 3), ForeColor = Color.DimGray,
            Text = "Example: chatgpt.com\nEnter exact domains or public IPs, without https:// or paths. The rule applies to these destinations from any program, not to a particular executable." }, 0, 4);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
        var save = new Button { Text = "Save rule", AutoSize = true, Padding = new Padding(8, 3, 8, 3), MinimumSize = new Size(80, 32) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, Padding = new Padding(8, 3, 8, 3), MinimumSize = new Size(80, 32), DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nameBox.Text)) throw new ArgumentException("Enter a rule name.");
                Result = new Profile { Id = profileId, Name = nameBox.Text.Trim(), Targets = TargetRules.ParseLines(targetsBox.Text) };
                DialogResult = DialogResult.OK;
            }
            catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "Check routing rule", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        if (original is not null)
            actions.Controls.Add(new Button { Text = "Remove rule", AutoSize = true, Padding = new Padding(8, 3, 8, 3),
                MinimumSize = new Size(80, 32), ForeColor = Color.Firebrick, DialogResult = DialogResult.Abort });
        layout.Controls.Add(actions, 0, 6);
        CancelButton = cancel;
        Controls.Add(layout);
    }
}
