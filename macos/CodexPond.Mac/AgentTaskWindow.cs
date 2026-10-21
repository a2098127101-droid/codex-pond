using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace CodexPond.Mac;

/// <summary>Native cross-platform task panel for the shared Codex/DSH/Work Buddy process adapters.</summary>
public sealed class AgentTaskWindow : Window
{
    private readonly PondSettings _settings;
    private readonly PondSettingsStore _settingsStore;
    private readonly FishBookStore _fishBook;
    private readonly Action<string> _onFishCaught;
    private readonly ComboBox _providerPicker;
    private readonly TextBox _workspaceBox;
    private readonly TextBox _executableBox;
    private readonly TextBox _promptBox;
    private readonly TextBox _outputBox;
    private readonly TextBlock _statusText;
    private readonly TextBlock _providerNote;
    private readonly StackPanel _approvalPanel;
    private readonly TextBlock _approvalTitle;
    private readonly TextBlock _approvalDetails;
    private readonly StackPanel _approvalAnswers;
    private readonly Button _allowButton;
    private readonly Button _rejectButton;
    private readonly Button _startButton;
    private readonly Button _stopButton;
    private IAgentTaskLauncher? _launcher;
    private IAsyncDisposable? _adapter;
    private IAgentApprovalResponder? _approvalResponder;
    private CodexAppServerAdapter? _codex;
    private CodexApprovalRequest? _pendingCodexRequest;
    private AgentApprovalPrompt? _pendingProviderPrompt;
    private readonly List<(CodexInputQuestion Question, TextBox Answer)> _codexAnswers = [];
    private string? _activeSessionId;
    private string? _activeTurnId;
    private string? _activeProviderId;
    private bool _turnBusy;
    private bool _stopRequested;
    private bool _shuttingDown;

    public AgentTaskWindow(PondSettings settings, PondSettingsStore settingsStore, FishBookStore fishBook, Action<string> onFishCaught)
    {
        _settings = settings;
        _settingsStore = settingsStore;
        _fishBook = fishBook;
        _onFishCaught = onFishCaught;
        Title = "Codex Pond · Agent 任务";
        Width = 660;
        Height = 760;
        MinWidth = 560;
        MinHeight = 620;
        Background = Brush("#E6D2AA");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid
        {
            RowDefinitions = new RowDefinitions { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto) },
            Margin = new Thickness(18)
        };
        Content = root;

        var titleStack = new StackPanel { Spacing = 3 };
        titleStack.Children.Add(new TextBlock { Text = "Agent 池塘", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Brush("#354338") });
        titleStack.Children.Add(new TextBlock { Text = "本机提供方会使用自己的登录、沙箱与审批设置。成功完成一轮后收一条鱼。", FontSize = 11, Foreground = Brush("#5A5948"), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(titleStack);

        var setup = new StackPanel { Spacing = 7, Margin = new Thickness(0, 12, 0, 12) };
        setup.Children.Add(new TextBlock { Text = "提供方", FontWeight = FontWeight.SemiBold, Foreground = Brush("#46543B") });
        _providerPicker = new ComboBox { ItemsSource = AgentProviderCatalog.Available.Select(provider => provider.Label).ToArray(), SelectedIndex = ProviderIndex(settings.AgentProviderId) };
        _providerPicker.SelectionChanged += (_, _) => ProviderChanged();
        setup.Children.Add(_providerPicker);
        _providerNote = new TextBlock { FontSize = 10, Foreground = Brush("#65734D"), TextWrapping = TextWrapping.Wrap };
        setup.Children.Add(_providerNote);
        setup.Children.Add(new TextBlock { Text = "工作目录", FontWeight = FontWeight.SemiBold, Foreground = Brush("#46543B") });
        var workspaceRow = new Grid { ColumnDefinitions = new ColumnDefinitions { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) } };
        _workspaceBox = new TextBox { Text = settings.LastWorkspace, PlaceholderText = "/Users/you/Projects/your-repo" };
        workspaceRow.Children.Add(_workspaceBox);
        var chooseFolder = MakeButton("选择…", async (_, _) => await ChooseWorkspaceAsync());
        Grid.SetColumn(chooseFolder, 1);
        workspaceRow.Children.Add(chooseFolder);
        setup.Children.Add(workspaceRow);
        setup.Children.Add(new TextBlock { Text = "CLI 命令或路径", FontWeight = FontWeight.SemiBold, Foreground = Brush("#46543B") });
        _executableBox = new TextBox();
        setup.Children.Add(_executableBox);
        setup.Children.Add(new TextBlock { Text = "任务", FontWeight = FontWeight.SemiBold, Foreground = Brush("#46543B") });
        _promptBox = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 74, PlaceholderText = "描述要处理的任务…" };
        setup.Children.Add(_promptBox);
        Grid.SetRow(setup, 1);
        root.Children.Add(setup);

        _outputBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brush("#1B2926"),
            Foreground = Brush("#D8E7C4"),
            FontFamily = new FontFamily("Cascadia Mono, Menlo, Consolas, monospace"),
            Padding = new Thickness(12),
            PlaceholderText = "任务运行后，Agent 回复会显示在这里。"
        };
        var outputScroll = new ScrollViewer { Content = _outputBox, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(outputScroll, 2);
        root.Children.Add(outputScroll);

        _approvalPanel = new StackPanel { Spacing = 7, Background = Brush("#F0E2BF"), Margin = new Thickness(0, 10, 0, 0), IsVisible = false };
        _approvalTitle = new TextBlock { FontWeight = FontWeight.Bold, Foreground = Brush("#8A4D25"), FontSize = 13 };
        _approvalPanel.Children.Add(_approvalTitle);
        _approvalDetails = new TextBlock { Foreground = Brush("#554B39"), FontSize = 11, TextWrapping = TextWrapping.Wrap };
        _approvalPanel.Children.Add(_approvalDetails);
        _approvalAnswers = new StackPanel { Spacing = 5 };
        _approvalPanel.Children.Add(_approvalAnswers);
        var approvalButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        _rejectButton = MakeButton("拒绝", (_, _) => _ = RespondToApprovalAsync(false));
        _allowButton = MakeButton("本次允许", (_, _) => _ = RespondToApprovalAsync(true));
        approvalButtons.Children.Add(_rejectButton);
        approvalButtons.Children.Add(_allowButton);
        _approvalPanel.Children.Add(approvalButtons);
        Grid.SetRow(_approvalPanel, 3);
        root.Children.Add(_approvalPanel);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, Margin = new Thickness(0, 10, 0, 0) };
        _statusText = new TextBlock { Text = "等待提交任务。", Foreground = Brush("#46543B"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        footer.Children.Add(_statusText);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _stopButton = MakeButton("请求停止", (_, _) => _ = StopAsync());
        _stopButton.IsEnabled = false;
        _startButton = MakeButton("开始任务", async (_, _) => await StartAsync());
        actions.Children.Add(_stopButton);
        actions.Children.Add(_startButton);
        Grid.SetColumn(actions, 1);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 4);
        root.Children.Add(footer);

        ProviderChanged();
        Closing += (_, e) =>
        {
            if (!_shuttingDown)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        try
        {
            if (_turnBusy && _launcher is not null && _activeSessionId is not null && _activeTurnId is not null)
                await _launcher.InterruptTurnAsync(_activeSessionId, _activeTurnId);
        }
        catch (Exception) { }
        if (_adapter is not null)
        {
            try { await _adapter.DisposeAsync(); } catch (Exception) { }
        }
        Close();
    }

    private async Task ChooseWorkspaceAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择 Agent 工作目录", AllowMultiple = false });
        if (folders.Count > 0) _workspaceBox.Text = folders[0].Path.LocalPath;
    }

    private async Task StartAsync()
    {
        if (_turnBusy) return;
        var provider = CurrentProvider();
        var workspace = _workspaceBox.Text?.Trim() ?? "";
        var prompt = _promptBox.Text?.Trim() ?? "";
        if (!Directory.Exists(workspace)) { _statusText.Text = "工作目录不存在，请选择或输入有效目录。"; return; }
        if (string.IsNullOrWhiteSpace(prompt)) { _statusText.Text = "请先填写任务内容。"; return; }

        await DisposeCurrentAdapterAsync();
        try
        {
            _settings.AgentProviderId = provider.Id;
            _settings.LastWorkspace = workspace;
            SaveExecutable(provider.Id, _executableBox.Text?.Trim() ?? "");
            _settingsStore.Save(_settings);

            CreateAdapter(provider.Id);
            if (_launcher is null) throw new InvalidOperationException("没有可用的 Agent 连接器。");
            _activeProviderId = provider.Id;
            _turnBusy = true;
            _stopRequested = false;
            _startButton.IsEnabled = false;
            _stopButton.IsEnabled = true;
            _statusText.Text = $"正在连接 {provider.Label}…";
            _outputBox.Text = "";

            _settings.ProviderSessions.TryGetValue(provider.Id, out var priorSession);
            _settings.ProviderSessionWorkspaces.TryGetValue(provider.Id, out var priorWorkspace);
            var resumeId = string.Equals(priorWorkspace, workspace, StringComparison.Ordinal) ? priorSession : null;
            var sessionId = await _launcher.StartThreadAsync(_executableBox.Text?.Trim() ?? "", workspace, resumeId);
            _activeSessionId = sessionId;
            _settings.ProviderSessions[provider.Id] = sessionId;
            _settings.ProviderSessionWorkspaces[provider.Id] = workspace;
            _settings.LastThreadId = sessionId;
            _settings.LastThreadWorkspace = workspace;
            _settingsStore.Save(_settings);

            _statusText.Text = $"{provider.Label} 正在处理…";
            _activeTurnId = await _launcher.StartTurnAsync(sessionId, prompt, workspace);
            _promptBox.Text = "";
        }
        catch (Exception ex)
        {
            _turnBusy = false;
            _startButton.IsEnabled = true;
            _stopButton.IsEnabled = false;
            _statusText.Text = $"任务未能启动：{ex.Message}";
        }
    }

    private void CreateAdapter(string providerId)
    {
        if (providerId == AgentProviderIds.Codex)
        {
            var adapter = new CodexAppServerAdapter();
            _codex = adapter;
            _adapter = adapter;
            _launcher = adapter;
            adapter.ConnectionLost += message => Dispatcher.UIThread.Post(() => FinishWithoutCatch("连接中断：" + message));
            adapter.RequestReceived += request => Dispatcher.UIThread.Post(() => ShowCodexApproval(request));
        }
        else if (providerId == AgentProviderIds.DeepSeekHarness)
        {
            var patchPath = System.IO.Path.Combine(AppContext.BaseDirectory, "DshBridge", "cordis.patch.yml");
            var adapter = new DeepSeekHarnessAdapter(executable: _executableBox.Text?.Trim(), patchFile: patchPath);
            _adapter = adapter;
            _launcher = adapter;
            _approvalResponder = adapter;
            adapter.ApprovalPromptReceived += prompt => Dispatcher.UIThread.Post(() => ShowProviderApproval(prompt));
            adapter.ResumableSessionResolved += (thread, session) => Dispatcher.UIThread.Post(() =>
            {
                if (string.Equals(thread, _activeSessionId, StringComparison.Ordinal))
                {
                    _settings.ProviderSessions[AgentProviderIds.DeepSeekHarness] = session;
                    _settings.ProviderSessionWorkspaces[AgentProviderIds.DeepSeekHarness] = _workspaceBox.Text?.Trim() ?? "";
                    try { _settingsStore.Save(_settings); } catch (Exception ex) { _statusText.Text = "会话状态未能保存：" + ex.Message; }
                }
            });
        }
        else
        {
            var executable = _executableBox.Text?.Trim();
            var helperPath = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "CodexPond.Mac");
            var adapter = new ClaudeCodeAgentAdapter(executable: executable, applicationExe: helperPath);
            _adapter = adapter;
            _launcher = adapter;
            _approvalResponder = adapter;
            adapter.ApprovalPromptReceived += prompt => Dispatcher.UIThread.Post(() => ShowProviderApproval(prompt));
        }
        if (_launcher is not null) _launcher.AgentEventReceived += HandleAgentEvent;
    }

    private async Task StopAsync()
    {
        if (!_turnBusy || _launcher is null || _activeSessionId is null || _activeTurnId is null) return;
        _stopRequested = true;
        _statusText.Text = _activeProviderId == AgentProviderIds.WorkBuddy
            ? "正在中断 Claude Code 外层进程；已启动的 Work Buddy 工作流可能继续。"
            : "正在请求停止；等待提供方终态。";
        _stopButton.IsEnabled = false;
        try { await _launcher.InterruptTurnAsync(_activeSessionId, _activeTurnId); }
        catch (Exception ex) { _statusText.Text = "停止请求未确认：" + ex.Message; }
    }

    private void HandleAgentEvent(AgentEvent agentEvent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_turnBusy || !string.Equals(agentEvent.ProviderId, _activeProviderId, StringComparison.Ordinal)) return;
            if (string.IsNullOrWhiteSpace(_activeSessionId) ||
                string.IsNullOrWhiteSpace(agentEvent.SessionId) ||
                !string.Equals(agentEvent.SessionId, _activeSessionId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(agentEvent.TurnId)) return;

            if (agentEvent.Kind == AgentEventKind.Started)
            {
                if (_activeTurnId is null) _activeTurnId = agentEvent.TurnId;
                else if (!string.Equals(agentEvent.TurnId, _activeTurnId, StringComparison.Ordinal)) return;
            }
            else if (_activeTurnId is null || !string.Equals(agentEvent.TurnId, _activeTurnId, StringComparison.Ordinal)) return;

            switch (agentEvent.Kind)
            {
                case AgentEventKind.Started:
                    _statusText.Text = "任务进行中…";
                    break;
                case AgentEventKind.StepStarted:
                    _statusText.Text = string.IsNullOrWhiteSpace(agentEvent.Text) ? "Agent 正在执行一步…" : "正在处理：" + agentEvent.Text;
                    break;
                case AgentEventKind.OutputDelta:
                    if (!string.IsNullOrEmpty(agentEvent.Text)) AppendOutput(agentEvent.Text);
                    break;
                case AgentEventKind.ApprovalRequired:
                    _statusText.Text = "Agent 正在等待你的明确授权。";
                    break;
                case AgentEventKind.Completed when string.Equals(agentEvent.Status, "completed", StringComparison.OrdinalIgnoreCase):
                    if (!string.IsNullOrEmpty(agentEvent.Text)) AppendOutput(agentEvent.Text);
                    var completion = _stopRequested
                        ? "任务已停止，本轮未收鱼。"
                        : RecordFish(_activeTurnId!);
                    FinishWithoutCatch(completion);
                    break;
                case AgentEventKind.Failed:
                    FinishWithoutCatch("任务失败；没有记鱼。" + (agentEvent.Text is null ? "" : " " + agentEvent.Text));
                    break;
                case AgentEventKind.Cancelled:
                    FinishWithoutCatch("任务已取消；没有记鱼。");
                    break;
                case AgentEventKind.Disconnected:
                    FinishWithoutCatch("连接中断；没有记鱼。");
                    break;
            }
        });
    }

    private string RecordFish(string turnId)
    {
        var now = DateTimeOffset.Now;
        var environment = PondEnvironment.At(now.LocalDateTime);
        var context = new FishCatchContext(environment, null, 0, now);
        try
        {
            if (!_fishBook.TryCatchCompletedTurn("completed", turnId, context, out var record) || record is null)
                return "任务完成；该 turn 已记录过，未重复收鱼。";
            var read = _fishBook.ReadResult();
            if (read.CanAppend && AccessoryUnlockRules.Apply(_settings, read.Records)) _settingsStore.Save(_settings);
            _onFishCaught($"任务收获「{record.FishName}」");
            return $"任务完成，收获「{record.FishName}」并记入鱼册。";
        }
        catch (Exception ex) { return "任务完成，但鱼册写入失败；原有记录已保留。" + ex.Message; }
    }

    private void ShowProviderApproval(AgentApprovalPrompt prompt)
    {
        if (!IsCurrentApproval(prompt.ProviderId, prompt.SessionId, prompt.TurnId)) return;
        _pendingProviderPrompt = prompt;
        _pendingCodexRequest = null;
        _approvalAnswers.Children.Clear();
        _approvalTitle.Text = prompt.Title;
        _approvalDetails.Text = $"{prompt.Details}\n范围：{prompt.Scope}";
        _allowButton.IsVisible = prompt.CanAllowOnce;
        _rejectButton.IsVisible = prompt.CanReject;
        _approvalPanel.IsVisible = true;
        _statusText.Text = "等待你决定是否允许这一次操作。";
    }

    private void ShowCodexApproval(CodexApprovalRequest request)
    {
        if (!IsCurrentApproval(AgentProviderIds.Codex, request.SessionId, request.TurnId)) return;
        _pendingCodexRequest = request;
        _pendingProviderPrompt = null;
        _approvalAnswers.Children.Clear();
        _codexAnswers.Clear();
        _approvalTitle.Text = request.Title;
        _approvalDetails.Text = request.Details;
        _allowButton.IsVisible = request.CanGrantOnce && request.Kind != ApprovalKind.UserInput;
        _rejectButton.IsVisible = true;
        if (request.Kind == ApprovalKind.UserInput)
        {
            foreach (var question in request.Questions)
            {
                var prompt = new TextBlock { Text = question.Header + " · " + question.Prompt, FontSize = 11, Foreground = Brush("#554B39"), TextWrapping = TextWrapping.Wrap };
                var answer = new TextBox { PlaceholderText = question.IsSecret ? "请填写（不会保存到鱼册）" : "填写回答" };
                _approvalAnswers.Children.Add(prompt);
                _approvalAnswers.Children.Add(answer);
                _codexAnswers.Add((question, answer));
            }
            _allowButton.Content = "提交回答";
            _allowButton.IsVisible = request.Questions.Count > 0;
        }
        else _allowButton.Content = "本次允许";
        _rejectButton.Content = request.DeclineLabel;
        _approvalPanel.IsVisible = true;
        _statusText.Text = "Codex 正在等待你的决定。";
    }

    private async Task RespondToApprovalAsync(bool allowOnce)
    {
        try
        {
            if (_pendingCodexRequest is { } request && _codex is not null)
            {
                if (request.Kind == ApprovalKind.UserInput && allowOnce)
                {
                    var answers = _codexAnswers.ToDictionary(item => item.Question.Id, item => item.Answer.Text ?? "", StringComparer.Ordinal);
                    if (answers.Values.Any(string.IsNullOrWhiteSpace)) { _statusText.Text = "请填写所有问题后再提交。"; return; }
                    await _codex.RespondToUserInputAsync(request, answers);
                }
                else if (request.CanGrantOnce || !allowOnce) await _codex.RespondToApprovalAsync(request, allowOnce);
                else await _codex.RejectUnsupportedRequestAsync(request);
            }
            else if (_pendingProviderPrompt is { } prompt && _approvalResponder is not null)
                await _approvalResponder.RespondToApprovalAsync(prompt, allowOnce);
            _approvalPanel.IsVisible = false;
            _pendingCodexRequest = null;
            _pendingProviderPrompt = null;
            _codexAnswers.Clear();
            _statusText.Text = allowOnce ? "已提交本次授权；任务继续。" : "已拒绝该操作；等待任务结果。";
        }
        catch (Exception ex) { _statusText.Text = "授权回应未能发送：" + ex.Message; }
    }

    private bool IsCurrentApproval(string providerId, string? sessionId, string? turnId) =>
        _turnBusy &&
        string.Equals(providerId, _activeProviderId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(_activeSessionId) &&
        !string.IsNullOrWhiteSpace(sessionId) &&
        string.Equals(sessionId, _activeSessionId, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(_activeTurnId) &&
        !string.IsNullOrWhiteSpace(turnId) &&
        string.Equals(turnId, _activeTurnId, StringComparison.Ordinal);

    private void FinishWithoutCatch(string message)
    {
        _turnBusy = false;
        _startButton.IsEnabled = true;
        _stopButton.IsEnabled = false;
        _approvalPanel.IsVisible = false;
        _statusText.Text = message;
    }

    private async Task DisposeCurrentAdapterAsync()
    {
        if (_adapter is not null)
        {
            try { await _adapter.DisposeAsync(); } catch (Exception) { }
        }
        _adapter = null;
        _launcher = null;
        _approvalResponder = null;
        _codex = null;
        _activeSessionId = null;
        _activeTurnId = null;
    }

    private void ConfigureExecutable(string providerId)
    {
        _executableBox.Text = providerId switch
        {
            AgentProviderIds.Codex => _settings.CodexExecutable,
            AgentProviderIds.DeepSeekHarness => _settings.DeepSeekExecutable,
            _ => _settings.ClaudeExecutable
        };
    }

    private void SaveExecutable(string providerId, string executable)
    {
        switch (providerId)
        {
            case AgentProviderIds.Codex: _settings.CodexExecutable = executable; break;
            case AgentProviderIds.DeepSeekHarness: _settings.DeepSeekExecutable = executable; break;
            default: _settings.ClaudeExecutable = executable; break;
        }
    }

    private void ProviderChanged()
    {
        if (_providerPicker.SelectedIndex < 0) return;
        var provider = AgentProviderCatalog.Available[_providerPicker.SelectedIndex];
        _settings.AgentProviderId = provider.Id;
        _providerNote.Text = provider.Id switch
        {
            AgentProviderIds.Codex => "Codex 使用 codex app-server；池塘显示逐次授权与回复。",
            AgentProviderIds.DeepSeekHarness => "DeepSeek Harness 使用 headless CLI 和应用自有一次性审批桥；需要已安装并登录的 dsh。",
            _ => "Work Buddy 通过 Claude Code 与本机 MCP 配置运行；Work Buddy 长期流程授权仍留在其原生界面。"
        };
        ConfigureExecutable(provider.Id);
    }

    private AgentProviderOption CurrentProvider() => AgentProviderCatalog.Available[Math.Max(0, _providerPicker.SelectedIndex)];
    private static int ProviderIndex(string id) => Math.Max(0, AgentProviderCatalog.Available.ToList().FindIndex(provider => provider.Id == id));

    private void AppendOutput(string text)
    {
        _outputBox.Text += text;
        _outputBox.CaretIndex = _outputBox.Text?.Length ?? 0;
    }

    private static Button MakeButton(string content, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click)
    {
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(11, 6),
            Background = Brush("#435D4C"),
            Foreground = Brush("#F3D9A2"),
            BorderBrush = Brush("#A67A47"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        button.Click += click;
        return button;
    }

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
}
