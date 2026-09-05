using CodexAccountManager.Core;

namespace CodexAccountManager;

public sealed class AccountDetailsForm : Form
{
    private readonly TextBox name = new() { Dock = DockStyle.Fill, MaxLength = 100 };
    private readonly TextBox note = new() { Dock = DockStyle.Fill, MaxLength = 1000 };
    public string AccountName => name.Text.Trim();
    public string AccountNote => note.Text.Trim();

    public AccountDetailsForm(Account account, string profilePath, string sessionsPath)
    {
        Theme.SetupDialog(this, "Chi tiết tài khoản", new Size(540, 365));
        name.Text = account.DisplayName; note.Text = account.Note;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 14, 22, 14), ColumnCount = 1 };
        void Row(Control control, int height)
        {
            var row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(control, 0, row);
        }
        Row(Theme.Label("Tên tài khoản"), 25); Row(name, 36);
        Row(Theme.Label("Ghi chú"), 25); Row(note, 36);
        void Detail(string label, string value)
        {
            Row(Theme.Label(label), 22);
            Row(new TextBox { Text = value, ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = BackColor }, 27);
        }
        Detail("ID", account.Id); Detail("Codex Home", profilePath); Detail("Sessions", sessionsPath);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var save = Theme.Button("Lưu", true); var cancel = Theme.Button("Hủy"); cancel.DialogResult = DialogResult.Cancel;
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { name.Focus(); name.BackColor = Color.MistyRose; return; }
            DialogResult = DialogResult.OK;
        };
        name.TextChanged += (_, _) => name.BackColor = Color.White;
        actions.Controls.AddRange([save, cancel]); Row(actions, 42);
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
    }
}
