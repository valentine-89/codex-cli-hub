namespace CodexAccountManager.Core;

public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public string ProfilePath { get; set; } = "";
    public bool SharedSessions { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string LoginStatus { get; set; } = "Not checked";
    public string Note { get; set; } = "";
    public QuotaSnapshot? Quota { get; set; }
    public string? QuotaError { get; set; }
}

public sealed class AccountDocument
{
    public int Version { get; set; } = 1;
    public List<Account> Accounts { get; set; } = [];
}

public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    public string DefaultCodexHome { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    public string WorkingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

public sealed class QuotaSnapshot
{
    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<QuotaLine> Lines { get; set; } = [];
}
public sealed record QuotaLine(string Title, double? RemainingPercent, long? ResetsAt, string? Detail = null)
{
    public string Display()
    {
        var value = RemainingPercent is double remaining ? $"còn {remaining:0.#}%" : Detail ?? "Không có dữ liệu";
        string reset = "";
        if (ResetsAt is long seconds)
        {
            try { reset = " · reset " + DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().ToString("dd/MM HH:mm"); }
            catch (ArgumentOutOfRangeException) { reset = " · reset không xác định"; }
        }
        return $"{Title}: {value}{reset}";
    }
}
