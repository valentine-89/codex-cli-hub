namespace CodexAccountManager;

internal static class Theme
{
    public static readonly Color Ink = Color.FromArgb(20, 44, 62);
    public static readonly Color Muted = Color.FromArgb(100, 117, 128);
    public static readonly Color Background = Color.FromArgb(243, 247, 249);
    public static readonly Color Accent = Color.FromArgb(19, 118, 105);
    public static Icon Icon => new(typeof(Theme).Assembly.GetManifestResourceStream("CodexAccountManager.app.ico")!);
    public static Image Logo => Image.FromStream(typeof(Theme).Assembly.GetManifestResourceStream("CodexAccountManager.logo.png")!);

    /// <summary>Returns the DPI scale factor relative to 96 DPI (1.0 at 100%, 1.25 at 125%, 1.5 at 150%, 2.0 at 200%).</summary>
    public static float DpiScale
    {
        get
        {
            using var screen = Graphics.FromHwnd(IntPtr.Zero);
            return screen.DpiX / 96f;
        }
    }

    /// <summary>Scale a pixel value by the current DPI factor.</summary>
    public static int Scale(int pixels) => (int)Math.Round(pixels * DpiScale);

    /// <summary>Scale a Size by the current DPI factor.</summary>
    public static Size Scale(Size size) => new(Scale(size.Width), Scale(size.Height));

    public static Button Button(string text, bool primary = false, int width = 100)
    {
        var w = Scale(width);
        var h = Scale(34);
        var button = new Button { Text = text, Width = w, Height = h, FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Ink : Color.White, ForeColor = primary ? Color.White : Ink,
            Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9.5f), Margin = new Padding(0, 0, Scale(8), 0) };
        button.FlatAppearance.BorderColor = primary ? Ink : Color.FromArgb(219, 228, 232);
        return button;
    }
    public static void SetupDialog(Form form, string title, Size size)
    {
        form.Text = title; form.ClientSize = Scale(size); form.Font = new Font("Segoe UI", 10);
        form.BackColor = Background; form.ForeColor = Ink; form.Icon = Icon;
        form.FormBorderStyle = FormBorderStyle.FixedDialog; form.MaximizeBox = false; form.MinimizeBox = false;
        form.StartPosition = FormStartPosition.CenterParent; form.ShowInTaskbar = false;
        form.AutoScaleMode = AutoScaleMode.Dpi;
    }
    public static Label Label(string text, bool bold = false) => new()
    {
        Text = text, AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = bold ? Ink : Muted, Font = new Font("Segoe UI", bold ? 11 : 9.5f, bold ? FontStyle.Bold : FontStyle.Regular), Margin = Padding.Empty
    };
}
internal sealed class AccountCard : Panel
{
    public AccountCard() { DoubleBuffered = true; BackColor = Color.White; Padding = new Padding(Theme.Scale(14), Theme.Scale(8), Theme.Scale(14), Theme.Scale(8)); Margin = new Padding(0, 0, Theme.Scale(14), Theme.Scale(10)); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Color.FromArgb(220, 229, 233));
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
