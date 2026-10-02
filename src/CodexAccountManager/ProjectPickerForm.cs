using CodexAccountManager.Core;

namespace CodexAccountManager;

public sealed class ProjectPickerForm : Form
{
    private readonly TextBox search = new() { Dock = DockStyle.Fill, PlaceholderText = "Search folders or chat titles…" };
    private readonly ListBox projects = List();
    private readonly ListBox projectChats = List();
    private readonly ListBox standaloneChats = List();
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly Label status = Theme.Label("Loading projects and chats…");
    private readonly Label detail = Theme.Label("");
    private readonly Button resume = Theme.Button("Resume chat", true, 120);
    private readonly Button create = Theme.Button("New chat", width: 120);
    private readonly Button browse = Theme.Button("Browse…", width: 100);
    private readonly Button refresh = Theme.Button("Refresh", width: 90);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Func<CancellationToken, Task<WorkspaceCatalog>> load;
    private bool resourcesDisposed;
    private WorkspaceCatalog catalog = new([], []);
    public CliSelection? Selection { get; private set; }
    public Task Ready { get; private set; } = Task.CompletedTask;

    public ProjectPickerForm(string accountName, Func<CancellationToken, Task<WorkspaceCatalog>> load)
    {
        this.load = load;
        Theme.SetupDialog(this, "Open CLI · " + accountName, new Size(860, 510));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22, 16, 22, 16), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Scale(38)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Scale(38)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Scale(32)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Scale(38)));
        var projectTab = new TabPage("Projects") { Padding = new Padding(8), BackColor = Theme.Background };
        var chatTab = new TabPage("Chats without project") { Padding = new Padding(8), BackColor = Theme.Background };
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        columns.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.Scale(28))); columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        columns.Controls.Add(Theme.Label("Project folders", true), 0, 0); columns.Controls.Add(Theme.Label("Chats in selected folder", true), 1, 0);
        columns.Controls.Add(projects, 0, 1); columns.Controls.Add(projectChats, 1, 1);
        projectTab.Controls.Add(columns); chatTab.Controls.Add(standaloneChats); tabs.TabPages.AddRange([projectTab, chatTab]);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var cancel = Theme.Button("Cancel"); cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.AddRange([resume, create, browse, refresh, cancel]);
        layout.Controls.Add(search, 0, 0); layout.Controls.Add(tabs, 0, 1); layout.Controls.Add(detail, 0, 2);
        layout.Controls.Add(status, 0, 3); layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout); AcceptButton = resume; CancelButton = cancel;
        search.TextChanged += (_, _) => Filter();
        projects.SelectedIndexChanged += (_, _) => FilterProjectChats();
        projectChats.SelectedIndexChanged += (_, _) => UpdateActions();
        standaloneChats.SelectedIndexChanged += (_, _) => UpdateActions();
        tabs.SelectedIndexChanged += (_, _) => UpdateActions();
        resume.Click += (_, _) => ResumeChat();
        projectChats.DoubleClick += (_, _) => ResumeChat(); standaloneChats.DoubleClick += (_, _) => ResumeChat();
        create.Click += (_, _) => NewChat(); projects.DoubleClick += (_, _) => NewChat();
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "Select project folder", UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                Finish(new(dialog.SelectedPath, projects.SelectedItem is SessionProject { Exists: false }
                    && Guid.TryParse(SelectedChat?.Id, out _) ? SelectedChat!.Id : null));
        };
        refresh.Click += (_, _) => Ready = LoadCatalog();
        Shown += (_, _) => Ready = LoadCatalog();
        FormClosing += (_, _) => { if (!resourcesDisposed) cancellation.Cancel(); };
        UpdateActions();
    }

    private static ListBox List() => new() { Dock = DockStyle.Fill, IntegralHeight = false,
        BorderStyle = BorderStyle.FixedSingle, HorizontalScrollbar = true };
    private bool WithoutProject => tabs.SelectedIndex == 1;
    private CatalogSession? SelectedChat => (WithoutProject ? standaloneChats : projectChats).SelectedItem as CatalogSession;

    private async Task LoadCatalog()
    {
        refresh.Enabled = false; status.Text = "Loading projects and chats…";
        try
        {
            var token = cancellation.Token;
            var result = await Task.Run(() => load(token), token);
            if (IsDisposed || cancellation.IsCancellationRequested) return;
            catalog = result; Filter();
            status.Text = result.Warning ?? $"{catalog.Projects.Count} folders · {catalog.Sessions.Count(s => s.IsProjectless)} chats without project";
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!IsDisposed) status.Text = "Could not read sessions. Browse for a folder or start a chat without project."; }
        finally { if (!IsDisposed) refresh.Enabled = true; }
    }

    private void Filter()
    {
        var previous = (projects.SelectedItem as SessionProject)?.Directory;
        var term = search.Text.Trim();
        projects.BeginUpdate(); projects.Items.Clear();
        foreach (var project in catalog.Projects.Where(p => p.Directory.Contains(term, StringComparison.OrdinalIgnoreCase)
            || catalog.Sessions.Any(s => !s.IsProjectless && Same(s.WorkingDirectory, p.Directory) && Matches(s, term))))
            projects.Items.Add(project);
        if (projects.Items.Count > 0)
            projects.SelectedItem = projects.Items.Cast<SessionProject>().FirstOrDefault(p => Same(p.Directory, previous)) ?? projects.Items[0];
        projects.EndUpdate();
        Fill(standaloneChats, catalog.Sessions.Where(s => s.IsProjectless && Matches(s, term)));
        FilterProjectChats();
    }
    private void FilterProjectChats()
    {
        var project = projects.SelectedItem as SessionProject;
        var term = search.Text.Trim();
        Fill(projectChats, catalog.Sessions.Where(s => !s.IsProjectless && Same(s.WorkingDirectory, project?.Directory)
            && (project!.Directory.Contains(term, StringComparison.OrdinalIgnoreCase) || Matches(s, term))));
        UpdateActions();
    }
    private static bool Same(string? a, string? b) => a is not null && b is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private static bool Matches(CatalogSession s, string term) => s.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
        || s.Id.Contains(term, StringComparison.OrdinalIgnoreCase);
    private static void Fill(ListBox list, IEnumerable<CatalogSession> sessions)
    {
        var previous = (list.SelectedItem as CatalogSession)?.Id;
        list.BeginUpdate(); list.Items.Clear();
        foreach (var session in sessions.OrderByDescending(s => s.UpdatedUtc)) list.Items.Add(session);
        if (list.Items.Count > 0) list.SelectedItem = list.Items.Cast<CatalogSession>().FirstOrDefault(s => s.Id == previous) ?? list.Items[0];
        list.EndUpdate();
    }
    private void UpdateActions()
    {
        var project = projects.SelectedItem as SessionProject;
        create.Enabled = WithoutProject || project is { Exists: true };
        resume.Enabled = Guid.TryParse(SelectedChat?.Id, out _) && (WithoutProject || project is { Exists: true });
        browse.Visible = !WithoutProject;
        detail.Text = WithoutProject ? "New chats use Documents\\Codex\\YYYY-MM-DD\\new-chat (work / outputs)."
            : project is null ? "Select a project folder or use Browse."
            : project.Exists ? project.Directory : "Folder unavailable: " + project.Directory + " · Browse to its current location.";
    }
    private void NewChat()
    {
        if (WithoutProject) Finish(new(null, IsProjectless: true));
        else if (projects.SelectedItem is SessionProject { Exists: true } project) Finish(new(project.Directory));
    }
    private void ResumeChat()
    {
        if (resume.Enabled && SelectedChat is { } session)
            Finish(new(session.WorkingDirectory, session.Id, session.IsProjectless));
    }
    private void Finish(CliSelection selection)
    {
        if (!selection.IsProjectless && (WorkspaceCatalogService.TryNormalize(selection.Directory) is not { } path || !Directory.Exists(path)))
        {
            MessageBox.Show(this, "The project folder is no longer available. Refresh or Browse to its current location.",
                "Cannot open folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Selection = selection; DialogResult = DialogResult.OK;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !resourcesDisposed) { resourcesDisposed = true; cancellation.Cancel(); cancellation.Dispose(); }
        base.Dispose(disposing);
    }
}
