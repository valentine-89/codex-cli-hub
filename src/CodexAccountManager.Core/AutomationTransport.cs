using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace CodexAccountManager.Core;

// No TCP listener, browser access, terminal input injection, or credentials on the wire.
public sealed class AutomationPipe : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource lifetime = new();
    private readonly string name;
    private readonly Func<string, JsonElement, Task<object>> handle;
    private Task? listener;
    public AutomationPipe(string name, Func<string, JsonElement, Task<object>> handle)
    { this.name = name; this.handle = handle; }
    public static string Name(string root, string suffix = "manager")
    {
        var identity = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("Windows user unavailable.");
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity + "|" + PathSafety.Canonical(root).ToUpperInvariant()));
        return "CodexAccountManager-" + Convert.ToHexString(bytes)[..24] + "-" + suffix;
    }
    public void Start() => listener = Task.Run(Listen);
    private async Task Listen()
    {
        while (!lifetime.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 10, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(lifetime.Token); }
            catch (OperationCanceledException) { pipe.Dispose(); break; }
            _ = Serve(pipe);
        }
    }
    private async Task Serve(NamedPipeServerStream pipe)
    {
        using (pipe)
        using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(90));
                var line = await ReadLine(reader, deadline.Token);
                if (line is null) return;
                using var request = JsonDocument.Parse(line);
                var method = request.RootElement.GetProperty("method").GetString()!;
                var args = request.RootElement.TryGetProperty("arguments", out var value) ? value.Clone() : JsonSerializer.SerializeToElement(new { });
                object response;
                try { response = new { result = await handle(method, args) }; }
                catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or TimeoutException
                    or System.ComponentModel.Win32Exception or JsonException or OperationCanceledException or KeyNotFoundException)
                { response = new { error = AutomationErrors.Safe(ex) }; }
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException) { }
        }
    }
    public static async Task<JsonElement> Call(string name, string method, object arguments, int timeoutSeconds = 60)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.ConnectAsync(3000, timeout.Token); }
        catch (TimeoutException) { throw new IOException("Manager/session host unavailable. Start Account Manager and retry."); }
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { method, arguments }, Json));
        var line = await ReadLine(reader, timeout.Token) ?? throw new IOException("Host closed before returning a result. Re-read status before retrying mutations.");
        using var response = JsonDocument.Parse(line);
        if (response.RootElement.TryGetProperty("error", out var error)) throw new IOException(error.GetString());
        return response.RootElement.GetProperty("result").Clone();
    }
    public static async Task<string?> ReadLine(StreamReader reader, CancellationToken token)
    {
        var line = new StringBuilder();
        var one = new char[1];
        while (await reader.ReadAsync(one.AsMemory(), token) > 0)
        {
            if (one[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= 1024 * 1024) throw new IOException("Protocol message exceeds 1 MiB.");
            line.Append(one[0]);
        }
        return line.Length == 0 ? null : line.ToString();
    }
    public void Dispose() { lifetime.Cancel(); }
}

public static class AutomationErrors
{
    public static string Safe(Exception ex) => ex switch
    {
        JsonException or ArgumentException => "Invalid arguments or malformed protocol data.",
        System.ComponentModel.Win32Exception => "Windows process operation failed; re-read session status.",
        OperationCanceledException => "Operation timed out or cancelled; re-read status before retrying.",
        _ => ex.Message.Length > 500 ? ex.Message[..500] : ex.Message
    };
}
