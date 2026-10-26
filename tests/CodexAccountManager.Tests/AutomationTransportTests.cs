using System.IO.Pipes;
using System.Text;
using CodexAccountManager.Core;

internal static class AutomationTransportTests
{
    public static void Register(List<(string Name, Func<Task> Run)> tests, string root)
    {
        string Name() => AutomationPipe.Name(Path.Combine(root, Guid.NewGuid().ToString("N")), "transport");
        tests.Add(("pipe survives its connection limit and accepts later requests", async () =>
        {
            var name = Name(); var entered = 0;
            var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var host = new AutomationPipe(name, async (_, _) =>
            {
                if (Interlocked.Increment(ref entered) == 10) full.TrySetResult();
                await release.Task;
                return new { ok = true };
            });
            host.Start();
            var requests = Enumerable.Range(0, 10).Select(_ => AutomationPipe.Call(name, "read", new { })).ToArray();
            try
            {
                await full.Task.WaitAsync(TimeSpan.FromSeconds(8));
                await Task.Delay(100); // Let the listener reach the full-capacity branch.
                var extra = AutomationPipe.Call(name, "read", new { });
                release.TrySetResult();
                await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(8));
                await extra;
                await AutomationPipe.Call(name, "read", new { });
                if (entered != 12) throw new Exception("Listener stopped after reaching capacity.");
            }
            finally { release.TrySetResult(); }
        }));
        tests.Add(("missing bridge recovers before sending exactly one request", async () =>
        {
            var name = Name(); var starts = 0; var calls = 0;
            using var host = new AutomationPipe(name, (_, _) => { calls++; return Task.FromResult<object>(new { ok = true }); });
            var client = new RecoveringAutomationClient(name, () => { starts++; host.Start(); return Task.CompletedTask; });
            await client.Call("mutation", new { });
            if (starts != 1 || calls != 1) throw new Exception("Missing host recovery lost or replayed the request.");
        }));
        tests.Add(("operation error never restarts a bridge or replays a mutation", async () =>
        {
            var name = Name(); var starts = 0; var calls = 0;
            using var host = new AutomationPipe(name, (_, _) => { calls++; throw new IOException("fixture operation failed"); });
            host.Start();
            var client = new RecoveringAutomationClient(name, () => { starts++; return Task.CompletedTask; });
            try { await client.Call("mutation", new { }); throw new Exception("Operation error was ignored."); }
            catch (IOException ex) when (ex.Message == "fixture operation failed") { }
            if (starts != 0 || calls != 1) throw new Exception("Operation was replayed.");
        }));
        tests.Add(("lost reply after delivery never triggers automatic replay", async () =>
        {
            var name = Name(); var starts = 0;
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var received = Task.Run(async () =>
            {
                using (pipe)
                {
                    await pipe.WaitForConnectionAsync();
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                    if (await reader.ReadLineAsync() is null) throw new Exception("Request not delivered.");
                    // Simulate completing a mutation and exiting before its reply.
                }
            });
            var client = new RecoveringAutomationClient(name, () => { starts++; return Task.CompletedTask; });
            try { await client.Call("mutation", new { }); throw new Exception("Lost reply accepted."); }
            catch (IOException ex) when (ex is not AutomationHostUnavailableException) { }
            await received.WaitAsync(TimeSpan.FromSeconds(8));
            if (starts != 0) throw new Exception("Delivered request was retried.");
        }));
    }
}
