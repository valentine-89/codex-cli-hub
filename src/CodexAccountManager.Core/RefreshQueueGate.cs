namespace CodexAccountManager.Core;

public sealed class RefreshQueueGate
{
    public string? ActiveAccountId { get; private set; }
    private DateTimeOffset nextStart;
    public void DelayStart(DateTimeOffset now) => nextStart = now.AddSeconds(2);
    public bool TryStart(string accountId, DateTimeOffset now)
    {
        if (ActiveAccountId is not null || now < nextStart) return false;
        ActiveAccountId = accountId; return true;
    }
    public void Complete(DateTimeOffset now) { ActiveAccountId = null; DelayStart(now); }
}
