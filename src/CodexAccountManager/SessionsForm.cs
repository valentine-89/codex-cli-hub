using CodexAccountManager.Core;
using System.Text.Json;

namespace CodexAccountManager;

public sealed class SessionsForm : Form
{
    private readonly AccountRepository repository;
    private readonly SessionRuntime runtime;
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None, AutoGenerateColumns = false };
    private readonly RichTextBox message = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
        BackColor = Color.White, DetectUrls = true, Font = new Font("Segoe UI", 10) };
    private readonly Label status = Theme.Label("");
    private readonly Button stop = Theme.Button("Stop", true, 90);
    private readonly Button force = Theme.Button("Force stop", false, 110);
    private readonly Button terminal = Theme.Button("Open terminal", false, 130);
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private string? selectedId;
    private bool refreshing, stopping;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Ready => ready.Task;
    private string? actionError;

    public SessionsForm(AccountRepository repository, string? selectedId = null)
    {
        this.repository = repository; runtime = new(repository.Root); this.selectedId = selectedId;
        Text = "Codex Sessions"; Icon = Theme.Icon; Font = new Font("Segoe UI", 9.5f);
        BackColor = Theme.Background; ForeColor = Theme.Ink; AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = Theme.Scale(new Size(860, 450)); MinimumSize = Theme.Scale(new Size(620, 340));
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, Padding = new Padding(Theme.Scale(12)) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Scale(42)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Scale(65)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Scale(26)));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var refresh = Theme.Button("Refresh", width: 90);
        refresh.Click += async (_, _) => await RefreshRows();
        actions.Controls.Add(refresh); actions.Controls.Add(stop); actions.Controls.Add(force); actions.Controls.Add(terminal);
        force.Visible = false; stop.Enabled = false;
        stop.Click += async (_, _) => await StopSelected(false);
        force.Click += async (_, _) => await StopSelected(true);
        terminal.Click += async (_, _) => await OpenTerminal();
        foreach (var (name, title, width) in new[] { ("account", "Account", 22), ("folder", "Folder", 43),
            ("state", "Status", 22), ("pid", "PID", 13) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, FillWeight = width,
                SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.RowTemplate.Height = Theme.Scale(28);
        grid.SelectionChanged += (_, _) => { if (!refreshing) { selectedId = Selected()?.Id; actionError = null; force.Visible = false; UpdateDetails(); } };
        layout.Controls.Add(actions, 0, 0); layout.Controls.Add(grid, 0, 1); layout.Controls.Add(message, 0, 2); layout.Controls.Add(status, 0, 3);
        Controls.Add(layout);
        Shown += async (_, _) => { await RefreshRows(); ready.TrySetResult(); timer.Start(); };
        timer.Tick += async (_, _) => await RefreshRows();
        FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); };
    }
    public void SelectSession(string id) { selectedId = id; _ = RefreshRows(); }
    private CliSessionRecord? Selected() => grid.CurrentRow?.Tag as CliSessionRecord;
    private async Task RefreshRows()
    {
        if (refreshing || stopping || IsDisposed) return;
        refreshing = true;
        try
        {
            var chosen = selectedId;
            var rows = await Task.Run(() =>
            {
                var accounts = repository.LoadAccounts().Accounts.ToDictionary(a => a.Id, a => a.DisplayName);
                var home = repository.LoadSettings().DefaultCodexHome;
                return runtime.List().Where(r => SessionRuntime.Alive(r) || r.Id == chosen).OrderByDescending(r => r.CreatedAt)
                    .Select(r => (Record: r, Name: accounts.GetValueOrDefault(r.AccountId, r.AccountId),
                        State: runtime.ReadState(r, home), Alive: SessionRuntime.Alive(r))).ToArray();
            });
            if (IsDisposed) return;
            grid.Rows.Clear();
            foreach (var row in rows)
            {
                var state = row.Alive ? DisplayStatus(CodexThreadReader.Text(row.State, "status")) : "Stopped";
                var index = grid.Rows.Add(row.Name, row.Record.WorkingDirectory, state, row.Record.ProcessId);
                grid.Rows[index].Tag = row.Record; grid.Rows[index].Cells[1].ToolTipText = row.Record.WorkingDirectory;
                grid.Rows[index].Cells[2].Tag = row.State;
                if (row.Record.Id == chosen) grid.CurrentCell = grid.Rows[index].Cells[0];
            }
            selectedId = Selected()?.Id;
            UpdateDetails();
        }
        catch (Exception ex) { if (!IsDisposed) status.Text = AutomationErrors.Safe(ex); }
        finally { refreshing = false; }
    }
    private static string DisplayStatus(string? value) => value switch
    { "active" => "Running", "completed" => "Completed · idle", "failed" => "Failed · idle",
        "running_or_idle" => "Running / idle", "idle" => "Idle", "interrupted" => "Interrupted", "stopped" => "Stopped", _ => value ?? "Unknown" };
    private void UpdateDetails()
    {
        var record = Selected();
        stop.Enabled = !stopping && record is not null && SessionRuntime.Alive(record);
        force.Enabled = stop.Enabled;
        terminal.Enabled = !stopping && record?.ThreadId is not null;
        if (grid.CurrentRow?.Cells[2].Tag is not JsonElement state)
        { message.Text = ""; status.Text = "No managed sessions."; return; }
        var text = CodexThreadReader.Text(state, "lastMessage") ?? CodexThreadReader.Text(state, "finalMessage") ?? "";
        var error = CodexThreadReader.Text(state, "error");
        if (error is not null) text = string.IsNullOrEmpty(text) ? error : text + "\n\n" + error;
        if (message.Text != text) message.Text = text;
        var timestamp = CodexThreadReader.Text(state, "lastEventAt");
        status.Text = actionError ?? record?.ThreadId + (DateTimeOffset.TryParse(timestamp, out var at) ? "  ·  " + at.ToLocalTime().ToString("HH:mm:ss") : "");
    }
    private async Task StopSelected(bool forceStop)
    {
        var record = Selected(); if (record is null || stopping) return;
        stopping = true; actionError = null; stop.Enabled = force.Enabled = false; status.Text = "Stopping…";
        try
        {
            await Task.Run(async () =>
            {
                var current = runtime.Get(record.Id);
                if (!SessionRuntime.Alive(current)) return;
                var state = runtime.ReadState(current, repository.LoadSettings().DefaultCodexHome);
                if (forceStop || current.Mode == "terminal" || CodexThreadReader.Text(state, "status") is "completed" or "failed" or "stopped")
                { SessionRuntime.StopVerified(current); return; }
                await AutomationPipe.Call(AutomationPipe.Name(repository.Root, "session-" + current.Id), "stop", new { }, 40);
                var until = DateTimeOffset.UtcNow.AddSeconds(10);
                while (SessionRuntime.Alive(current) && DateTimeOffset.UtcNow < until) await Task.Delay(100);
                if (SessionRuntime.Alive(current)) throw new IOException("Session did not stop. Force stop is available.");
            });
            force.Visible = false; status.Text = "Stopped.";
        }
        catch (Exception ex) { actionError = AutomationErrors.Safe(ex); status.Text = actionError; force.Visible = true; }
        finally { stopping = false; force.Enabled = true; await RefreshRows(); }
    }
    private async Task OpenTerminal()
    {
        var record = Selected();
        if (record?.ThreadId is null || stopping) return;
        stopping = true; actionError = null; stop.Enabled = force.Enabled = terminal.Enabled = false; status.Text = "Opening…";
        try
        {
            var response = await AutomationPipe.Call(AutomationPipe.Name(repository.Root), "session_open_terminal", new { sessionId = record.Id }, 90);
            if (response.TryGetProperty("session", out var replacement)) selectedId = replacement.GetProperty("id").GetString();
        }
        catch (Exception ex) { actionError = AutomationErrors.Safe(ex); status.Text = actionError; }
        finally { stopping = false; await RefreshRows(); }
    }
}