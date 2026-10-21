using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CodexPond;

/// <summary>
/// Small per-run stdio MCP server used only as Claude Code's documented permission prompt tool.
/// It receives no credentials and forwards each permission request to the parent Pond process.
/// </summary>
public static class ClaudePermissionMcpServer
{
    public static async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var method = ReadString(root, "method");
            var id = root.TryGetProperty("id", out var requestId) ? requestId.Clone() : (JsonElement?)null;
            if (id is null) continue;

            object? result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "codex-pond-permissions", version = "1.0.0" }
                },
                "tools/list" => new
                {
                    tools = new[]
                    {
                        new
                        {
                            name = "approve",
                            description = "Route a Claude Code tool permission request to the Codex Pond user. Return allow only for the current operation.",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new Dictionary<string, object>
                                {
                                    ["tool_name"] = new { type = "string" },
                                    ["tool_input"] = new { type = "object", additionalProperties = true },
                                    ["permission_suggestions"] = new { type = "array", items = new { type = "object", additionalProperties = true } },
                                    ["cwd"] = new { type = "string" },
                                    ["decision_reason"] = new { type = "string" }
                                },
                                required = new[] { "tool_name", "tool_input" },
                                additionalProperties = true
                            }
                        }
                    }
                },
                "tools/call" => await HandleToolCallAsync(root, cancellationToken).ConfigureAwait(false),
                _ => null
            };
            if (result is null)
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32601, message = "Method not found" }
                }));
                continue;
            }
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));
        }
    }

    private static async Task<object> HandleToolCallAsync(JsonElement root, CancellationToken cancellationToken)
    {
        var parameters = root.TryGetProperty("params", out var p) ? p : default;
        if (ReadString(parameters, "name") != "approve")
            return ToolError("不支持的 Pond 权限工具。");
        var arguments = parameters.TryGetProperty("arguments", out var args) ? args : default;
        var pipeName = Environment.GetEnvironmentVariable("CODEX_POND_APPROVAL_PIPE");
        if (string.IsNullOrWhiteSpace(pipeName)) return ToolError("Codex Pond 审批桥不可用；操作已拒绝。");

        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(8000, cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(arguments.GetRawText());
            var response = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            var allowed = false;
            if (!string.IsNullOrWhiteSpace(response))
            {
                using var responseDoc = JsonDocument.Parse(response);
                allowed = ReadString(responseDoc.RootElement, "decision") == "allow";
            }
            var toolInput = arguments.TryGetProperty("tool_input", out var originalInput)
                ? JsonSerializer.Deserialize<object>(originalInput.GetRawText())
                : new { };
            object decision;
            if (allowed)
                decision = new { behavior = "allow", updatedInput = toolInput, message = (string?)null };
            else
                decision = new { behavior = "deny", updatedInput = (object?)null, message = (string?)"用户在 Codex Pond 中拒绝了该操作。" };
            return new
            {
                content = new[] { new { type = "text", text = JsonSerializer.Serialize(decision, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }) } },
                isError = !allowed
            };
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            return ToolError("Codex Pond 审批桥无响应；操作已拒绝。");
        }
    }

    private static object ToolError(string message) => new { content = new[] { new { type = "text", text = message } }, isError = true };

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
