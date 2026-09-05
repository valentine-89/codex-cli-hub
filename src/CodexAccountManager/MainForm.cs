using CodexAccountManager.Core;
using System.Diagnostics;

namespace CodexAccountManager;

public sealed class MainForm : Form
{
    private readonly AccountRepository repository;
    private readonly CodexProfileService profiles;
    private readonly JunctionService junctions = new();
    private readonly CodexProcessLauncher launcher = new();
    private readonly CodexStatusService statusService = new();
    private readonly IQuotaProvider quotaProvider = new UnavailableQuotaProvider();
    private readonly Logger logger;
    private readonly AccountDocument accounts;
    private AppSettings settings;
    private Dependencies dependencies = new(null, null, false);
    private readonly DataGridView grid = new();
    private readonly TextBox displayName = new() { Width = 185, PlaceholderText = "Display name", MaxLength = 100 };
    private readonly TextBox note = new() { Width = 225, PlaceholderText = "Note (optional)", MaxLength = 1000 };
    private readonly TextBox home = new() { Width = 360 };
    private readonly TextBox workingDirectory = new() { Width = 360 };
    private readonly Label details = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Padding = new Padding(8, 3, 8, 3) };
    private readonly ToolStripStatusLabel status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly FlowLayoutPanel toolbar = new() { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
    private readonly FlowLayoutPanel addPanel = new() { Dock = DockStyle.Fill, WrapContents = false };
    private readonly FlowLayoutPanel settingsPanel = new() { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
    private bool busy;
    private readonly TaskCompletionSource ready = new();
    public Task Ready => ready.Task;

    public MainForm(AccountRepository repository)
    {
        this.repository = repository;
        logger = new(repository.Root);
        profiles = new(repository.Root, junctions);
        accounts = repository.LoadAccounts();
        settings = repository.LoadSettings();
        if (!File.Exists(Path.Combine(repository.Root, "accounts.json"))) repository.SaveAccounts(accounts);
        if (!File.Exists(Path.Combine(repository.Root, "settings.json"))) repository.SaveSettings(settings);
        logger.Write("startup", repository.Root);
        Text = "Codex Account Manager";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1180, 620);
        MinimumSize = new Size(980, 440);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(246, 248, 250);
        AutoScaleMode = AutoScaleMode.Dpi;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        Controls.Add(layout);
        toolbar.Controls.Add(Button("Open Codex", () => LaunchSelected(CodexAction.Open), true));
        toolbar.Controls.Add(Button("Resume", () => LaunchSelected(CodexAction.Resume)));
        toolbar.Controls.Add(Button("Check", CheckSelected));
        toolbar.Controls.Add(Button("Login / Re-login", () => LaunchSelected(CodexAction.Login)));
        toolbar.Controls.Add(Button("Open Folder", OpenFolder));
        toolbar.Controls.Add(Button("Delete", DeleteSelected));
        toolbar.Controls.Add(Button("Refresh", RefreshDependencies));
        addPanel.Controls.AddRange([new Label { Text = "Name", AutoSize = true, Margin = new Padding(3, 6, 3, 0) },
            displayName, new Label { Text = "Note", AutoSize = true, Margin = new Padding(8, 6, 3, 0) }, note, Button("+ Add Account", AddAccount)]);

        var homeGroup = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 370, Height = 56 };
        homeGroup.Controls.Add(new Label { Text = "Default Codex Home", AutoSize = true });
        home.Text = settings.DefaultCodexHome; homeGroup.Controls.Add(home);
        var workGroup = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Width = 370, Height = 56 };
        workGroup.Controls.Add(new Label { Text = "Working directory", AutoSize = true });
        workingDirectory.Text = settings.WorkingDirectory; workGroup.Controls.Add(workingDirectory);
        settingsPanel.Controls.AddRange([homeGroup, workGroup, Button("Save Settings", SaveSettings)]);

        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false; grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false;
        grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.RowTemplate.Height = 34; grid.AutoGenerateColumns = false;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(229, 235, 241);
        grid.ColumnHeadersHeight = 34;
        foreach (var (key, title, weight) in new[] { ("Name", "Account", 18f), ("Login", "Login status", 22f),
            ("Sessions", "Sessions", 12f), ("Quota", "Quota", 11f), ("Checked", "Last checked", 16f), ("Note", "Note", 21f) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = key, HeaderText = title, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.SelectionChanged += (_, _) => ShowDetails();
        var strip = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = false };
        strip.Items.Add(status);
        layout.Controls.Add(toolbar, 0, 0); layout.Controls.Add(addPanel, 0, 1); layout.Controls.Add(settingsPanel, 0, 2);
        layout.Controls.Add(grid, 0, 3); layout.Controls.Add(details, 0, 4); layout.Controls.Add(strip, 0, 5);
        RenderAccounts();
        Shown += async (_, _) => { try { await Run(RefreshDependencies); } finally { ready.TrySetResult(); } };
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; status.Text = "Wait for the current operation to finish."; } };
    }

    private Button Button(string text, Func<Task> action, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 32, MinimumSize = new Size(85, 32),
            FlatStyle = FlatStyle.Flat, Padding = new Padding(6, 0, 6, 0),
            BackColor = primary ? Color.FromArgb(32, 105, 185) : Color.White,
            ForeColor = primary ? Color.White : Color.FromArgb(35, 43, 53) };
        button.Click += async (_, _) => await Run(action);
        return button;
    }

    private async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; toolbar.Enabled = addPanel.Enabled = settingsPanel.Enabled = grid.Enabled = false;
        UseWaitCursor = true;
        try { await action(); }
        catch (Exception ex)
        {
            status.Text = "Operation stopped.";
            try { logger.Write("error:" + ex.GetType().Name, exitCode: ex.HResult); }
            catch (Exception logError) { status.Text = "Operation stopped; log unavailable (" + logError.GetType().Name + ")."; }
            MessageBox.Show(this, ex.Message, "Operation stopped", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            busy = false; toolbar.Enabled = addPanel.Enabled = settingsPanel.Enabled = grid.Enabled = true;
            UseWaitCursor = false; RenderAccounts();
        }
    }

    private Account Selected() => grid.SelectedRows.Count == 1 && grid.SelectedRows[0].Tag is Account account
        ? account : throw new IOException("Select an account first.");

    private void RenderAccounts()
    {
        var selected = grid.SelectedRows.Count > 0 ? (grid.SelectedRows[0].Tag as Account)?.Id : null;
        grid.Rows.Clear();
        foreach (var account in accounts.Accounts)
        {
            string shared;
            try { profiles.Validate(account, settings); shared = "Shared ✓"; }
            catch (Exception) { shared = "Needs attention"; }
            var index = grid.Rows.Add(account.DisplayName, account.LoginStatus, shared, quotaProvider.GetDisplay(account),
                account.LastCheckedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—", account.Note);
            grid.Rows[index].Tag = account;
        }
        if (selected is not null)
            foreach (DataGridViewRow row in grid.Rows)
                if ((row.Tag as Account)?.Id == selected) { grid.CurrentCell = row.Cells[0]; break; }
        ShowDetails();
    }

    private void ShowDetails()
    {
        if (grid.SelectedRows.Count == 0 || grid.SelectedRows[0].Tag is not Account account)
        { details.Text = accounts.Accounts.Count == 0 ? "No accounts. Enter a display name and choose Add Account." : ""; return; }
        details.Text = $"ID: {account.Id}\nCodex Home: {Path.Combine(repository.Root, account.ProfilePath)}";
    }

    private async Task RefreshDependencies()
    {
        status.Text = "Checking dependencies…";
        dependencies = await DependencyDetector.DetectAsync();
        string shared;
        try { _ = profiles.SharedTarget(settings); shared = "OK"; } catch (Exception) { shared = "Needs attention"; }
        status.Text = $"Codex CLI: {(dependencies.Codex is null ? "Not found" : "Found")}  •  pwsh: {(dependencies.PowerShell is null ? "Not found" : "Found")}  •  Shared sessions: {shared}";
        status.ToolTipText = $"Codex: {dependencies.Codex ?? "Not found"}\npwsh: {dependencies.PowerShell ?? "Not found"}";
        status.Owner!.ShowItemToolTips = true;
    }

    private Task AddAccount()
    {
        dependencies.Require();
        var account = profiles.Create(displayName.Text, note.Text, settings);
        accounts.Accounts.Add(account);
        try { repository.SaveAccounts(accounts); }
        catch { accounts.Accounts.Remove(account); throw; }
        displayName.Clear(); note.Clear();
        logger.Write("create", PathSafety.Profile(repository.Root, account));
        launcher.Launch(dependencies, account.Id, profiles.Validate(account, settings), settings.WorkingDirectory, CodexAction.Login);
        status.Text = "Login terminal opened. Choose Check after signing in.";
        return Task.CompletedTask;
    }

    private Task LaunchSelected(CodexAction action)
    {
        var account = Selected();
        var path = profiles.Validate(account, settings);
        launcher.Launch(dependencies, account.Id, path, settings.WorkingDirectory, action);
        logger.Write("launch:" + action, path);
        status.Text = "Terminal opened: " + account.DisplayName;
        return Task.CompletedTask;
    }

    private async Task CheckSelected()
    {
        var account = Selected();
        var path = profiles.Validate(account, settings);
        status.Text = "Checking " + account.DisplayName + "…";
        account.LoginStatus = await statusService.CheckAsync(dependencies, path);
        account.LastCheckedAt = DateTimeOffset.UtcNow;
        repository.SaveAccounts(accounts);
        logger.Write("check", path);
        status.Text = account.LoginStatus;
    }

    private Task DeleteSelected()
    {
        var account = Selected();
        var path = profiles.Validate(account, settings);
        if (launcher.IsRunning(account.Id)) throw new IOException("Close this account's terminals before deleting it.");
        if (MessageBox.Show(this, $"Delete {account.DisplayName}?\n\n{path}\n\nThis profile's auth, config and private state will be deleted.\nShared Codex App sessions will NOT be deleted.\n\nClose all terminals using this profile, including terminals opened before restarting this manager.",
            "Delete account", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return Task.CompletedTask;
        profiles.Delete(account, settings);
        accounts.Accounts.Remove(account);
        try { repository.SaveAccounts(accounts); }
        catch { accounts.Accounts.Add(account); throw; }
        logger.Write("delete", path);
        status.Text = "Account deleted; shared sessions preserved.";
        return Task.CompletedTask;
    }

    private Task OpenFolder()
    {
        var path = PathSafety.Profile(repository.Root, Selected());
        PathSafety.OrdinaryDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { path } });
        return Task.CompletedTask;
    }

    private Task SaveSettings()
    {
        var updated = new AppSettings { DefaultCodexHome = PathSafety.Canonical(home.Text), WorkingDirectory = PathSafety.Canonical(workingDirectory.Text) };
        if (accounts.Accounts.Count > 0 && !PathSafety.Same(updated.DefaultCodexHome, settings.DefaultCodexHome))
            throw new IOException("Delete existing accounts safely before changing the shared Codex Home. Existing junctions are not retargeted automatically.");
        _ = profiles.SharedTarget(updated);
        PathSafety.OrdinaryDirectory(updated.WorkingDirectory);
        repository.SaveSettings(updated); settings = updated;
        status.Text = "Settings saved.";
        return Task.CompletedTask;
    }
}
