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
    bool CanReject = true);

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

public sealed record AgentProviderOption(string Id, string Label, string Guidance);

public static class AgentProviderCatalog
{
    public static IReadOnlyList<AgentProviderOption> Available { get; } =
    [
        new(AgentProviderIds.Codex, "Codex", "通过本机 codex app-server 启动 Codex 会话。"),
        new(AgentProviderIds.DeepSeekHarness, "DeepSeek Harness", "需要本机 dsh CLI 和已配置的 headless profile；审批在池塘内逐次处理。"),
        new(AgentProviderIds.WorkBuddy, "Work Buddy", "通过 Claude Code CLI 使用已配置 MCP；Work Buddy 自身授权仍在原生界面。")
    ];

    public static string Label(string? id) => Available.FirstOrDefault(provider => provider.Id == id)?.Label ?? "Codex";
}
