namespace CodexAccountManager.Core;

public static class QuotaPresentation
{
    public static bool WeeklyOnly(QuotaSnapshot? quota) => quota is not null
        && quota.Lines.Any(l => l.Duration == 10080) && !quota.Lines.Any(l => l.Duration == 300);
    public static bool Low(QuotaSnapshot? quota) => quota?.Lines.Any(l => l.Duration is 300 or 10080 && l.RemainingPercent is <= 10) == true;
    public static long? DueReset(Account account, DateTimeOffset now) => account.Quota?.Lines
        .Where(l => l.Duration is > 0 && l.ResetsAt is > 0 && l.ResetsAt <= now.ToUnixTimeSeconds()
            && l.ResetsAt > account.LastAutoRefreshReset && l.ResetsAt > account.Quota.FetchedAt.ToUnixTimeSeconds())
        .Select(l => l.ResetsAt).Max();
}
