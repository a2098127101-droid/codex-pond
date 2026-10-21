using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexPond;

public sealed record WorkBuddyRunEvidence(
    bool InitializationSucceeded,
    bool DurableWorkflowObserved,
    bool VerifiedWorkflowCompletion,
    bool VerifiedWorkflowUnsuccessfulTerminal);

/// <summary>Conservative parser for Work Buddy terminal evidence; ignores unrelated nested health/status fields.</summary>
public static class WorkBuddyProtocolEvidence
{
    private static readonly string[] TerminalStates = ["completed", "succeeded", "success"];
    private static readonly string[] UnsuccessfulTerminalStates = ["failed", "error", "cancelled", "canceled", "aborted"];
    private static readonly string[] WorkflowObjectNames = ["workflow", "workflow_run", "workflowRun", "run"];

    public static bool IndicatesCompletedWorkflowStatus(string json, IReadOnlySet<string>? expectedWorkflowIds = null)
        => IsSuccessfulTerminal(ReadTerminalWorkflowStatus(json, expectedWorkflowIds));

    public static bool IndicatesUnsuccessfulWorkflowTerminal(string json, IReadOnlySet<string>? expectedWorkflowIds = null)
        => ReadTerminalWorkflowStatus(json, expectedWorkflowIds) is { } status &&
           UnsuccessfulTerminalStates.Contains(status, StringComparer.OrdinalIgnoreCase);

    public static string? ReadTerminalWorkflowStatus(string json, IReadOnlySet<string>? expectedWorkflowIds = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // Some wb_status versions expose the run ID and its status at the response root.
            if (HasDirectWorkflowRunId(root) && MatchesExpectedId(root, expectedWorkflowIds) && ReadDirectTerminalState(root) is { } rootStatus)
                return rootStatus;

            // Other versions nest the actual run under a named workflow object. Do not scan arbitrary
            // descendants: MCP/server health objects can also contain status="success".
            foreach (var name in WorkflowObjectNames)
            {
                if (TryGetPropertyIgnoreCase(root, name, out var workflow) && ReadTerminalWorkflowNode(workflow, expectedWorkflowIds) is { } workflowStatus)
                    return workflowStatus;
            }
            return null;
        }
        catch (JsonException) { return null; }
    }

    public static bool ContainsWorkflowRunId(string json)
        => ExtractWorkflowRunIds(json).Count > 0;

    public static IReadOnlySet<string> ExtractWorkflowRunIds(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            CollectWorkflowRunIds(document.RootElement, ids);
            return ids;
        }
        catch (JsonException)
        {
            return Regex.Matches(json, @"\bwf_[0-9a-f]{8}\b", RegexOptions.IgnoreCase)
                .Select(match => match.Value)
                .ToHashSet(StringComparer.Ordinal);
        }
    }

    private static string? ReadTerminalWorkflowNode(JsonElement node, IReadOnlySet<string>? expectedWorkflowIds)
    {
        if (node.ValueKind != JsonValueKind.Object) return null;
        if (HasDirectWorkflowRunId(node) && MatchesExpectedId(node, expectedWorkflowIds) && ReadDirectTerminalState(node) is { } directStatus)
            return directStatus;
        foreach (var name in WorkflowObjectNames)
            if (TryGetPropertyIgnoreCase(node, name, out var child) && ReadTerminalWorkflowNode(child, expectedWorkflowIds) is { } nestedStatus)
                return nestedStatus;
        return null;
    }

    private static bool MatchesExpectedId(JsonElement node, IReadOnlySet<string>? expectedWorkflowIds)
    {
        if (expectedWorkflowIds is null) return true;
        var id = ReadDirectWorkflowRunId(node);
        return id is not null && expectedWorkflowIds.Contains(id);
    }

    private static string? ReadDirectWorkflowRunId(JsonElement node)
    {
        if (TryGetPropertyIgnoreCase(node, "workflow_run_id", out var snake) && IsNonEmptyString(snake)) return snake.GetString();
        if (TryGetPropertyIgnoreCase(node, "workflowRunId", out var camel) && IsNonEmptyString(camel)) return camel.GetString();
        return null;
    }

    private static bool HasDirectWorkflowRunId(JsonElement node) =>
        TryGetPropertyIgnoreCase(node, "workflow_run_id", out var snake) && IsNonEmptyString(snake) ||
        TryGetPropertyIgnoreCase(node, "workflowRunId", out var camel) && IsNonEmptyString(camel);

    private static string? ReadDirectTerminalState(JsonElement node)
    {
        foreach (var key in new[] { "status", "state", "workflow_status", "workflow_state" })
        {
            if (!TryGetPropertyIgnoreCase(node, key, out var value) || value.ValueKind != JsonValueKind.String) continue;
            var status = value.GetString();
            if (status is not null && (IsSuccessfulTerminal(status) || UnsuccessfulTerminalStates.Contains(status, StringComparer.OrdinalIgnoreCase))) return status;
        }
        return null;
    }

    private static bool IsSuccessfulTerminal(string? status) => status is not null && TerminalStates.Contains(status, StringComparer.OrdinalIgnoreCase);

    private static bool TryGetPropertyIgnoreCase(JsonElement node, string name, out JsonElement value)
    {
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var property in node.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    private static bool IsNonEmptyString(JsonElement value) => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString());

    private static void CollectWorkflowRunIds(JsonElement value, ISet<string> ids)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var id = ReadDirectWorkflowRunId(value);
            if (id is not null) ids.Add(id);
            foreach (var property in value.EnumerateObject()) CollectWorkflowRunIds(property.Value, ids);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CollectWorkflowRunIds(item, ids);
    }
}

/// <summary>
/// Drives the official one-shot DeepSeek Harness headless CLI. Its local thread key is kept
/// separate from dsh's persisted session id, which arrives in the first JSON event.
/// </summary>
public sealed class DeepSeekHarnessAdapter : IAgentTaskLauncher, IAgentApprovalResponder, IAgentSessionState, IAsyncDisposable
{
    private readonly string _executable;
    private readonly string _patchFile;
    private readonly IReadOnlyList<string> _executablePrefixArguments;
    private readonly TimeSpan _approvalBridgeReadyTimeout;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _approvalReplies = new(StringComparer.Ordinal);
    private Process? _process;
    private CancellationTokenSource? _runCancellation;
    private string? _threadKey;
    private string? _sessionId;
    private string? _activeTurnId;
    private string? _pipeName;
    private volatile bool _approvalBridgeReady;
    private TaskCompletionSource<bool>? _approvalBridgeReadyCompletion;
    private readonly Dictionary<string, string> _toolNamesByCallId = new(StringComparer.Ordinal);
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private Task? _pipeTask;
    private int _cancelRequested;

    public DeepSeekHarnessAdapter(string? executable = null, string? patchFile = null, IReadOnlyList<string>? executablePrefixArguments = null,
        TimeSpan? approvalBridgeReadyTimeout = null)
    {
        _executable = string.IsNullOrWhiteSpace(executable) ? "dsh" : executable;
        _patchFile = patchFile ?? Path.Combine(AppContext.BaseDirectory, "DshBridge", "cordis.patch.yml");
        _executablePrefixArguments = executablePrefixArguments ?? Array.Empty<string>();
        _approvalBridgeReadyTimeout = approvalBridgeReadyTimeout ?? TimeSpan.FromSeconds(12);
    }

    public string ProviderId => AgentProviderIds.DeepSeekHarness;
    public AgentProviderCapabilities Capabilities { get; } = new(
        CanLaunchTasks: true,
        CanObserveExternalTasks: false,
        CanCancel: true,
        CanRespondToApprovals: true,
        CanAcceptUserInput: false,
        ApprovalBoundary: AgentApprovalBoundary.PondOneShot,
        CancellationBoundary: AgentCancellationBoundary.OuterProcessOnly,
        Limitations: "使用官方 headless 单次进程；只呈现一次性允许/拒绝。插件或审批桥不可用时请求失败关闭。");

    public event Action<AgentEvent>? AgentEventReceived;
    public event Action<AgentApprovalPrompt>? ApprovalPromptReceived;
    public event Action<string, string>? ResumableSessionResolved;

    public Task<string> StartThreadAsync(string executable, string cwd, string? resumeThreadId = null)
    {
        _sessionId = string.IsNullOrWhiteSpace(resumeThreadId) ? null : resumeThreadId;
        _threadKey = _sessionId ?? $"dsh-new:{Guid.NewGuid():N}";
        return Task.FromResult(_threadKey);
    }

    public async Task<string> StartTurnAsync(string threadId, string prompt, string cwd)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("任务内容不能为空。", nameof(prompt));
        if (_process is { HasExited: false }) throw new InvalidOperationException("DeepSeek Harness 上一轮仍在运行。");
        if (!File.Exists(_patchFile)) throw new FileNotFoundException("缺少应用自有的 DeepSeek 审批桥文件；为避免静默绕过审批，本轮未启动。", _patchFile);
        _threadKey = threadId;
        _activeTurnId = null;
        _toolNamesByCallId.Clear();
        _dshTerminal = new DshTerminal(false, null, null);
        _approvalBridgeReady = false;
        Interlocked.Exchange(ref _cancelRequested, 0);
        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        _pipeName = AgentPipeAddress.Create("dsh");
        var readyPipeName = AgentPipeAddress.Create("ready");
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridgeReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvalBridgeReadyCompletion = bridgeReady;

        var startInfo = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in _executablePrefixArguments) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--profile");
        startInfo.ArgumentList.Add("headless");
        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("--patch");
        startInfo.ArgumentList.Add(_patchFile);
        if (!string.IsNullOrWhiteSpace(_sessionId))
        {
            startInfo.ArgumentList.Add("--session-id");
            startInfo.ArgumentList.Add(_sessionId);
        }
        startInfo.ArgumentList.Add(prompt);
        startInfo.Environment["CODEX_POND_APPROVAL_PIPE"] = _pipeName;
        startInfo.Environment["CODEX_POND_APPROVAL_READY_PIPE"] = readyPipeName;

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!_process.Start()) throw new InvalidOperationException("无法启动 dsh。");
        }
        catch
        {
            _process.Dispose();
            _process = null;
            _runCancellation.Cancel();
            throw;
        }
        _pipeTask = ServeDshApprovalPipeAsync(_pipeName, _runCancellation.Token);
        _ = ServeDshApprovalReadyPipeAsync(readyPipeName, bridgeReady, _runCancellation.Token);
        _stdoutTask = ReadDshEventsAsync(_process, started, _runCancellation.Token);
        _stderrTask = DrainDiagnosticsAsync(_process.StandardError, _runCancellation.Token);
        _ = FinishDshRunAsync(_process, started, _runCancellation.Token);

        try
        {
            if (!await bridgeReady.Task.WaitAsync(_approvalBridgeReadyTimeout).ConfigureAwait(false))
                throw new InvalidOperationException("DeepSeek 审批桥没有通过协议握手；为避免无审批运行，本轮已停止。请检查 dsh 版本和 headless profile。");
            return await started.Task.WaitAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        }
        catch
        {
            if (_process is { HasExited: false })
            {
                Interlocked.Exchange(ref _cancelRequested, 1);
                try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            throw;
        }
    }

    public async Task InterruptTurnAsync(string threadId, string turnId)
    {
        if (!string.Equals(threadId, _threadKey, StringComparison.Ordinal) || !string.Equals(turnId, _activeTurnId, StringComparison.Ordinal))
            throw new InvalidOperationException("停止请求与当前 DeepSeek Harness turn 不匹配。");
        Interlocked.Exchange(ref _cancelRequested, 1);
        if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        await Task.CompletedTask;
    }

    public async Task RespondToApprovalAsync(AgentApprovalPrompt prompt, bool allowOnce)
    {
        if (!string.Equals(prompt.ProviderId, ProviderId, StringComparison.Ordinal) ||
            !_approvalReplies.TryRemove(prompt.RequestId, out var response))
            throw new InvalidOperationException("该 DeepSeek 审批已过期或不是当前提供方的请求。");
        response.TrySetResult(allowOnce);
        await Task.CompletedTask;
    }

    private async Task ReadDshEventsAsync(Process process, TaskCompletionSource<string> started, CancellationToken token)
    {
        string? finalText = null;
        string? turnEndReason = null;
        var finalSeen = false;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = ReadString(root, "type");
                if (type == "session")
                {
                    var actualSessionId = ReadString(root, "sessionId");
                    if (!string.IsNullOrWhiteSpace(actualSessionId))
                    {
                        _sessionId = actualSessionId;
                        if (!string.IsNullOrWhiteSpace(_threadKey)) ResumableSessionResolved?.Invoke(_threadKey, actualSessionId);
                    }
                    continue;
                }
                if (type == "thinking") continue;
                if (type == "text")
                {
                    var text = ReadString(root, "text");
                    if (!string.IsNullOrEmpty(text)) Emit(AgentEventKind.OutputDelta, text: text);
                    continue;
                }
                if (type == "final")
                {
                    finalSeen = true;
                    finalText = ReadString(root, "text");
                    continue;
                }
                if (type == "error")
                {
                    turnEndReason = "error";
                    Emit(AgentEventKind.StepCompleted, status: "headless_error");
                    continue;
                }
                if (type == "tool_call")
                {
                    var callId = ReadString(root, "callId");
                    var toolName = ReadString(root, "tool") ?? "tool";
                    if (!string.IsNullOrWhiteSpace(callId)) _toolNamesByCallId[callId] = toolName;
                    Emit(AgentEventKind.StepStarted, status: toolName, text: toolName);
                    continue;
                }
                if (type == "tool_result")
                {
                    var callId = ReadString(root, "callId");
                    var toolName = callId is not null && _toolNamesByCallId.TryGetValue(callId, out var mappedTool) ? mappedTool : "tool";
                    Emit(AgentEventKind.StepCompleted, status: ReadString(root, "status") ?? "tool_result", text: toolName);
                    continue;
                }
                if (type != "status") continue;

                var phase = ReadString(root, "phase");
                var turn = ReadString(root, "turn");
                switch (phase)
                {
                    case "turn_start":
                        if (string.IsNullOrWhiteSpace(turn)) turn = Guid.NewGuid().ToString("N");
                        _activeTurnId = turn;
                        Emit(AgentEventKind.Started, turnId: turn, status: "running");
                        started.TrySetResult(turn);
                        break;
                    case "step_start": Emit(AgentEventKind.StepStarted, turnId: turn, status: "step_start"); break;
                    case "step_end": Emit(AgentEventKind.StepCompleted, turnId: turn, status: "step_end"); break;
                    case "turn_end": turnEndReason = ReadString(root, "reason"); break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (JsonException ex)
        {
            started.TrySetException(new InvalidOperationException("dsh --json 返回了无法识别的事件；本轮不会记鱼。", ex));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            started.TrySetException(new InvalidOperationException("读取 dsh 事件流失败。", ex));
        }
        finally
        {
            _dshTerminal = new DshTerminal(finalSeen, finalText, turnEndReason);
        }
    }

    private DshTerminal _dshTerminal = new(false, null, null);

    private async Task FinishDshRunAsync(Process process, TaskCompletionSource<string> started, CancellationToken token)
    {
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (_stdoutTask is not null) await _stdoutTask.ConfigureAwait(false);
            if (_stderrTask is not null) await _stderrTask.ConfigureAwait(false);
            if (!_approvalBridgeReady)
                _approvalBridgeReadyCompletion?.TrySetException(new InvalidOperationException("dsh 在审批桥完成握手前退出。"));
            var terminal = _dshTerminal;
            if (Interlocked.Exchange(ref _cancelRequested, 0) == 1)
            {
                Emit(AgentEventKind.Cancelled, status: "cancelled");
            }
            else if (_approvalBridgeReady && process.ExitCode == 0 && terminal.FinalSeen &&
                     string.Equals(terminal.Reason, "completed", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(_activeTurnId))
            {
                Emit(AgentEventKind.Completed, turnId: _activeTurnId, status: "completed", text: terminal.Text);
            }
            else
            {
                var status = !_approvalBridgeReady ? "approval_bridge_not_ready" : terminal.Reason ?? $"exit_{process.ExitCode}";
                Emit(AgentEventKind.Failed, status: status, text: terminal.Text);
            }
            if (!started.Task.IsCompleted) started.TrySetException(new InvalidOperationException("dsh 在开始 turn 前退出。请检查 DeepSeek Harness 安装、headless profile 与本机认证配置。"));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            started.TrySetException(ex);
            Emit(AgentEventKind.Disconnected, status: "disconnected", text: "DeepSeek Harness headless 连接中断。");
        }
    }

    private async Task ServeDshApprovalPipeAsync(string pipeName, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            try
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) { await writer.WriteLineAsync("{\"decision\":\"deny\"}"); continue; }
                using var requestDoc = JsonDocument.Parse(line);
                var root = requestDoc.RootElement;
                var requestId = Guid.NewGuid().ToString("N");
                var prompt = new AgentApprovalPrompt(requestId, ProviderId,
                    ReadString(root, "toolName") is { Length: > 0 } name ? $"DeepSeek 请求允许工具：{name}" : "DeepSeek 请求一次性操作授权",
                    ReadString(root, "reason") ?? "DeepSeek Harness 请求执行一项受审批保护的操作。",
                    "仅允许这一次；不保存为会话授权", CanAllowOnce: true, CanReject: true,
                    SessionId: _threadKey, TurnId: _activeTurnId);
                var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _approvalReplies[requestId] = response;
                ApprovalPromptReceived?.Invoke(prompt);
                bool allowed;
                try { allowed = await response.Task.WaitAsync(TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { _approvalReplies.TryRemove(requestId, out _); allowed = false; }
                await writer.WriteLineAsync(allowed ? "{\"decision\":\"allow\"}" : "{\"decision\":\"deny\"}");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                try { await writer.WriteLineAsync("{\"decision\":\"deny\"}"); } catch (IOException) { }
            }
        }
    }

    private async Task ServeDshApprovalReadyPipeAsync(string pipeName, TaskCompletionSource<bool> ready, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try { await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { ready.TrySetCanceled(cancellationToken); return; }
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        try
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            using var requestDocument = JsonDocument.Parse(line ?? "{}");
            var root = requestDocument.RootElement;
            var isCompatible = ReadString(root, "type") == "bridge_ready" &&
                root.TryGetProperty("protocolVersion", out var protocolVersion) && protocolVersion.ValueKind == JsonValueKind.Number &&
                protocolVersion.GetInt32() == 1;
            await writer.WriteLineAsync(isCompatible ? "{\"ready\":true,\"protocolVersion\":1}" : "{\"ready\":false}");
            if (isCompatible)
            {
                _approvalBridgeReady = true;
                ready.TrySetResult(true);
            }
            else ready.TrySetException(new InvalidOperationException("DeepSeek 审批桥协议版本不兼容。"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or OperationCanceledException)
        {
            ready.TrySetException(new InvalidOperationException("DeepSeek 审批桥握手失败。", ex));
        }
    }

    private void Emit(AgentEventKind kind, string? turnId = null, string? status = null, string? text = null) =>
        AgentEventReceived?.Invoke(new AgentEvent(kind, ProviderId, _threadKey, turnId ?? _activeTurnId, text, status));

    private static async Task DrainDiagnosticsAsync(StreamReader reader, CancellationToken token)
    {
        try { while (await reader.ReadLineAsync(token).ConfigureAwait(false) is not null) { } }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) return id.GetString();
        return null;
    }

    public ValueTask DisposeAsync()
    {
        _runCancellation?.Cancel();
        if (_process is { HasExited: false }) { try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        if (_process is not null) { _process.Dispose(); _process = null; }
        _runCancellation?.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record DshTerminal(bool FinalSeen, string? Text, string? Reason);
}

/// <summary>
/// Runs Claude Code in print/stream-json mode. Existing user and project MCP configuration
/// remains enabled; a per-process Pond permission MCP server is added without editing it.
/// </summary>
public sealed class ClaudeCodeAgentAdapter : IAgentTaskLauncher, IAgentApprovalResponder, IAsyncDisposable
{
    private readonly string _executable;
    private readonly string _applicationExe;
    private readonly IReadOnlyList<string> _executablePrefixArguments;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _approvalReplies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _toolNamesByUseId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _workflowRunIds = new(StringComparer.Ordinal);
    private Process? _process;
    private CancellationTokenSource? _runCancellation;
    private string? _threadKey;
    private string? _sessionId;
    private string? _activeTurnId;
    private string? _pipeName;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private Task? _pipeTask;
    private int _cancelRequested;
    private bool _workBuddyInitializationSucceeded;
    private bool _durableWorkflowObserved;
    private bool _verifiedWorkflowCompletion;
    private bool _verifiedWorkflowUnsuccessfulTerminal;

    public ClaudeCodeAgentAdapter(string? executable = null, string? applicationExe = null, IReadOnlyList<string>? executablePrefixArguments = null)
    {
        _executable = string.IsNullOrWhiteSpace(executable) ? "claude" : executable;
        _applicationExe = applicationExe ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "CodexPond.exe");
        _executablePrefixArguments = executablePrefixArguments ?? Array.Empty<string>();
    }

    public string ProviderId => AgentProviderIds.WorkBuddy;
    public AgentProviderCapabilities Capabilities { get; } = new(
        CanLaunchTasks: true,
        CanObserveExternalTasks: false,
        CanCancel: true,
        CanRespondToApprovals: true,
        CanAcceptUserInput: false,
        ApprovalBoundary: AgentApprovalBoundary.PondForOuterToolsProviderNativeForDurableWorkflows,
        CancellationBoundary: AgentCancellationBoundary.OuterProcessOnly,
        Limitations: "池塘处理 Claude Code 外层工具权限；Work Buddy 自身授权仍在其原生界面。停止只中断外层进程，后台工作流可能继续；无法确认工作流完成时不收鱼。");

    public event Action<AgentEvent>? AgentEventReceived;
    public event Action<AgentApprovalPrompt>? ApprovalPromptReceived;

    public Task<string> StartThreadAsync(string executable, string cwd, string? resumeThreadId = null)
    {
        _sessionId = string.IsNullOrWhiteSpace(resumeThreadId) ? Guid.NewGuid().ToString() : resumeThreadId;
        _threadKey = _sessionId;
        _hasCompletedTurn = !string.IsNullOrWhiteSpace(resumeThreadId);
        return Task.FromResult(_threadKey);
    }

    public async Task<string> StartTurnAsync(string threadId, string prompt, string cwd)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("任务内容不能为空。", nameof(prompt));
        if (_process is { HasExited: false }) throw new InvalidOperationException("Claude Code 上一轮仍在运行。");
        _threadKey = threadId;
        _sessionId ??= Guid.NewGuid().ToString();
        _activeTurnId = Guid.NewGuid().ToString("N");
        _workBuddyInitializationSucceeded = false;
        _durableWorkflowObserved = false;
        _verifiedWorkflowCompletion = false;
        _verifiedWorkflowUnsuccessfulTerminal = false;
        _lastResultSucceeded = false;
        _finalText = null;
        _workflowRunIds.Clear();
        _toolNamesByUseId.Clear();
        Interlocked.Exchange(ref _cancelRequested, 0);
        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        _pipeName = AgentPipeAddress.Create("claude");
        var startInfo = CreateStartInfo(cwd, _sessionId, prompt, _pipeName);
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!_process.Start()) throw new InvalidOperationException("无法启动 Claude Code CLI。");
        }
        catch
        {
            _process.Dispose();
            _process = null;
            _runCancellation.Cancel();
            throw;
        }
        _pipeTask = ServeClaudeApprovalPipeAsync(_pipeName, _runCancellation.Token);
        _stdoutTask = ReadClaudeEventsAsync(_process, _runCancellation.Token);
        _stderrTask = DrainDiagnosticsAsync(_process.StandardError, _runCancellation.Token);
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content = BuildPrompt(prompt) }
        }));
        await _process.StandardInput.FlushAsync();
        _process.StandardInput.Close();
        _ = FinishClaudeRunAsync(_process, _runCancellation.Token);
        return _activeTurnId;
    }

    private static string BuildPrompt(string request) =>
        "Codex Pond Work Buddy connector protocol: before using any other Work Buddy tool, call mcp__work-buddy__wb_init with the WORK_BUDDY_SESSION_ID supplied by the Work Buddy runtime. Do not invent a session ID or continue through another provider if initialization is missing or fails. For any workflow started through wb_run that returns workflow_run_id, retain that ID and call wb_status until it reports a terminal successful or unsuccessful state; report clearly if a terminal state cannot be confirmed. Work Buddy consent and approval requests must remain in its native authorization surface.\n\nUser task:\n" + request;

    private ProcessStartInfo CreateStartInfo(string cwd, string sessionId, string prompt, string pipeName)
    {
        var startInfo = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in _executablePrefixArguments) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--print");
        startInfo.ArgumentList.Add("--input-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--verbose");
        if (string.IsNullOrWhiteSpace(_threadKey) || _threadKey.StartsWith("claude-new:", StringComparison.Ordinal))
        {
            startInfo.ArgumentList.Add("--session-id");
            startInfo.ArgumentList.Add(sessionId);
        }
        else if (_process is not null || !string.IsNullOrWhiteSpace(sessionId))
        {
            // --resume is used only after this adapter has already completed a turn in that session.
            if (_hasCompletedTurn)
            {
                startInfo.ArgumentList.Add("--resume");
                startInfo.ArgumentList.Add(sessionId);
            }
            else
            {
                startInfo.ArgumentList.Add("--session-id");
                startInfo.ArgumentList.Add(sessionId);
            }
        }

        var mcpConfig = JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["codex_pond_permissions"] = new
                {
                    command = _applicationExe,
                    args = new[] { "--permission-mcp-stdio" }
                }
            }
        });
        startInfo.ArgumentList.Add("--mcp-config");
        startInfo.ArgumentList.Add(mcpConfig);
        startInfo.ArgumentList.Add("--permission-prompt-tool");
        startInfo.ArgumentList.Add("mcp__codex_pond_permissions__approve");
        startInfo.Environment["CODEX_POND_APPROVAL_PIPE"] = pipeName;
        return startInfo;
    }

    private bool _hasCompletedTurn;

    public async Task InterruptTurnAsync(string threadId, string turnId)
    {
        if (!string.Equals(threadId, _threadKey, StringComparison.Ordinal) || !string.Equals(turnId, _activeTurnId, StringComparison.Ordinal))
            throw new InvalidOperationException("停止请求与当前 Claude Code turn 不匹配。");
        Interlocked.Exchange(ref _cancelRequested, 1);
        if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        await Task.CompletedTask;
    }

    public async Task RespondToApprovalAsync(AgentApprovalPrompt prompt, bool allowOnce)
    {
        if (!string.Equals(prompt.ProviderId, ProviderId, StringComparison.Ordinal) || !_approvalReplies.TryRemove(prompt.RequestId, out var response))
            throw new InvalidOperationException("该 Claude Code 权限请求已过期。");
        response.TrySetResult(allowOnce);
        await Task.CompletedTask;
    }

    private async Task ReadClaudeEventsAsync(Process process, CancellationToken token)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = ReadString(root, "type");
                if (type == "system" && string.Equals(ReadString(root, "subtype"), "init", StringComparison.Ordinal))
                {
                    var sessionId = ReadString(root, "session_id");
                    if (!string.IsNullOrWhiteSpace(sessionId)) _sessionId = sessionId;
                    Emit(AgentEventKind.Started, status: "running");
                    continue;
                }
                if (type == "stream_event" && root.TryGetProperty("event", out var streamEvent))
                {
                    if (ReadString(streamEvent, "type") == "content_block_delta" && streamEvent.TryGetProperty("delta", out var delta) &&
                        ReadString(delta, "type") == "text_delta" && ReadString(delta, "text") is { Length: > 0 } deltaText)
                        Emit(AgentEventKind.OutputDelta, text: deltaText);
                    continue;
                }
                if (type == "assistant")
                {
                    if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                    foreach (var block in content.EnumerateArray())
                    {
                        var blockType = ReadString(block, "type");
                        if (blockType == "tool_use")
                        {
                            var toolName = ReadString(block, "name") ?? "tool";
                            if (ReadString(block, "id") is { Length: > 0 } toolUseId) _toolNamesByUseId[toolUseId] = toolName;
                            if (IsWorkBuddyTool(toolName) &&
                                (toolName.Contains("wb_advance", StringComparison.OrdinalIgnoreCase) || IsStatusTool(toolName)) &&
                                block.TryGetProperty("input", out var toolInput))
                            {
                                _workflowRunIds.UnionWith(WorkBuddyProtocolEvidence.ExtractWorkflowRunIds(toolInput.GetRawText()));
                                _durableWorkflowObserved = _workflowRunIds.Count > 0;
                            }
                            Emit(AgentEventKind.StepStarted, status: toolName, text: toolName);
                        }
                    }
                    continue;
                }
                if (type == "user" && root.TryGetProperty("message", out var userMessage) && userMessage.TryGetProperty("content", out var userContent) && userContent.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in userContent.EnumerateArray())
                    {
                        if (ReadString(block, "type") != "tool_result") continue;
                        var toolUseId = ReadString(block, "tool_use_id");
                        var toolName = (toolUseId is not null && _toolNamesByUseId.TryGetValue(toolUseId, out var mappedName))
                            ? mappedName
                            : ReadString(block, "name") ?? "";
                        var resultText = ReadContentText(block);
                        var toolFailed = block.TryGetProperty("is_error", out var isToolError) && isToolError.ValueKind == JsonValueKind.True;
                        if (!toolFailed && IsWorkBuddyTool(toolName) && toolName.Contains("wb_init", StringComparison.OrdinalIgnoreCase))
                            _workBuddyInitializationSucceeded = true;
                        if (!toolFailed && IsWorkBuddyTool(toolName) && toolName.Contains("wb_run", StringComparison.OrdinalIgnoreCase) && ContainsWorkflowRunId(resultText))
                        {
                            _workflowRunIds.UnionWith(WorkBuddyProtocolEvidence.ExtractWorkflowRunIds(resultText));
                            _durableWorkflowObserved = _workflowRunIds.Count > 0;
                        }
                        if (!toolFailed && IsWorkBuddyTool(toolName) && IsStatusTool(toolName))
                        {
                            var terminalStatus = WorkBuddyProtocolEvidence.ReadTerminalWorkflowStatus(resultText, _workflowRunIds);
                            if (terminalStatus is not null)
                            {
                                _verifiedWorkflowCompletion = WorkBuddyProtocolEvidence.IndicatesCompletedWorkflowStatus(resultText, _workflowRunIds);
                                _verifiedWorkflowUnsuccessfulTerminal = !_verifiedWorkflowCompletion;
                            }
                        }
                        Emit(AgentEventKind.StepCompleted, status: "tool_result", text: toolName);
                    }
                    continue;
                }
                if (type == "result")
                {
                    if (root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                        _lastResultSucceeded = false;
                    else _lastResultSucceeded = true;
                    if (ReadString(root, "result") is { Length: > 0 } finalText) _finalText = finalText;
                    if (ReadString(root, "session_id") is { Length: > 0 } finalSession) _sessionId = finalSession;
                    continue;
                }
                // Thinking blocks and verbose diagnostics are intentionally ignored.
            }
        }
        catch (OperationCanceledException) { }
        catch (JsonException) { _lastResultSucceeded = false; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { _lastResultSucceeded = false; }
    }

    private bool _lastResultSucceeded;
    private string? _finalText;

    private async Task FinishClaudeRunAsync(Process process, CancellationToken token)
    {
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (_stdoutTask is not null) await _stdoutTask.ConfigureAwait(false);
            if (_stderrTask is not null) await _stderrTask.ConfigureAwait(false);
            if (Interlocked.Exchange(ref _cancelRequested, 0) == 1)
                Emit(AgentEventKind.Cancelled, status: "outer_session_cancelled", text: "外层 Claude Code 会话已中断；Work Buddy 已启动的后台流程可能仍在运行。");
            else if (process.ExitCode == 0 && _lastResultSucceeded)
            {
                _hasCompletedTurn = true;
                var evidence = new WorkBuddyRunEvidence(_workBuddyInitializationSucceeded, _durableWorkflowObserved,
                    _verifiedWorkflowCompletion, _verifiedWorkflowUnsuccessfulTerminal);
                Emit(AgentEventKind.Completed, status: "completed", text: _finalText, payload: evidence);
            }
            else Emit(AgentEventKind.Failed, status: $"exit_{process.ExitCode}", text: _finalText);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Emit(AgentEventKind.Disconnected, status: "disconnected", text: ex.Message); }
    }

    private async Task ServeClaudeApprovalPipeAsync(string pipeName, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            try
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line)) { await writer.WriteLineAsync("{\"decision\":\"deny\"}"); continue; }
                using var requestDoc = JsonDocument.Parse(line);
                var request = requestDoc.RootElement;
                var requestId = Guid.NewGuid().ToString("N");
                var toolName = ReadString(request, "tool_name") ?? "Claude Code 工具";
                var toolInput = request.TryGetProperty("tool_input", out var input) ? input.GetRawText() : "{}";
                var prompt = new AgentApprovalPrompt(requestId, ProviderId,
                    $"Claude Code 请求权限：{toolName}", toolInput,
                    "只授权当前这次操作；不会写入 Claude 或 Work Buddy 配置", CanAllowOnce: true, CanReject: true,
                    SessionId: _threadKey, TurnId: _activeTurnId);
                var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _approvalReplies[requestId] = response;
                ApprovalPromptReceived?.Invoke(prompt);
                bool allowed;
                try { allowed = await response.Task.WaitAsync(TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException) { _approvalReplies.TryRemove(requestId, out _); allowed = false; }
                await writer.WriteLineAsync(allowed ? "{\"decision\":\"allow\"}" : "{\"decision\":\"deny\"}");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                try { await writer.WriteLineAsync("{\"decision\":\"deny\"}"); } catch (IOException) { }
            }
        }
    }

    private static bool IsWorkBuddyTool(string name) => name.Contains("workbuddy", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("work_buddy", StringComparison.OrdinalIgnoreCase) || name.Contains("wb_", StringComparison.OrdinalIgnoreCase);
    private static bool IsStatusTool(string name) => name.Contains("wb_status", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWorkflowRunId(string value)
        => WorkBuddyProtocolEvidence.ContainsWorkflowRunId(value);

    private static bool HasWorkflowRunId(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if ((property.Name.Equals("workflow_run_id", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("workflowRunId", StringComparison.OrdinalIgnoreCase)) &&
                    property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString())) return true;
                if (HasWorkflowRunId(property.Value)) return true;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) if (HasWorkflowRunId(item)) return true;
        }
        return false;
    }

    private void Emit(AgentEventKind kind, string? status = null, string? text = null, object? payload = null) =>
        AgentEventReceived?.Invoke(new AgentEvent(kind, ProviderId, _threadKey, _activeTurnId, text, status, payload));

    private static async Task DrainDiagnosticsAsync(StreamReader reader, CancellationToken token)
    {
        try { while (await reader.ReadLineAsync(token).ConfigureAwait(false) is not null) { } }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string ReadContentText(JsonElement element)
    {
        if (!element.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind == JsonValueKind.Array)
            return string.Join("\n", content.EnumerateArray().Where(item => ReadString(item, "type") == "text")
                .Select(item => ReadString(item, "text") ?? ""));
        return content.ValueKind == JsonValueKind.Undefined ? "" : content.GetRawText();
    }

    public ValueTask DisposeAsync()
    {
        _runCancellation?.Cancel();
        if (_process is { HasExited: false }) { try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        if (_process is not null) { _process.Dispose(); _process = null; }
        _runCancellation?.Dispose();
        return ValueTask.CompletedTask;
    }
}
