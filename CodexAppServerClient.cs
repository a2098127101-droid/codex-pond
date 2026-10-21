using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace CodexPond;

public enum ApprovalKind
{
    CommandExecution,
    FileChange,
    Permissions,
    ExecCommand,
    ApplyPatch,
    UserInput,
    McpElicitation,
    Unsupported
}

public sealed record CodexInputQuestion(string Id, string Header, string Prompt, IReadOnlyList<string> Options, bool IsSecret, bool IsOther);

public sealed record CodexApprovalRequest(JsonElement RequestId, string Method, JsonElement Parameters)
{
    public string? SessionId => ReadString(Parameters, "threadId");
    public string? TurnId => ReadString(Parameters, "turnId");

    public ApprovalKind Kind => Method switch
    {
        "item/commandExecution/requestApproval" => ApprovalKind.CommandExecution,
        "item/fileChange/requestApproval" => ApprovalKind.FileChange,
        "item/permissions/requestApproval" => ApprovalKind.Permissions,
        "item/tool/requestUserInput" => ApprovalKind.UserInput,
        "mcpServer/elicitation/request" => ApprovalKind.McpElicitation,
        "execCommandApproval" => ApprovalKind.ExecCommand,
        "applyPatchApproval" => ApprovalKind.ApplyPatch,
        _ => ApprovalKind.Unsupported
    };

    public string Title => Kind switch
    {
        ApprovalKind.CommandExecution => "Codex 请求运行命令",
        ApprovalKind.FileChange => "Codex 请求确认文件变更",
        ApprovalKind.Permissions => "Codex 请求额外权限",
        ApprovalKind.ExecCommand => "Codex 请求命令授权",
        ApprovalKind.ApplyPatch => "Codex 请求应用补丁",
        ApprovalKind.UserInput => "Codex 需要你补充信息",
        ApprovalKind.McpElicitation => "外部工具请求输入",
        _ => $"Codex 请求暂不支持的交互：{Method}"
    };

    public bool CanGrantOnce
    {
        get
        {
            if (Kind == ApprovalKind.UserInput) return Questions.Count > 0;
            if (Kind == ApprovalKind.CommandExecution)
                return AvailableDecisions.Contains("accept", StringComparer.Ordinal);
            return Kind is ApprovalKind.FileChange or ApprovalKind.Permissions or ApprovalKind.ExecCommand or ApprovalKind.ApplyPatch;
        }
    }

    public IReadOnlyList<string> AvailableDecisions
    {
        get
        {
            if (Parameters.ValueKind != JsonValueKind.Object ||
                !Parameters.TryGetProperty("availableDecisions", out var choices) || choices.ValueKind != JsonValueKind.Array)
                return [];
            return choices.EnumerateArray()
                .Where(choice => choice.ValueKind == JsonValueKind.String)
                .Select(choice => choice.GetString()!)
                .ToArray();
        }
    }

    public string? DeclineDecision => Kind switch
    {
        ApprovalKind.CommandExecution when AvailableDecisions.Contains("decline", StringComparer.Ordinal) => "decline",
        ApprovalKind.CommandExecution when AvailableDecisions.Contains("cancel", StringComparer.Ordinal) => "cancel",
        ApprovalKind.CommandExecution => null,
        ApprovalKind.UserInput or ApprovalKind.Unsupported => null,
        _ => "decline"
    };

    public string DeclineLabel => DeclineDecision == "cancel" ? "拒绝并停止本轮" : "拒绝请求";

    public string Details
    {
        get
        {
            if (Kind == ApprovalKind.UserInput) return "请填写下方问题。";
            if (Parameters.ValueKind != JsonValueKind.Object) return "请查看 Codex 请求并决定如何处理。";
            if (Kind == ApprovalKind.Permissions && Parameters.TryGetProperty("permissions", out var requestedPermissions))
            {
                var reason = ReadString(Parameters, "reason");
                return $"原因：{reason ?? "未提供"}{Environment.NewLine}本轮权限：{requestedPermissions.GetRawText()}";
            }
            foreach (var key in new[] { "command", "reason", "cwd", "serverName" })
            {
                if (Parameters.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) return text!;
                }
            }
            return "Codex 正在等待你的决定。";
        }
    }

    public IReadOnlyList<CodexInputQuestion> Questions
    {
        get
        {
            if (Parameters.ValueKind != JsonValueKind.Object || !Parameters.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
                return [];
            var result = new List<CodexInputQuestion>();
            foreach (var question in questions.EnumerateArray())
            {
                var id = ReadString(question, "id");
                var prompt = ReadString(question, "question");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(prompt)) continue;
                var options = new List<string>();
                if (question.TryGetProperty("options", out var optionsElement) && optionsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var option in optionsElement.EnumerateArray())
                    {
                        var label = ReadString(option, "label");
                        if (!string.IsNullOrWhiteSpace(label)) options.Add(label!);
                    }
                }
                result.Add(new CodexInputQuestion(
                    id!,
                    ReadString(question, "header") ?? "补充信息",
                    prompt!,
                    options,
                    question.TryGetProperty("isSecret", out var secret) && secret.ValueKind == JsonValueKind.True,
                    question.TryGetProperty("isOther", out var other) && other.ValueKind == JsonValueKind.True));
            }
            return result;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public sealed record CodexServerEvent(
    string Method,
    string? ThreadId = null,
    string? TurnId = null,
    string? Text = null,
    string? Status = null);

public interface ICodexAppServerAdapter : IAsyncDisposable
{
    event Action<CodexServerEvent>? EventReceived;
    event Action<CodexApprovalRequest>? RequestReceived;
    event Action<string>? ConnectionLost;
    bool IsRunning { get; }
    Task<string> StartThreadAsync(string executable, string cwd, string? resumeThreadId = null);
    Task<string> StartTurnAsync(string threadId, string prompt, string cwd);
    Task InterruptTurnAsync(string threadId, string turnId);
    Task RespondToApprovalAsync(CodexApprovalRequest request, bool approve);
    Task RespondToUserInputAsync(CodexApprovalRequest request, IReadOnlyDictionary<string, string> answers);
    Task RejectUnsupportedRequestAsync(CodexApprovalRequest request);
}

public sealed class CodexAppServerAdapter : ICodexAppServerAdapter, IAgentTaskLauncher
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly IReadOnlyList<string>? _argumentOverride;
    private Process? _process;
    private Task? _readerTask;
    private string? _activeThreadId;
    private string? _activeTurnId;
    private long _nextId;
    private int _disposed;
    private int _lostRaised;

    public CodexAppServerAdapter(IReadOnlyList<string>? argumentOverride = null)
    {
        _argumentOverride = argumentOverride;
    }

    public event Action<CodexServerEvent>? EventReceived;
    public event Action<AgentEvent>? AgentEventReceived;
    public event Action<CodexApprovalRequest>? RequestReceived;
    public event Action<string>? ConnectionLost;

    public string ProviderId => AgentProviderIds.Codex;
    public AgentProviderCapabilities Capabilities { get; } = new(
        CanLaunchTasks: true,
        CanObserveExternalTasks: false,
        CanCancel: true,
        CanRespondToApprovals: true,
        CanAcceptUserInput: true);

    public bool IsRunning
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public async Task<string> StartThreadAsync(string executable, string cwd, string? resumeThreadId = null)
    {
        await EnsureServerAsync(executable, cwd).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(resumeThreadId))
        {
            var resumed = await SendRequestAsync("thread/resume", new { threadId = resumeThreadId }).ConfigureAwait(false);
            _activeThreadId = ReadString(resumed, "thread", "id") ?? resumeThreadId;
            _activeTurnId = null;
            return _activeThreadId;
        }

        var result = await SendRequestAsync("thread/start", new { cwd }).ConfigureAwait(false);
        var threadId = ReadString(result, "thread", "id");
        if (string.IsNullOrWhiteSpace(threadId))
            throw new InvalidOperationException("Codex App Server 没有返回 thread id。");
        _activeThreadId = threadId;
        _activeTurnId = null;
        return threadId;
    }

    public async Task<string> StartTurnAsync(string threadId, string prompt, string cwd)
    {
        _activeThreadId = threadId;
        _activeTurnId = null;
        var result = await SendRequestAsync("turn/start", new
        {
            threadId,
            cwd,
            input = new[] { new { type = "text", text = prompt } }
        }).ConfigureAwait(false);
        var turnId = ReadString(result, "turn", "id");
        var status = ReadString(result, "turn", "status");
        if (string.IsNullOrWhiteSpace(turnId) || !string.Equals(status, "inProgress", StringComparison.Ordinal))
            throw new InvalidOperationException("Codex App Server 的 turn/start 回包缺少有效 turn ID 或未处于 inProgress 状态。");
        _activeTurnId = turnId;
        return turnId;
    }

    public Task InterruptTurnAsync(string threadId, string turnId) =>
        SendRequestAsync("turn/interrupt", new { threadId, turnId });

    public async Task RespondToApprovalAsync(CodexApprovalRequest request, bool approve)
    {
        object result = request.Kind switch
        {
            ApprovalKind.CommandExecution when approve && !request.CanGrantOnce =>
                throw new InvalidOperationException("Codex 当前没有提供仅本轮允许选项。") ,
            ApprovalKind.CommandExecution =>
                new { decision = approve ? "accept" : request.DeclineDecision ?? throw new InvalidOperationException("Codex 没有提供可用的拒绝选项。") },
            ApprovalKind.FileChange =>
                new { decision = approve ? "accept" : "decline" },
            ApprovalKind.ExecCommand or ApprovalKind.ApplyPatch =>
                new { decision = approve ? (object)"approved" : new { denied = new { rejection = "用户在 Codex 池塘中拒绝了该请求。" } } },
            ApprovalKind.Permissions => new
            {
                permissions = approve && request.Parameters.TryGetProperty("permissions", out var requested)
                    ? (object)requested.Clone()
                    : new { fileSystem = (object?)null, network = (object?)null },
                scope = "turn"
            },
            ApprovalKind.McpElicitation when !approve =>
                new { action = "cancel", content = (object?)null },
            ApprovalKind.McpElicitation =>
                throw new NotSupportedException("此版本可以取消外部工具的输入请求，但不能安全构造其专用表单。"),
            ApprovalKind.UserInput =>
                throw new InvalidOperationException("请通过输入表单提交 Codex 所需的信息。"),
            _ => throw new NotSupportedException($"请关闭不支持的请求：{request.Method}")
        };
        await SendResponseAsync(request.RequestId, result).ConfigureAwait(false);
    }

    public Task RespondToUserInputAsync(CodexApprovalRequest request, IReadOnlyDictionary<string, string> answers)
    {
        if (request.Kind != ApprovalKind.UserInput)
            throw new InvalidOperationException("当前请求不是补充信息请求。");
        var responseAnswers = answers.ToDictionary(
            pair => pair.Key,
            pair => (object)new { answers = new[] { pair.Value } },
            StringComparer.Ordinal);
        return SendResponseAsync(request.RequestId, new { answers = responseAnswers });
    }

    public Task RejectUnsupportedRequestAsync(CodexApprovalRequest request) =>
        SendErrorResponseAsync(request.RequestId, -32601, $"Codex Pond 暂不支持此请求类型：{request.Method}");

    private async Task EnsureServerAsync(string executable, string cwd)
    {
        if (IsRunning) return;
        var previousProcess = _process;
        var previousReader = _readerTask;
        _process = null;
        _readerTask = null;
        if (previousProcess is not null)
        {
            previousProcess.Dispose();
            if (previousReader is not null)
            {
                try { await previousReader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception) { }
            }
        }
        Interlocked.Exchange(ref _lostRaised, 0);
        await StartServerAsync(executable, cwd).ConfigureAwait(false);
    }
    private async Task StartServerAsync(string executable, string cwd)
    {
        if (_process is not null) throw new InvalidOperationException("Codex App Server 已经启动。");
        var start = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(executable) ? "codex" : executable,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in _argumentOverride ?? ["app-server", "--stdio"])
            start.ArgumentList.Add(argument);

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process = process;
        process.Exited += (_, _) => RaiseConnectionLost(process, "Codex App Server 已退出。");
        if (!process.Start())
            throw new InvalidOperationException("无法启动 Codex App Server。");

        _ = DrainStandardErrorAsync(process);
        _readerTask = Task.Run(() => ReadLoopAsync(process));
        try
        {
            await SendRequestAsync("initialize", new
            {
                clientInfo = new { name = "codex-pond", title = "Codex Pond", version = "0.1.0" }
            }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await SendNotificationAsync("initialized", new { }).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Exchange(ref _lostRaised, 1);
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            if (_readerTask is not null)
            {
                try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception) { }
            }
            process.Dispose();
            if (ReferenceEquals(_process, process)) _process = null;
            _readerTask = null;
            throw;
        }
    }

    private async Task<JsonElement> SendRequestAsync(string method, object parameters, TimeSpan? timeout = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        await WriteMessageAsync(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        }).ConfigureAwait(false);

        if (timeout is null) return await completion.Task.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource(timeout.Value);
        try { return await completion.Task.WaitAsync(cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            throw new TimeoutException($"Codex App Server 请求超时：{method}");
        }
    }

    private Task SendNotificationAsync(string method, object parameters) =>
        WriteMessageAsync(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters
        });

    private async Task WriteMessageAsync(object message)
    {
        var process = _process;
        if (process is null || process.HasExited)
            throw new InvalidOperationException("Codex App Server 尚未连接。");
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SendResponseAsync(JsonElement requestId, object result) =>
        await WriteMessageAsync(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId,
            ["result"] = result
        }).ConfigureAwait(false);

    private async Task SendErrorResponseAsync(JsonElement requestId, int code, string message) =>
        await WriteMessageAsync(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId,
            ["error"] = new { code, message }
        }).ConfigureAwait(false);

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            while (!process.HasExited)
            {
                var line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                if (!ReferenceEquals(process, _process)) break;
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id) && !root.TryGetProperty("method", out _))
                    ResolveResponse(id, root);
                else if (root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String)
                {
                    var method = methodElement.GetString()!;
                    var parameters = root.TryGetProperty("params", out var value) ? value.Clone() : default;
                    if (root.TryGetProperty("id", out var requestId))
                    {
                        var request = new CodexApprovalRequest(requestId.Clone(), method, parameters);
                        RequestReceived?.Invoke(request);
                        AgentEventReceived?.Invoke(new AgentEvent(AgentEventKind.ApprovalRequired, ProviderId,
                            ReadString(parameters, "threadId"), ReadString(parameters, "turnId"),
                            request.Title, ProviderPayload: request));
                    }
                    else
                        HandleNotification(process, method, parameters);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ObjectDisposedException)
        {
            RaiseConnectionLost(process, ex.Message);
        }
        finally
        {
            if (ReferenceEquals(process, _process))
            {
                foreach (var pending in _pending.Values)
                    pending.TrySetException(new IOException("Codex App Server 连接已关闭。"));
                _pending.Clear();
                if (Volatile.Read(ref _disposed) == 0)
                    RaiseConnectionLost(process, "Codex App Server 连接已关闭。");
            }
        }
    }

    private void ResolveResponse(JsonElement id, JsonElement root)
    {
        if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var key) ||
            !_pending.TryRemove(key, out var completion)) return;

        if (root.TryGetProperty("error", out var error))
        {
            var message = ReadString(error, "message") ?? "App Server 返回错误。";
            completion.TrySetException(new InvalidOperationException(message));
        }
        else if (root.TryGetProperty("result", out var result))
            completion.TrySetResult(result.Clone());
        else
            completion.TrySetResult(default);
    }

    private void HandleNotification(Process sourceProcess, string method, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            EventReceived?.Invoke(new CodexServerEvent(method));
            if (TryMapAgentEvent(method, null, null, null, null, out var simpleEvent))
                AgentEventReceived?.Invoke(simpleEvent);
            return;
        }
        var threadId = ReadString(parameters, "threadId");
        var turn = parameters.TryGetProperty("turn", out var turnElement) ? turnElement : default;
        var turnId = ReadString(parameters, "turnId") ?? ReadString(turn, "id");
        if ((method is "turn/started" or "turn/completed" or "item/started" or "item/completed" or "item/agentMessage/delta") &&
            (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId)))
        {
            RaiseConnectionLost(sourceProcess, $"App Server 的 {method} 事件缺少 threadId 或 turnId，已停止处理未校验事件。");
            return;
        }
        var text = method == "item/agentMessage/delta" ? ReadString(parameters, "delta") : null;
        string? status = null;
        if (method == "turn/completed")
        {
            status = ReadString(turn, "status");
            if (string.IsNullOrWhiteSpace(status))
            {
                RaiseConnectionLost(sourceProcess, "App Server 的 turn/completed 事件缺少 turn.status，已停止处理未校验事件。");
                return;
            }
            text = string.Join(Environment.NewLine, ReadFinalMessages(turn));
        }
        EventReceived?.Invoke(new CodexServerEvent(method, threadId, turnId, text, status));
        if (TryMapAgentEvent(method, threadId, turnId, text, status, out var agentEvent))
        {
            if (agentEvent.Kind == AgentEventKind.Started)
            {
                if (string.Equals(agentEvent.SessionId, _activeThreadId, StringComparison.Ordinal))
                    _activeTurnId = agentEvent.TurnId;
            }
            AgentEventReceived?.Invoke(agentEvent);
        }
    }

    private bool TryMapAgentEvent(string method, string? sessionId, string? turnId, string? text, string? status, out AgentEvent agentEvent)
    {
        AgentEventKind? kind = method switch
        {
            "turn/started" => AgentEventKind.Started,
            "item/started" => AgentEventKind.StepStarted,
            "item/completed" => AgentEventKind.StepCompleted,
            "item/agentMessage/delta" => AgentEventKind.OutputDelta,
            "turn/completed" when string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) => AgentEventKind.Completed,
            "turn/completed" when string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase) => AgentEventKind.Cancelled,
            "turn/completed" => AgentEventKind.Failed,
            _ => null
        };
        if (kind is null)
        {
            agentEvent = default!;
            return false;
        }
        agentEvent = new AgentEvent(kind.Value, ProviderId, sessionId, turnId, text, status);
        return true;
    }

    private static IEnumerable<string> ReadFinalMessages(JsonElement turn)
    {
        if (turn.ValueKind != JsonValueKind.Object ||
            !turn.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in items.EnumerateArray())
        {
            if (ReadString(item, "type") == "agentMessage" &&
                item.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(text.GetString()))
                yield return text.GetString()!;
        }
    }

    private static string? ReadString(JsonElement element, params string[] path)
    {
        foreach (var part in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element))
                return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    private static async Task DrainStandardErrorAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) { }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private void RaiseConnectionLost(Process sourceProcess, string message)
    {
        if (!ReferenceEquals(sourceProcess, _process)) return;
        if (Volatile.Read(ref _disposed) != 0) return;
        if (Interlocked.Exchange(ref _lostRaised, 1) == 0)
        {
            ConnectionLost?.Invoke(message);
            AgentEventReceived?.Invoke(new AgentEvent(AgentEventKind.Disconnected, ProviderId,
                _activeThreadId, _activeTurnId, message, "disconnected"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        if (_readerTask is not null)
        {
            try { await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception) { }
        }
        _process?.Dispose();
        _writeLock.Dispose();
    }
}
