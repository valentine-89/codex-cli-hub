using System.Text.Json;

namespace CodexAccountManager.Core;

public static class ManagerMcp
{
    private static object Schema(string[] required, params (string Name, string Type, string Description)[] properties)
        => new { type = "object", additionalProperties = false, required,
            properties = properties.ToDictionary(p => p.Name, p => (object)(p.Type == "array"
                ? new { type = p.Type, description = p.Description, items = new { type = "string" } }
                : (object)new { type = p.Type, description = p.Description })) };
    private static object Tool(string name, string description, object schema, bool readOnly = false)
        => new { name, description, inputSchema = schema, annotations = new { readOnlyHint = readOnly, destructiveHint = !readOnly, openWorldHint = false } };
    public static object[] Tools =>
    [
        Tool("manager_health", "Check the local Manager bridge without opening windows.", Schema([]), true),
        Tool("accounts_list", "List account IDs, order and cached quota. Notes and authentication are never returned.", Schema([]), true),
        Tool("account_refresh", "Read current account quota without inference or reset credits.", Schema(["accountId"], ("accountId", "string", "Full account ID from accounts_list"))),
        Tool("projects_list", "Read stored projects and Codex threads without resuming or starting inference.", Schema(["accountId"], ("accountId", "string", "Full account ID")), true),
        Tool("sessions_list", "List Manager-owned managed CLI hosts and discover its existing terminal launches.", Schema([]), true),
        Tool("session_read", "Read bounded session state, quota, latest message and pending approvals.", Schema(["sessionId"], ("sessionId", "string", "Manager session ID, not Codex thread ID")), true),
        Tool("session_start", "Open an interactive Codex terminal by default. Supply text as its initial prompt; visible=false uses a background managed host.", Schema(["accountId", "cwd"], ("accountId", "string", "Full account ID"), ("cwd", "string", "Existing absolute working directory"), ("text", "string", "Optional initial prompt"), ("visible", "boolean", "Show interactive terminal; default true"))),
        Tool("session_resume", "Resume the same stored Codex thread under an isolated account; refuses duplicate owners.", Schema(["accountId", "cwd", "threadId"], ("accountId", "string", "Full account ID"), ("cwd", "string", "Existing absolute working directory"), ("threadId", "string", "Stored Codex thread UUID"), ("text", "string", "Optional continuation prompt"), ("visible", "boolean", "Show interactive terminal; default true"))),
        Tool("session_take_control", "Stop a Manager terminal and transfer its SAME stored thread to the managed CLI protocol. Interrupts current work; use only when authorized.", Schema(["sessionId"], ("sessionId", "string", "Manager session ID"), ("text", "string", "Optional continuation prompt"))),
        Tool("session_switch_account", "Verify quota first, stop the current owner, then resume the SAME thread under another account. Never consumes reset credits.", Schema(["sessionId", "accountId"], ("sessionId", "string", "Current Manager session ID"), ("accountId", "string", "Target full account ID"), ("text", "string", "Optional continuation prompt"), ("excludedAccountIds", "array", "Accounts forbidden by caller policy"), ("excludeLastAccount", "boolean", "Also exclude last account in saved order"), ("minimumRemainingPercent", "number", "Minimum verified 5h quota; default20"), ("onlyIfQuotaExhausted", "boolean", "Require exhausted source quota; default true"), ("visible", "boolean", "Show interactive terminal; default true"))),
        Tool("session_open_terminal", "Open the same thread/account in an interactive terminal; stops its background owner first. Existing live terminal is reused. Interrupts active work; use only when authorized.", Schema(["sessionId"], ("sessionId", "string", "Manager session ID"), ("text", "string", "Optional initial prompt; omit to wait for keyboard input"))),
        Tool("session_send", "Send a new prompt to an idle managed CLI session; refuses active turns.", Schema(["sessionId", "text"], ("sessionId", "string", "Manager session ID"), ("text", "string", "User-authorized prompt"))),
        Tool("session_steer", "Append authorized guidance to an active managed turn.", Schema(["sessionId", "text"], ("sessionId", "string", "Manager session ID"), ("text", "string", "User-authorized guidance"))),
        Tool("session_interrupt", "Interrupt a managed turn and retain its thread for later continuation.", Schema(["sessionId"], ("sessionId", "string", "Manager session ID"))),
        Tool("session_stop", "Stop only the verified Manager-owned CLI host/terminal. History and files remain.", Schema(["sessionId"], ("sessionId", "string", "Manager session ID"))),
        Tool("session_reply", "Answer an explicit pending command/file approval or user-input request. Never auto-approves.", Schema(["sessionId", "requestId", "result"], ("sessionId", "string", "Manager session ID"), ("requestId", "string", "Request ID from session_read"), ("result", "object", "Explicit result: decision accept/acceptForSession/decline/cancel, or answers object")))
    ];
    public static async Task Run(TextReader input, TextWriter output, Func<string, JsonElement, Task<JsonElement>> call)
    {
        var initialized = false;
        while (await input.ReadLineAsync() is { } line)
        {
            JsonElement id = default;
            object response;
            try
            {
                if (line.Length > 1024 * 1024) throw new JsonException();
                using var doc = JsonDocument.Parse(line);
                var request = doc.RootElement;
                if (!request.TryGetProperty("id", out id)) continue;
                id = id.Clone();
                var method = request.GetProperty("method").GetString();
                object result;
                if (method == "initialize")
                {
                    if (initialized) throw new IOException("Already initialized.");
                    initialized = true;
                    // Stdio framing is version-independent. Echo the client's negotiated
                    // protocol version; only the stable tools capability is advertised.
                    var version = request.GetProperty("params").GetProperty("protocolVersion").GetString();
                    result = new { protocolVersion = version, capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "codex-account-manager", version = "1.11.2" } };
                }
                else if (method == "ping") result = new { };
                else if (!initialized) throw new IOException("Not initialized.");
                else if (method == "tools/list") result = new { tools = Tools };
                else if (method == "tools/call")
                {
                    var parameters = request.GetProperty("params");
                    var name = parameters.GetProperty("name").GetString()!;
                    var args = parameters.TryGetProperty("arguments", out var value) ? value : JsonSerializer.SerializeToElement(new { });
                    try
                    {
                        Validate(name, args);
                        var valueResult = await call(name, args);
                        result = new { content = new[] { new { type = "text", text = valueResult.GetRawText() } }, structuredContent = valueResult, isError = false };
                    }
                    catch (Exception ex) when (ex is IOException or ArgumentException or JsonException or TimeoutException or OperationCanceledException)
                    { result = new { content = new[] { new { type = "text", text = AutomationErrors.Safe(ex) } }, isError = true }; }
                }
                else { response = new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Method not found" } }; await Write(response); continue; }
                response = new { jsonrpc = "2.0", id, result };
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IOException)
            { response = new { jsonrpc = "2.0", id = id.ValueKind == JsonValueKind.Undefined ? (object?)null : id, error = new { code = -32600, message = "Invalid request" } }; }
            await Write(response);
        }
        async Task Write(object value) { await output.WriteLineAsync(JsonSerializer.Serialize(value, AutomationPipe.Json)); await output.FlushAsync(); }
    }
    public static void Validate(string name, JsonElement args)
    {
        var tool = Tools.Select(t => JsonSerializer.SerializeToElement(t)).SingleOrDefault(t => t.GetProperty("name").GetString() == name);
        if (tool.ValueKind == JsonValueKind.Undefined) throw new IOException("Unknown tool.");
        if (args.ValueKind != JsonValueKind.Object) throw new IOException("Expected an arguments object.");
        var schema = tool.GetProperty("inputSchema");
        foreach (var key in schema.GetProperty("required").EnumerateArray())
            if (!args.TryGetProperty(key.GetString()!, out _)) throw new IOException("Missing required argument: " + key.GetString());
        foreach (var arg in args.EnumerateObject())
        {
            if (!schema.GetProperty("properties").TryGetProperty(arg.Name, out var property)) throw new IOException("Unknown argument: " + arg.Name);
            var expected = property.GetProperty("type").GetString();
            var valid = expected switch { "string" => arg.Value.ValueKind == JsonValueKind.String, "object" => arg.Value.ValueKind == JsonValueKind.Object,
                "array" => arg.Value.ValueKind == JsonValueKind.Array && arg.Value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String),
                "number" => arg.Value.ValueKind == JsonValueKind.Number, "boolean" => arg.Value.ValueKind is JsonValueKind.True or JsonValueKind.False, _ => false };
            if (!valid) throw new IOException("Wrong argument type: " + arg.Name);
        }
    }
}
