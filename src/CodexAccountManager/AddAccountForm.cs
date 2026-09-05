namespace CodexAccountManager;

public sealed class AddAccountForm : Form
{
    private readonly TextBox name = new() { Dock = DockStyle.Fill, MaxLength = 100 };
    private readonly TextBox note = new() { Dock = DockStyle.Fill, MaxLength = 1000 };
    private readonly RadioButton copy = new() { Text = "Sao chép tài khoản Codex gốc", AutoSize = true };
    public string AccountName => name.Text.Trim();
    public string AccountNote => note.Text.Trim();
    public bool CopyDefaultAccount => copy.Checked;
    public AddAccountForm()
    {
        Theme.SetupDialog(this, "Thêm tài khoản", new Size(440, 305));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 14, 22, 14), ColumnCount = 1, RowCount = 7 };
        foreach (var height in new[] { 25, 36, 25, 37, 34, 42, 42 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(Theme.Label("Tên tài khoản"), 0, 0); layout.Controls.Add(name, 0, 1);
        layout.Controls.Add(Theme.Label("Ghi chú"), 0, 2); layout.Controls.Add(note, 0, 3);
        layout.Controls.Add(new RadioButton { Text = "Đăng nhập mới", Checked = true, AutoSize = true }, 0, 4);
        layout.Controls.Add(copy, 0, 5);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var add = Theme.Button("Thêm", true); var cancel = Theme.Button("Hủy"); cancel.DialogResult = DialogResult.Cancel;
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { name.Focus(); name.BackColor = Color.MistyRose; return; }
            DialogResult = DialogResult.OK;
        };
        name.TextChanged += (_, _) => name.BackColor = Color.White;
        actions.Controls.AddRange([add, cancel]); layout.Controls.Add(actions, 0, 6);
        Controls.Add(layout); AcceptButton = add; CancelButton = cancel;
    }
}
