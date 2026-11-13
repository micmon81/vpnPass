using System.Drawing.Drawing2D;

namespace VpnPass;

internal sealed class RouteSwitch : CheckBox
{
    public RouteSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        Size = new Size(64, 36);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
        Text = "";
        CheckedChanged += (_, _) => Invalidate();
        EnabledChanged += (_, _) => Invalidate();
        GotFocus += (_, _) => Invalidate();
        LostFocus += (_, _) => Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = Math.Max(3, Height / 12);
        var track = new Rectangle(inset, inset, Width - inset * 2, Height - inset * 2);
        var color = !Enabled ? Color.FromArgb(190, 197, 204) : Checked ? Color.FromArgb(35, 179, 106) : Color.FromArgb(174, 183, 194);
        if (SystemInformation.HighContrast) color = Checked ? SystemColors.Highlight : SystemColors.ControlDark;
        using var path = new GraphicsPath();
        path.AddArc(track.Left, track.Top, track.Height, track.Height, 90, 180);
        path.AddArc(track.Right - track.Height, track.Top, track.Height, track.Height, 270, 180);
        path.CloseFigure();
        using var background = new SolidBrush(color);
        e.Graphics.FillPath(background, path);
        var diameter = track.Height - 4;
        var thumbX = Checked ? track.Right - diameter - 2 : track.Left + 2;
        using var thumb = new SolidBrush(SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White);
        e.Graphics.FillEllipse(thumb, thumbX, track.Top + 2, diameter, diameter);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
    }
}
