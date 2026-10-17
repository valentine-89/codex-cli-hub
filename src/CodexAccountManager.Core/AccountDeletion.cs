namespace CodexAccountManager.Core;

// Explicit offline maintenance entry point. No account-deletion MCP tool is exposed.
public sealed class AccountDeletion
{
    private readonly AccountRepository repository;
    private readonly Action ensureIdle;
    public AccountDeletion(AccountRepository repository) : this(repository, SessionStoreMaintenance.EnsureCodexClosed) { }
    internal AccountDeletion(AccountRepository repository, Action ensureIdle)
    { this.repository = repository; this.ensureIdle = ensureIdle; }

    public void Delete(string id, string expectedName)
    {
        if (!Guid.TryParseExact(id, "N", out _) || string.IsNullOrWhiteSpace(expectedName))
            throw new IOException("Account deletion requires an exact account ID and expected name.");
        using var appLock = repository.AcquireLock();
        var accounts = repository.LoadAccounts();
        var account = accounts.Accounts.SingleOrDefault(a => a.Id == id)
            ?? throw new IOException("Account was not found. No account was deleted.");
        if (!string.Equals(account.DisplayName, expectedName, StringComparison.Ordinal))
            throw new IOException("Account name changed. No account was deleted.");
        var settings = repository.LoadSettings();
        new CodexProfileService(repository.Root, new JunctionService(), ensureIdle).Delete(account, settings);
        accounts.Accounts.Remove(account);
        repository.SaveAccounts(accounts);
        new Logger(repository.Root).Write("delete:offline", PathSafety.Profile(repository.Root, account));
    }
}
