namespace CodexPond;

public enum AgentEventKind
{
    Started,
    StepStarted,
    StepCompleted,
    OutputDelta,
    ApprovalRequired,
    Completed,
    Failed,
    Cancelled,
    Disconnected
}

public sealed record AgentEvent(
    AgentEventKind Kind,
    string ProviderId,
    string? SessionId = null,
    string? TurnId = null,
    string? Text = null,
    string? Status = null,
    object? ProviderPayload = null);

public sealed record AgentProviderCapabilities(
    bool CanLaunchTasks,
    bool CanObserveExternalTasks,
    bool CanCancel,
    bool CanRespondToApprovals,
    bool CanAcceptUserInput,
    AgentApprovalBoundary ApprovalBoundary = AgentApprovalBoundary.None,
    AgentCancellationBoundary CancellationBoundary = AgentCancellationBoundary.None,
    string? Limitations = null);

public enum AgentApprovalBoundary
{
    None,
    PondOneShot,
    ProviderNative,
    PondForOuterToolsProviderNativeForDurableWorkflows
}

public enum AgentCancellationBoundary
{
    None,
    Turn,
    OuterProcessOnly
}

public sealed record AgentApprovalPrompt(
    string RequestId,
    string ProviderId,
    string Title,
    string Details,
    string Scope,
    bool CanAllowOnce = true,
    bool CanReject = true,
    string? SessionId = null,
    string? TurnId = null);

public interface IAgentApprovalResponder
{
    event Action<AgentApprovalPrompt>? ApprovalPromptReceived;
    Task RespondToApprovalAsync(AgentApprovalPrompt prompt, bool allowOnce);
}

/// <summary>Providers which discover a durable resumable session after launching can report it for safe local persistence.</summary>
public interface IAgentSessionState
{
    event Action<string, string>? ResumableSessionResolved;
}

/// <summary>Provider-neutral task creation and lifecycle contract used by the pond UI.</summary>
public interface IAgentTaskLauncher
{
    string ProviderId { get; }
    AgentProviderCapabilities Capabilities { get; }
    event Action<AgentEvent>? AgentEventReceived;
    Task<string> StartThreadAsync(string executable, string cwd, string? resumeThreadId = null);
    Task<string> StartTurnAsync(string threadId, string prompt, string cwd);
    Task InterruptTurnAsync(string threadId, string turnId);
}

/// <summary>Future providers can implement this separately to observe tasks created elsewhere.</summary>
public interface IAgentTaskMonitor
{
    string ProviderId { get; }
    AgentProviderCapabilities Capabilities { get; }
    event Action<AgentEvent>? AgentEventReceived;
    Task ObserveExternalTaskAsync(string externalTaskId, CancellationToken cancellationToken = default);
}

/// <summary>Provider ids shown in the shared task panel. Connectors are selected explicitly and report their own limits.</summary>
public static class AgentProviderIds
{
    public const string Codex = "codex";
    public const string DeepSeekHarness = "deepseek-harness";
    public const string WorkBuddy = "work-buddy";
}

/// <summary>Creates local IPC names that work with Windows named pipes and Unix-domain-socket-backed .NET pipes.</summary>
public static class AgentPipeAddress
{
    public static string Create(string purpose)
    {
        var id = Guid.NewGuid().ToString("N");
        if (OperatingSystem.IsWindows()) return $"CodexPond.{purpose}.{id}";
        return Path.Combine(Path.GetTempPath(), $"cp.{purpose}.{id}");
    }
}

public sealed record AgentProviderOption(string Id, string Label, string Guidance, string Readiness);

public static class AgentProviderCatalog
{
    public static IReadOnlyList<AgentProviderOption> Available { get; } =
    [
        new(AgentProviderIds.Codex, "Codex", "通过本机 codex app-server 启动 Codex 会话。", "尚未检查 codex CLI、登录和 App Server；提交任务时检查。"),
        new(AgentProviderIds.DeepSeekHarness, "DeepSeek Harness", "需要本机 dsh CLI 和已配置的 headless profile；审批在池塘内逐次处理。", "尚未检查 dsh CLI、headless profile 和审批桥；提交任务时检查。"),
        new(AgentProviderIds.WorkBuddy, "Work Buddy", "通过 Claude Code CLI 使用已配置 MCP；Work Buddy 自身授权仍在原生界面。", "尚未检查 claude CLI 和 Work Buddy MCP；提交任务时检查。")
    ];

    public static string Label(string? id) => Available.FirstOrDefault(provider => provider.Id == id)?.Label ?? "Codex";
}

/// <summary>Rejects delayed events that do not belong to the provider, session, and turn currently shown by the pond.</summary>
public static class AgentEventIsolation
{
    public static string TurnKey(string providerId, string turnId) => $"{providerId}:{turnId}";

    public static bool IsCurrentTurnEvent(
        IAgentTaskLauncher source,
        IAgentTaskLauncher? activeLauncher,
        AgentEvent agentEvent,
        bool turnBusy,
        string? activeSessionId,
        string? activeTurnId,
        IReadOnlySet<string>? terminalTurnIds = null)
    {
        if (!turnBusy || !ReferenceEquals(source, activeLauncher) ||
            !string.Equals(source.ProviderId, agentEvent.ProviderId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(activeSessionId)) return false;

        if (string.IsNullOrWhiteSpace(agentEvent.SessionId) ||
            !string.Equals(agentEvent.SessionId, activeSessionId, StringComparison.Ordinal)) return false;

        if (agentEvent.Kind == AgentEventKind.Started)
            return !string.IsNullOrWhiteSpace(agentEvent.TurnId) &&
                !(terminalTurnIds?.Contains(TurnKey(agentEvent.ProviderId, agentEvent.TurnId)) ?? false) &&
                (string.IsNullOrWhiteSpace(activeTurnId) || string.Equals(agentEvent.TurnId, activeTurnId, StringComparison.Ordinal));

        return !string.IsNullOrWhiteSpace(activeTurnId) &&
            !string.IsNullOrWhiteSpace(agentEvent.TurnId) &&
            string.Equals(agentEvent.TurnId, activeTurnId, StringComparison.Ordinal);
    }
}
