using CodexAccountManager.Core;
using System.Diagnostics;

namespace CodexAccountManager;

public sealed class MainForm : Form
{
    private readonly AccountRepository repository;
    private readonly CodexProfileService profiles;
    private readonly CodexProcessLauncher launcher = new();
    private readonly Func<string, CancellationToken, Task<AccountRefreshResult>> readAccount;
    private readonly CodexQuotaService quotaService = new();
    private readonly Logger logger;
    private readonly AccountDocument accounts;
    private AppSettings settings;
    private Dependencies dependencies = new(null, null, false);
    private readonly AccountListPanel cards = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(Theme.Scale(22), Theme.Scale(4), Theme.Scale(8), Theme.Scale(8)) };
    private readonly Label status = Theme.Label("Checking…");
    private readonly Label count = Theme.Label("");
    private readonly Panel header = new() { Dock = DockStyle.Fill };
    private readonly ToolTip tips = new();
    private bool busy;
    private readonly HashSet<string> warming = [];
    private readonly HashSet<string> refreshing = [];
    private readonly RefreshQueueGate refreshQueue = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Forms.Timer resetTimer = new() { Interval = 1000 };
    private readonly TaskCompletionSource ready = new();
    private readonly ManagerAutomation automation;
    private readonly SessionRuntime sessionRuntime;
    private AutomationPipe? automationPipe;
    public Task Ready => ready.Task;

    public MainForm(AccountRepository repository, Func<string, CancellationToken, Task<AccountRefreshResult>>? readAccount = null)
    {
        this.readAccount = readAccount ?? new AccountRefreshService().ReadAsync;
        this.repository = repository; logger = new(repository.Root); profiles = new(repository.Root, new JunctionService());
        accounts = repository.LoadAccounts(); settings = repository.LoadSettings();
        sessionRuntime = new SessionRuntime(repository.Root);
        launcher.OnLaunched = (process, id, cwd, thread) => sessionRuntime.Track(process, id, cwd, thread, "terminal");
        automation = new(repository, () => accounts, () => settings, async account =>
        {
            if (busy || AccountBusy(account)) throw new IOException("Account operation is busy. Retry after it finishes.");
            await RefreshAccount(account);
        }, async () => { if (dependencies.NativeCodex is null) await RefreshDependencies(); return dependencies; }, Environment.ProcessPath!);
        if (!File.Exists(Path.Combine(repository.Root, "accounts.json"))) repository.SaveAccounts(accounts);
        if (!File.Exists(Path.Combine(repository.Root, "settings.json"))) repository.SaveSettings(settings);
        logger.Write("startup", repository.Root);
        Text = "Codex Account Manager"; Icon = Theme.Icon; Font = new Font("Segoe UI", 9.5f);
        BackColor = Theme.Background; ForeColor = Theme.Ink;
        ClientSize = Theme.Scale(new Size(940, 565));
        MinimumSize = Theme.Scale(new Size(620, 420));
        StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi; DoubleBuffered = true;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Scale(88)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Scale(34)));

        // Header uses a TableLayoutPanel instead of absolute positioning for DPI scaling
        var headerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.Scale(72)));   // logo column
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));                 // title/count column (fill)
        headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));                     // action buttons column
        headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        headerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        var logo = new PictureBox { Image = Theme.Logo, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Fill, Margin = new Padding(Theme.Scale(24), Theme.Scale(12), 0, Theme.Scale(8)) };
        headerLayout.Controls.Add(logo, 0, 0);
        headerLayout.SetRowSpan(logo, 2);

        var title = Theme.Label("Codex Accounts", true);
        title.Dock = DockStyle.Fill; title.AutoSize = false;
        title.Font = new Font("Segoe UI", 16, FontStyle.Bold);
        title.TextAlign = ContentAlignment.BottomLeft;
        title.Margin = new Padding(Theme.Scale(8), 0, 0, 0);
        headerLayout.Controls.Add(title, 1, 0);

        count.Dock = DockStyle.Fill; count.AutoSize = false;
        count.TextAlign = ContentAlignment.TopLeft;
        count.Margin = new Padding(Theme.Scale(8), 0, 0, 0);
        headerLayout.Controls.Add(count, 1, 1);

        var topActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Anchor = AnchorStyles.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, Theme.Scale(20), Theme.Scale(16), 0) };
        topActions.Controls.Add(ActionButton("Settings", SettingsDialog, false, 100));
        topActions.Controls.Add(ActionButton("+ Add account", AddAccount, true, 155));
        headerLayout.Controls.Add(topActions, 2, 0);
        headerLayout.SetRowSpan(topActions, 2);

        header.Controls.Add(headerLayout);
        status.Padding = new Padding(Theme.Scale(24), 0, 0, 0);
        layout.Controls.Add(header, 0, 0); layout.Controls.Add(cards, 0, 1); layout.Controls.Add(status, 0, 2);
        Controls.Add(layout);
        cards.Resize += (_, _) => ResizeCards(); RenderAccounts();
        Shown += async (_, _) =>
        {
            try
            {
                await Run(async () =>
                {
                    await RefreshDependencies();
                });
            }
            finally
            {
                ready.TrySetResult(); refreshQueue.DelayStart(DateTimeOffset.UtcNow); resetTimer.Start();
                automationPipe = new(AutomationPipe.Name(repository.Root), InvokeAutomation);
                automationPipe.Start();
            }
        };
        resetTimer.Tick += async (_, _) => await RefreshDueAccounts();
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; status.Text = "Please wait for the current operation."; } };
        FormClosed += (_, _) => { automationPipe?.Dispose(); lifetime.Cancel(); resetTimer.Stop(); resetTimer.Dispose(); tips.Dispose(); logo.Image?.Dispose(); };
    }

    private Task<object> InvokeAutomation(string method, System.Text.Json.JsonElement args)
    {
        var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (IsDisposed || !IsHandleCreated) throw new IOException("Manager is closing.");
        BeginInvoke(new Action(async () =>
        {
            try { completion.TrySetResult(await automation.Call(method, args)); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }));
        return completion.Task;
    }

    private Button ActionButton(string text, Func<Task> action, bool primary = false, int width = 100)
    {
        var button = Theme.Button(text, primary, width);
        button.Click += async (_, _) => await Run(action); return button;
    }
    private async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; header.Enabled = cards.Enabled = false;
        try { await action(); }
        catch (Exception ex)
        {
            status.Text = "Operation incomplete.";
            try { logger.Write("error:" + ex.GetType().Name, exitCode: ex.HResult); }
            catch (Exception) { status.Text = "Operation incomplete; could not write the log."; }
            MessageBox.Show(this, ex.Message, "Codex Accounts", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { busy = false; header.Enabled = cards.Enabled = true; UseWaitCursor = false; RenderAccounts(); }
    }
    private void ResizeCards()
    {
        var available = cards.ClientSize.Width - cards.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth;
        var columns = available >= Theme.Scale(790) ? 2 : 1;
        foreach (Control card in cards.Controls) card.Width = Math.Max(Theme.Scale(320), available / columns - Theme.Scale(14));
    }
    private void RenderAccounts()
    {
        var scroll = cards.AutoScrollPosition; cards.SuspendLayout();
        foreach (Control control in cards.Controls.Cast<Control>().ToArray()) control.Dispose();
        count.Text = accounts.Accounts.Count == 1 ? "1 account" : $"{accounts.Accounts.Count} accounts";
        if (accounts.Accounts.Count == 0)
        {
            var empty = new AccountCard { Height = Theme.Scale(145) };
            var label = Theme.Label("Add your first account", true); label.TextAlign = ContentAlignment.MiddleCenter;
            empty.Controls.Add(label); cards.Controls.Add(empty);
        }
        var rows = QuotaRows();
        foreach (var account in accounts.Accounts) cards.Controls.Add(CreateCard(account, rows));
        ResizeCards(); cards.ResumeLayout(); cards.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
    }
    private int QuotaRows() => accounts.Accounts.Select(a => Math.Max(1, a.Quota?.Lines.Count ?? 0)
        + (a.QuotaError is not null ? 1 : 0)).DefaultIfEmpty(1).Max();
    private bool AccountBusy(Account account) => refreshing.Contains(account.Id) || warming.Contains(account.Id)
        || refreshQueue.ActiveAccountId == account.Id;
    private Control? FindCard(Account account) => cards.Controls.Cast<Control>().FirstOrDefault(c => Equals(c.Tag, account.Id));
    private void UpdateCardState(Account account)
    {
        if (FindCard(account) is not { } card) return;
        cards.UpdateWithoutScrolling(() =>
        {
            // Retire focus before disabling its card instead of selecting another account's button.
            if (AccountBusy(account) && card.ContainsFocus) cards.Focus();
            card.Enabled = !AccountBusy(account);
            var button = card.Controls.Find("refresh", true).OfType<Button>().Single();
            button.Text = refreshing.Contains(account.Id) || refreshQueue.ActiveAccountId == account.Id ? "Refreshing…" : "Refresh";
            card.Controls.Find("warm", true).OfType<Button>().Single().Text = warming.Contains(account.Id) ? "Warming…" : "Warm up";
        });
    }
    private void RenderAccount(Account account)
    {
        if (IsDisposed || lifetime.IsCancellationRequested || FindCard(account) is not AccountCard card) return;
        cards.UpdateWithoutScrolling(() =>
        {
            card.RefreshContent?.Invoke();
            UpdateCardState(account);
        });
    }
    private Control CreateCard(Account account, int quotaRows)
    {
        var card = new AccountCard { Tag = account.Id, Height = Theme.Scale(140) + quotaRows * Theme.Scale(22) };
        card.Enabled = !AccountBusy(account);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = Padding.Empty };
        foreach (var height in new[] { 26, 23, quotaRows * 22, 25, 37 }) body.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Scale(height)));
        TableLayoutPanel Columns(Control left, Control right, int rightWidth)
        {
            var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.Scale(rightWidth)));
            columns.Controls.Add(left, 0, 0); columns.Controls.Add(right, 1, 0);
            if (right is Label label) label.TextAlign = ContentAlignment.MiddleRight;
            return columns;
        }
        var heading = Theme.Label(account.DisplayName, true); heading.Font = new Font("Segoe UI", 12, FontStyle.Bold);
        var id = Theme.Label(account.Id[..8]); tips.SetToolTip(id, account.Id);
        body.Controls.Add(Columns(heading, id, 76), 0, 0); tips.SetToolTip(heading, account.DisplayName);
        var login = Theme.Label("");
        var sharedLabel = Theme.Label("");
        body.Controls.Add(Columns(login, sharedLabel, 140), 0, 1);
        // Keep the outer footprint stable for this list. Extra quota/error rows scroll inside it.
        var quotaViewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty, Name = "quotaViewport" };
        var quotaPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 0, Margin = Padding.Empty };
        quotaPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        quotaViewport.Controls.Add(quotaPanel);
        body.Controls.Add(quotaViewport, 0, 2);
        var note = Theme.Label(string.IsNullOrEmpty(account.Note) ? "—" : account.Note); tips.SetToolTip(note, account.Note);
        var updatedText = "Updated: " + (account.LastCheckedAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? "—");
        var updated = Theme.Label(updatedText); tips.SetToolTip(updated, updatedText);
        body.Controls.Add(Columns(note, updated, 166), 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        actions.Controls.Add(ActionButton("Open CLI", () => Launch(account, CodexAction.Open), true, 82));
        var refresh = Theme.Button(refreshing.Contains(account.Id) || refreshQueue.ActiveAccountId == account.Id ? "Refreshing…" : "Refresh", width: 92);
        refresh.Name = "refresh";
        refresh.Click += async (_, _) => await RefreshAccount(account);
        actions.Controls.Add(refresh);
        var warm = Theme.Button(warming.Contains(account.Id) ? "Warming…" : "Warm up", width: 82);
        warm.Name = "warm";
        tips.SetToolTip(warm, "Send reply \"OK\" without saving a session. Uses a small amount of quota.");
        warm.Click += async (_, _) => await WarmUp(account); actions.Controls.Add(warm);
        actions.Controls.Add(ActionButton("Apply", () => Apply(account), false, 58));
        var more = Theme.Button("•••", width: 32); var menu = new ContextMenuStrip();
        void Item(string text, Func<Task> action) { var item = menu.Items.Add(text); item.Click += async (_, _) => { if (!AccountBusy(account)) await Run(action); }; }
        Item("Log in again", () => Launch(account, CodexAction.Login)); Item("Resume in CLI", () => Launch(account, CodexAction.Resume));
        Item("Open folder", () => OpenFolder(account));
        Item(account.PendingResetAttempt is null ? "Use reset credit…" : "Retry reset attempt…", () => UseResetCredit(account));
        Item("Details", () =>
        {
            using var dialog = new AccountDetailsForm(account, PathSafety.Profile(repository.Root, account), Path.Combine(settings.DefaultCodexHome, "sessions"));
            if (dialog.ShowDialog(this) != DialogResult.OK) return Task.CompletedTask;
            var oldName = account.DisplayName; var oldNote = account.Note;
            account.DisplayName = dialog.AccountName; account.Note = dialog.AccountNote;
            try { repository.SaveAccounts(accounts); }
            catch { account.DisplayName = oldName; account.Note = oldNote; throw; }
            status.Text = "Account updated.";
            return Task.CompletedTask;
        });
        menu.Items.Add(new ToolStripSeparator()); Item("Delete account", () => Delete(account));
        more.Click += (_, _) => menu.Show(more, new Point(0, more.Height)); more.Disposed += (_, _) => menu.Dispose();
        actions.Controls.Add(more); body.Controls.Add(actions, 0, 4); card.Controls.Add(body);
        card.RefreshContent = () =>
        {
            var weekly = QuotaPresentation.WeeklyOnly(account.Quota); var isLow = QuotaPresentation.Low(account.Quota);
            card.BackColor = weekly ? (isLow ? Color.FromArgb(250, 244, 221) : Color.FromArgb(245, 218, 140))
                : isLow ? Color.FromArgb(225, 228, 231) : Color.White;
            var state = account.LoginStatus switch
            {
                "Logged in (local credentials)" => "●  Logged in", "Not logged in" => "○  Not logged in",
                "Not checked" => "○  Not checked", _ => account.LoginStatus
            };
            if (!string.IsNullOrWhiteSpace(account.Quota?.PlanType)) state += " · " + account.Quota.PlanType;
            login.Text = state; tips.SetToolTip(login, state);
            login.ForeColor = account.LoginStatus.StartsWith("Logged in", StringComparison.Ordinal) ? Theme.Accent : Theme.Muted;
            try { profiles.Validate(account, settings); sharedLabel.Text = "Session files linked"; tips.SetToolTip(card, null); }
            catch (Exception ex) { sharedLabel.Text = "Check shared sessions"; tips.SetToolTip(card, ex.Message); }
            tips.SetToolTip(sharedLabel, sharedLabel.Text);
            updated.Text = "Updated: " + (account.LastCheckedAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? "—");
            tips.SetToolTip(updated, updated.Text);
            quotaPanel.SuspendLayout();
            try
            {
                foreach (Control control in quotaPanel.Controls.Cast<Control>().ToArray()) control.Dispose();
                quotaPanel.RowStyles.Clear(); quotaPanel.RowCount = 0;
                void AddQuota(string text, Color color)
                {
                    var row = quotaPanel.RowCount++;
                    quotaPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Scale(22)));
                    var label = Theme.Label(text); label.ForeColor = color; tips.SetToolTip(label, text);
                    quotaPanel.Controls.Add(label, 0, row);
                }
                if (account.QuotaError is not null)
                    AddQuota(account.Quota is null ? "Quota: refresh failed" : "Cached quota · " + account.Quota.FetchedAt.ToLocalTime().ToString("dd/MM HH:mm"), Color.DarkOrange);
                var lines = account.Quota?.Lines ?? [];
                if (lines.Count == 0) AddQuota(account.Quota is null ? "Quota: not checked" : "Quota: no limits returned", Theme.Muted);
                foreach (var line in lines) AddQuota(line.Display(), line.RemainingPercent is <= 10 ? Color.Firebrick : Theme.Accent);
            }
            finally { quotaPanel.ResumeLayout(true); }
        };
        card.RefreshContent();
        return card;
    }
    private async Task RefreshDependencies()
    {
        dependencies = await DependencyDetector.DetectAsync();
        status.Text = $"Codex {(dependencies.Codex is null ? "not installed" : "ready")}   ·   PowerShell {(dependencies.PowerShell is null ? "not installed" : dependencies.PowerShellMajor + (dependencies.PowerShellMajor < 7 ? " · UTF-8" : ""))}";
    }
    private async Task AddAccount()
    {
        using var dialog = new AddAccountForm(); if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!dialog.CopyDefaultAccount) dependencies.Require();
        var account = profiles.Create(dialog.AccountName, dialog.AccountNote, settings, dialog.CopyDefaultAccount);
        accounts.Accounts.Add(account);
        try { repository.SaveAccounts(accounts); } catch { accounts.Accounts.Remove(account); throw; }
        logger.Write(dialog.CopyDefaultAccount ? "create:copy-default" : "create:login", PathSafety.Profile(repository.Root, account));
        if (dialog.CopyDefaultAccount)
        {
            status.Text = "Added account from default Codex.";
            if (dependencies.Codex is not null && dependencies.PowerShell is not null) await Check(account);
        }
        else await Launch(account, CodexAction.Login);
    }
    private Task Launch(Account account, CodexAction action)
    {
        if (action == CodexAction.Login && account.PendingResetAttempt is not null)
            throw new IOException("Resolve the pending reset attempt before changing this account's login.");
        var path = profiles.PrepareSharedStore(account, settings);
        var workingDirectory = settings.WorkingDirectory;
        string? sessionId = null;
        if (action == CodexAction.Open)
        {
            using var picker = new ProjectPickerForm(account.DisplayName,
                token => new WorkspaceCatalogService().ReadAsync(dependencies, path, settings.DefaultCodexHome, token));
            if (picker.ShowDialog(this) != DialogResult.OK || picker.Selection is not { } selection) return Task.CompletedTask;
            workingDirectory = WorkspaceCatalogService.ResolveDirectory(selection, settings.DefaultCodexHome);
            sessionId = selection.SessionId;
            if (sessionId is not null) action = CodexAction.Resume;
        }
        if (sessionId is not null && sessionRuntime.List().Any(r => r.ThreadId == sessionId && SessionRuntime.Alive(r)))
            throw new IOException("This chat already has a running CLI session. Stop it before resuming again.");
        launcher.Launch(dependencies, account.Id, path, workingDirectory, action, settings.DefaultCodexHome, sessionId);
        logger.Write("launch:" + action, path); status.Text = "Opened " + account.DisplayName; return Task.CompletedTask;
    }
    private async Task Check(Account account)
    {
        var currentSettings = settings; var token = lifetime.Token;
        status.Text = "Refreshing " + account.DisplayName + "…";
        var result = await Task.Run(async () =>
        {
            var path = profiles.Validate(account, currentSettings); CredentialConfig.EnsureFile(path);
            return await readAccount(path, token);
        }, token);
        token.ThrowIfCancellationRequested();
        if (result.Dependencies is not null) dependencies = result.Dependencies;
        account.LoginStatus = result.LoginStatus; account.LastCheckedAt = DateTimeOffset.UtcNow;
        if (result.Quota is not null) account.Quota = result.Quota;
        account.QuotaError = result.QuotaError;
        // Commit on the UI thread so concurrent refreshes save the latest complete account document.
        var profilePath = PathSafety.Profile(repository.Root, account);
        repository.SaveAccounts(accounts); logger.Write("check", profilePath); status.Text = "Updated " + account.DisplayName;
        if (account.QuotaError is not null) status.Text = account.QuotaError;
    }
    private async Task RefreshAccount(Account account)
    {
        if (busy || AccountBusy(account) || !refreshing.Add(account.Id)) return;
        UpdateCardState(account);
        try { await Check(account); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!lifetime.IsCancellationRequested)
            {
                account.QuotaError = "Refresh failed. Use Refresh to retry.";
                status.Text = account.DisplayName + ": " + account.QuotaError;
                try { logger.Write("refresh-error:" + ex.GetType().Name, exitCode: ex.HResult); } catch (Exception) { }
            }
        }
        finally { refreshing.Remove(account.Id); RenderAccount(account); }
    }
    private async Task RefreshDueAccounts()
    {
        if (busy || IsDisposed || lifetime.IsCancellationRequested || refreshQueue.ActiveAccountId is not null) return;
        var account = accounts.Accounts.FirstOrDefault(a => !AccountBusy(a) && QuotaPresentation.DueReset(a, DateTimeOffset.UtcNow) is not null);
        if (account is null || !refreshQueue.TryStart(account.Id, DateTimeOffset.UtcNow)) return;
        var due = QuotaPresentation.DueReset(account, DateTimeOffset.UtcNow)!.Value;
        try
        {
            var previous = account.LastAutoRefreshReset;
            account.LastAutoRefreshReset = due;
            try { repository.SaveAccounts(accounts); }
            catch { account.LastAutoRefreshReset = previous; resetTimer.Stop(); throw; }
            UpdateCardState(account);
            var currentSettings = settings; var currentDependencies = dependencies; var token = lifetime.Token;
            var snapshot = await Task.Run(async () =>
            {
                var path = profiles.Validate(account, currentSettings); CredentialConfig.EnsureFile(path);
                return await quotaService.ReadAsync(currentDependencies, path, token);
            }, token);
            if (lifetime.IsCancellationRequested) return;
            account.Quota = snapshot; account.QuotaError = null; account.LastCheckedAt = DateTimeOffset.UtcNow;
            repository.SaveAccounts(accounts);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!lifetime.IsCancellationRequested)
            {
                account.QuotaError = "Automatic refresh failed. Use Refresh to retry.";
                if (!busy) status.Text = account.QuotaError;
            }
        }
        finally
        {
            refreshQueue.Complete(DateTimeOffset.UtcNow);
            RenderAccount(account);
        }
    }
    private async Task UseResetCredit(Account account)
    {
        if (MessageBox.Show(this, $"Use one available reset credit for {account.DisplayName}?\n\nThis uses an existing credit for this account and cannot be undone. No credits will be purchased.",
            "Use reset credit", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var path = profiles.Validate(account, settings); CredentialConfig.EnsureFile(path);
        account.PendingResetAttempt ??= Guid.NewGuid().ToString();
        repository.SaveAccounts(accounts);
        string outcome;
        try { outcome = await quotaService.ConsumeResetAsync(dependencies, path, account.PendingResetAttempt); }
        catch (Exception) { throw new IOException("Reset result is uncertain. Choose Retry reset attempt to check the same attempt without consuming another credit."); }
        var completedAttempt = account.PendingResetAttempt;
        account.PendingResetAttempt = null;
        try { repository.SaveAccounts(accounts); }
        catch { account.PendingResetAttempt = completedAttempt; throw; }
        var message = outcome switch
        {
            "reset" => "Reset credit used successfully.", "alreadyRedeemed" => "This reset attempt was already completed.",
            "noCredit" => "No reset credits available.", _ => "No quota window is eligible for reset."
        };
        try { await Check(account); }
        catch (Exception) { message += " Refresh failed; use Refresh to update quota."; }
        status.Text = message;
        MessageBox.Show(this, message, "Reset credit", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    private async Task WarmUp(Account account)
    {
        if (busy || AccountBusy(account) || !warming.Add(account.Id)) return;
        RenderAccount(account);
        try
        {
            var path = profiles.Validate(account, settings); var deps = dependencies; var token = lifetime.Token;
            await Task.Run(() => new WarmUpService().RunAsync(deps, path, token), token);
            if (!token.IsCancellationRequested && !busy) status.Text = "Warm up completed: " + account.DisplayName + " · OK";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!lifetime.IsCancellationRequested) MessageBox.Show(this, ex.Message, "Warm up", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { warming.Remove(account.Id); RenderAccount(account); }
    }
    private Task Delete(Account account)
    {
        var path = profiles.Validate(account, settings);
        if (AccountHasSessions(account.Id)) throw new IOException("Stop this account's CLI sessions before deleting it.");
        if (MessageBox.Show(this, $"Delete {account.DisplayName}?\n\n{path}\n\nAuthentication, config and private data will be deleted.\nDefault Codex shared sessions will NOT be deleted.\n\nClose all terminals for this account before continuing.",
            "Delete account", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return Task.CompletedTask;
        profiles.Delete(account, settings); accounts.Accounts.Remove(account);
        try { repository.SaveAccounts(accounts); } catch { accounts.Accounts.Add(account); throw; }
        logger.Write("delete", path); status.Text = "Account deleted; shared sessions preserved."; return Task.CompletedTask;
    }
    private Task OpenFolder(Account account)
    {
        var path = PathSafety.Profile(repository.Root, account); PathSafety.OrdinaryDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { path } }); return Task.CompletedTask;
    }
    private Task Apply(Account account)
    {
        if (AccountHasSessions(account.Id)) throw new IOException("Stop this account's CLI sessions before Apply to prevent concurrent authentication updates.");
        if (MessageBox.Show(this, $"Apply {account.DisplayName}'s login to default Codex?\n\nClose Codex App and terminals using the default login before continuing. Reopen Codex App after Apply.\n\nTarget: {settings.DefaultCodexHome}\nOnly auth.json is replaced; config and sessions are preserved.",
            "Apply login", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return Task.CompletedTask;
        profiles.ApplyAuthentication(account, settings);
        logger.Write("apply-auth", settings.DefaultCodexHome);
        status.Text = "Applied " + account.DisplayName + ". Reopen Codex App to use this login.";
        return Task.CompletedTask;
    }
    private async Task SettingsDialog()
    {
        await RefreshDependencies(); using var dialog = new AdvancedSettingsForm(settings, dependencies);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var updated = dialog.Settings;
        if (accounts.Accounts.Count > 0 && !PathSafety.Same(updated.DefaultCodexHome, settings.DefaultCodexHome))
            throw new IOException("Safely delete managed accounts before changing the default Codex Home.");
        _ = profiles.SharedTarget(updated); PathSafety.OrdinaryDirectory(updated.WorkingDirectory);
        repository.SaveSettings(updated); settings = updated; status.Text = "Settings saved.";
    }
    private bool AccountHasSessions(string id) => launcher.IsRunning(id)
        || sessionRuntime.List().Any(r => r.AccountId == id && SessionRuntime.Alive(r));
}
