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

public interface IQuotaProvider { string GetDisplay(Account account); }
public sealed class UnavailableQuotaProvider : IQuotaProvider
{
    public string GetDisplay(Account account) => "Not available";
}
