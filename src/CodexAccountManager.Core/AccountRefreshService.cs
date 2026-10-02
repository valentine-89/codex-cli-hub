namespace CodexAccountManager.Core;

public sealed record AccountRefreshResult(string LoginStatus, QuotaSnapshot? Quota, string? QuotaError, Dependencies? Dependencies = null);

public sealed class AccountRefreshService
{
    public async Task<AccountRefreshResult> ReadAsync(string home, CancellationToken cancellationToken = default)
    {
        var dependencies = await DependencyDetector.DetectAsync(cancellationToken: cancellationToken);
        var login = await new CodexStatusService().CheckAsync(dependencies, home, cancellationToken);
        try { return new(login, await new CodexQuotaService().ReadAsync(dependencies, home, cancellationToken), null, dependencies); }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.Text.Json.JsonException)
        {
            return new(login, null, ex is TimeoutException ? "Quota request timed out" : "Could not read quota; check login and connection", dependencies);
        }
    }
}
