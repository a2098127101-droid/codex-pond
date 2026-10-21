using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using Control = System.Windows.Controls.Control;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace CodexPond;

public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _text;

    public ChatMessage(string speaker, string text, bool isUser)
    {
        Speaker = speaker;
        _text = text;
        IsUser = isUser;
    }

    public string Speaker { get; }
    public bool IsUser { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class TaskPanelWindow : Window
{
    private readonly Dictionary<string, Control> _questionControls = new(StringComparer.Ordinal);
    private CodexApprovalRequest? _currentRequest;
    private AgentApprovalPrompt? _currentAgentApproval;
    private ChatMessage? _streamingReply;
    private bool _isBusy;
    private bool _stopPending;
    private bool _suppressProviderChanged;
    private string _providerLabel = "Codex";

    public TaskPanelWindow(Window owner, string workspace)
    {
        InitializeComponent();
        Owner = owner;
        DataContext = this;
        SetWorkspace(workspace);
        ProviderComboBox.ItemsSource = AgentProviderCatalog.Available;
        ProviderComboBox.DisplayMemberPath = nameof(AgentProviderOption.Label);
        ProviderComboBox.SelectedValuePath = nameof(AgentProviderOption.Id);
        SetProviderSelection(AgentProviderIds.Codex);
        AddNotice("选择本地工作目录和 Agent，写下一件要做的事。只有明确成功完成的一轮才会钓起一条鱼。");
        Messages.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(MessagesScrollViewer.ScrollToEnd);
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Hide();
        };
    }

    public ObservableCollection<ChatMessage> Messages { get; } = [];

    public event Action<string>? WorkspaceChanged;
    public event Action<string>? PromptSubmitted;
    public event Action? CancelRequested;
    public event Action? NewThreadRequested;
    public event Action? FishBookRequested;
    public event Action? AppearanceRequested;
    public event Action? IndependentModeRequested;
    public event Action? WorkBuddyStatusRequested;
    public event Action<string>? ProviderSelected;
    public event Action<CodexApprovalRequest, bool>? RequestDecision;
    public event Action<CodexApprovalRequest, IReadOnlyDictionary<string, string>>? UserInputSubmitted;
    public event Action<AgentApprovalPrompt, bool>? AgentApprovalDecision;

    public string WorkspacePath { get; private set; } = "";
    private bool _allowClose;

    public void CloseForShutdown()
    {
        _allowClose = true;
        Close();
    }

    public void SetWorkspace(string workspace)
    {
        WorkspacePath = workspace;
        WorkspacePathText.Text = string.IsNullOrWhiteSpace(workspace) ? "尚未选择工作目录" : workspace;
        UpdateControls();
    }

    public void SetStatus(string status) => StatusText.Text = status;

    public void SetProviderSelection(string providerId)
    {
        _suppressProviderChanged = true;
        var selectedId = AgentProviderCatalog.Available.Any(provider => provider.Id == providerId) ? providerId : AgentProviderIds.Codex;
        ProviderComboBox.SelectedValue = selectedId;
        _providerLabel = AgentProviderCatalog.Label(selectedId);
        ProviderComboBox.ToolTip = AgentProviderCatalog.Available.First(provider => provider.Id == selectedId).Guidance;
        _suppressProviderChanged = false;
    }

    public void RequestWorkspaceSelection() => SelectWorkspace();

    public void SetBusy(bool busy, bool canCancel)
    {
        _isBusy = busy;
        if (!busy) _stopPending = false;
        SubmitButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(WorkspacePath);
        SubmitButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        CancelTurnButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelTurnButton.IsEnabled = busy && canCancel && !_stopPending;
        CancelTurnButton.Content = _stopPending ? "正在请求停止…" : "停止当前任务";
        NewThreadButton.IsEnabled = !busy;
        UpdateControls();
    }

    public void SetStopPending(bool pending)
    {
        _stopPending = pending;
        CancelTurnButton.Content = pending ? "正在请求停止…" : "停止当前任务";
        CancelTurnButton.IsEnabled = _isBusy && !_stopPending;
    }

    public void AddUserMessage(string text)
    {
        _streamingReply = null;
        AddMessage(new ChatMessage("你", text, true));
        _streamingReply = new ChatMessage(_providerLabel, "正在岸边整理思路…", false);
        AddMessage(_streamingReply);
    }

    public void AppendAssistantDelta(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_streamingReply is null)
        {
            _streamingReply = new ChatMessage(_providerLabel, "", false);
            AddMessage(_streamingReply);
        }
        if (_streamingReply.Text == "正在岸边整理思路…") _streamingReply.Text = "";
        _streamingReply.Text += text;
    }

    public void FinishAssistantMessage(string? finalText)
    {
        if (!string.IsNullOrWhiteSpace(finalText))
        {
            if (_streamingReply is null)
            {
                _streamingReply = new ChatMessage(_providerLabel, finalText, false);
                AddMessage(_streamingReply);
            }
            else
            {
                _streamingReply.Text = finalText;
            }
        }
        else if (_streamingReply is not null && _streamingReply.Text == "正在岸边整理思路…")
        {
            _streamingReply.Text = "这一轮没有收到文字回复。";
        }
        _streamingReply = null;
    }

    public void AddNotice(string text)
    {
        _streamingReply = null;
        AddMessage(new ChatMessage("池边记录", text, false));
    }

    public void ShowRequest(CodexApprovalRequest request)
    {
        _currentRequest = request;
        _currentAgentApproval = null;
        RequestTitleText.Text = request.Title;
        RequestDetailsText.Text = request.Details;
        RequestNoteText.Text = request.Kind switch
        {
            ApprovalKind.CommandExecution or ApprovalKind.FileChange or ApprovalKind.ExecCommand or ApprovalKind.ApplyPatch =>
                "允许只会回应当前 Codex 请求；Codex 自身仍受本机沙箱和审批设置约束。",
            ApprovalKind.Permissions =>
                "允许仅回应本轮权限请求，不保存为会话级授权。",
            ApprovalKind.UserInput =>
                "请补全下方问题。答案会直接发送给当前 Codex turn。",
            ApprovalKind.McpElicitation =>
                "当前版本不能构造该工具的专用输入表单。可以取消这次请求。",
            _ => "当前版本不能处理此交互。关闭后会向 App Server 返回不支持的请求错误。"
        };
        RequestFieldsPanel.Children.Clear();
        _questionControls.Clear();

        if (request.Kind == ApprovalKind.UserInput)
        {
            foreach (var question in request.Questions)
            {
                var field = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
                field.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(question.Header) ? question.Prompt : $"{question.Header} · {question.Prompt}",
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
                    FontSize = 10,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(61, 53, 39))
                });

                Control input;
                if (question.IsSecret)
                {
                    input = new PasswordBox
                    {
                        MinHeight = 27,
                        Margin = new Thickness(0, 3, 0, 0),
                        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
                        FontSize = 11
                    };
                }
                else if (question.Options.Count > 0)
                {
                    var combo = new ComboBox
                    {
                        ItemsSource = question.Options,
                        SelectedIndex = 0,
                        IsEditable = question.IsOther,
                        MinHeight = 27,
                        Margin = new Thickness(0, 3, 0, 0),
                        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
                        FontSize = 11
                    };
                    input = combo;
                }
                else
                {
                    input = new TextBox
                    {
                        MinHeight = 27,
                        Padding = new Thickness(5, 3, 5, 3),
                        Margin = new Thickness(0, 3, 0, 0),
                        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI"),
                        FontSize = 11
                    };
                }

                var accessibleQuestion = string.IsNullOrWhiteSpace(question.Header)
                    ? question.Prompt
                    : $"{question.Header}：{question.Prompt}";
                System.Windows.Automation.AutomationProperties.SetName(input, accessibleQuestion);
                System.Windows.Automation.AutomationProperties.SetHelpText(input, question.IsSecret
                    ? "将此回答发送给当前 Codex 请求。"
                    : "请填写此 Codex 请求所需的回答。");
                field.Children.Add(input);
                RequestFieldsPanel.Children.Add(field);
                _questionControls[question.Id] = input;
            }
        }

        RequestPrimaryButton.Content = request.Kind switch
        {
            ApprovalKind.UserInput => "提交回答",
            ApprovalKind.FileChange => "接受本次变更",
            ApprovalKind.Permissions => "仅本轮允许",
            ApprovalKind.ApplyPatch => "应用补丁",
            ApprovalKind.ExecCommand => "批准本次命令",
            _ => "允许一次"
        };
        RequestPrimaryButton.Visibility = request.Kind is ApprovalKind.McpElicitation or ApprovalKind.Unsupported
            ? Visibility.Collapsed
            : Visibility.Visible;
        RequestPrimaryButton.IsEnabled = request.CanGrantOnce;
        if (request.Kind == ApprovalKind.CommandExecution && !request.CanGrantOnce)
            RequestNoteText.Text = request.AvailableDecisions.Contains("acceptForSession", StringComparer.Ordinal)
                ? "Codex 只提供会话级允许；本应用不会扩大为会话授权。你可以拒绝请求，或停止当前任务。"
                : "当前请求没有可用的一次性允许选项；你可以拒绝请求，或停止当前任务。";
        RequestDeclineButton.Content = request.Kind switch
        {
            ApprovalKind.UserInput => "停止当前任务",
            ApprovalKind.McpElicitation => "取消询问",
            ApprovalKind.Unsupported => "关闭并拒绝请求",
            _ => request.DeclineLabel
        };
        RequestDeclineButton.Visibility = request.Kind == ApprovalKind.CommandExecution && request.DeclineDecision is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        RequestDeclineButton.IsEnabled = true;
        RequestCard.Visibility = Visibility.Visible;
        PromptTextBox.IsEnabled = false;
        SubmitButton.IsEnabled = false;
        SetStatus("Codex 正在等你处理岸边请求");
    }

    public void ShowExternalApproval(AgentApprovalPrompt prompt)
    {
        _currentRequest = null;
        _currentAgentApproval = prompt;
        _questionControls.Clear();
        RequestFieldsPanel.Children.Clear();
        RequestTitleText.Text = prompt.Title;
        RequestDetailsText.Text = prompt.Details.Length > 2400 ? prompt.Details[..2400] + "…" : prompt.Details;
        RequestNoteText.Text = $"{AgentProviderCatalog.Label(prompt.ProviderId)} · {prompt.Scope}";
        RequestPrimaryButton.Content = "仅允许这一次";
        RequestPrimaryButton.Visibility = prompt.CanAllowOnce ? Visibility.Visible : Visibility.Collapsed;
        RequestPrimaryButton.IsEnabled = prompt.CanAllowOnce;
        RequestDeclineButton.Content = "拒绝这一次";
        RequestDeclineButton.Visibility = prompt.CanReject ? Visibility.Visible : Visibility.Collapsed;
        RequestDeclineButton.IsEnabled = true;
        RequestCard.Visibility = Visibility.Visible;
        PromptTextBox.IsEnabled = false;
        SubmitButton.IsEnabled = false;
        SetStatus($"{AgentProviderCatalog.Label(prompt.ProviderId)} 正在等待你处理权限请求");
        UpdateControls();
    }

    public void ClearRequest()
    {
        _currentRequest = null;
        _currentAgentApproval = null;
        _questionControls.Clear();
        RequestFieldsPanel.Children.Clear();
        RequestCard.Visibility = Visibility.Collapsed;
        UpdateControls();
    }

    private void AddMessage(ChatMessage message)
    {
        Messages.Add(message);
        while (Messages.Count > 80) Messages.RemoveAt(0);
    }

    private void UpdateControls()
    {
        WorkspacePathText.Text = string.IsNullOrWhiteSpace(WorkspacePath) ? "尚未选择工作目录" : WorkspacePath;
        WorkspacePathText.ToolTip = WorkspacePathText.Text;
        var canSelectWorkspace = _currentRequest is null && !_isBusy;
        WorkspaceButton.IsEnabled = canSelectWorkspace;
        PromptTextBox.IsEnabled = canSelectWorkspace;
        SubmitButton.IsEnabled = canSelectWorkspace && !string.IsNullOrWhiteSpace(WorkspacePath);
        NewThreadButton.IsEnabled = canSelectWorkspace;
        ProviderComboBox.IsEnabled = !_isBusy && _currentRequest is null && _currentAgentApproval is null;
        WorkBuddyStatusButton.Visibility = ProviderComboBox.SelectedValue is string selectedProvider && selectedProvider == AgentProviderIds.WorkBuddy
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SubmitButton_Click(object sender, RoutedEventArgs e) => SubmitPrompt();

    private void SubmitPrompt()
    {
        var prompt = PromptTextBox.Text.Trim();
        if (prompt.Length == 0) return;
        if (string.IsNullOrWhiteSpace(WorkspacePath))
        {
            SelectWorkspace();
            if (string.IsNullOrWhiteSpace(WorkspacePath)) return;
        }
        PromptTextBox.Clear();
        PromptSubmitted?.Invoke(prompt);
    }

    private void PromptTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            SubmitPrompt();
        }
    }

    private void CancelTurnButton_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
    private void NewThreadButton_Click(object sender, RoutedEventArgs e) => NewThreadRequested?.Invoke();
    private void FishBookButton_Click(object sender, RoutedEventArgs e) => FishBookRequested?.Invoke();
    private void AppearanceButton_Click(object sender, RoutedEventArgs e) => AppearanceRequested?.Invoke();
    private void IndependentModeButton_Click(object sender, RoutedEventArgs e) => IndependentModeRequested?.Invoke();
    private void WorkBuddyStatusButton_Click(object sender, RoutedEventArgs e) => WorkBuddyStatusRequested?.Invoke();
    private void HideButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void ProviderComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProviderChanged || ProviderComboBox.SelectedValue is not string providerId) return;
        _providerLabel = AgentProviderCatalog.Label(providerId);
        var selected = AgentProviderCatalog.Available.FirstOrDefault(provider => provider.Id == providerId);
        if (selected is not null) ProviderComboBox.ToolTip = selected.Guidance;
        ProviderSelected?.Invoke(providerId);
    }

    private void WorkspaceButton_Click(object sender, RoutedEventArgs e) => SelectWorkspace();

    private void SelectWorkspace()
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择 Codex 可以工作的本地目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(WorkspacePath)
                ? WorkspacePath
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        var owner = new DialogOwnerWindow(new WindowInteropHelper(this).Handle);
        if (dialog.ShowDialog(owner) != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;
        WorkspaceChanged?.Invoke(dialog.SelectedPath);
    }

    private void RequestPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentAgentApproval is { } agentPrompt)
        {
            if (!agentPrompt.CanAllowOnce) return;
            AgentApprovalDecision?.Invoke(agentPrompt, true);
            RequestPrimaryButton.IsEnabled = false;
            RequestDeclineButton.IsEnabled = false;
            SetStatus("已提交一次性允许请求，正在等待 Agent 继续");
            return;
        }
        var request = _currentRequest;
        if (request is null) return;
        if (request.Kind == ApprovalKind.UserInput)
        {
            var answers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in _questionControls)
            {
                answers[pair.Key] = pair.Value switch
                {
                    PasswordBox password => password.Password,
                    ComboBox combo => combo.Text,
                    TextBox text => text.Text,
                    _ => ""
                };
            }
            if (answers.Values.Any(string.IsNullOrWhiteSpace))
            {
                SetStatus("请回答每个问题；如需退出，请使用“停止当前任务”");
                return;
            }
            UserInputSubmitted?.Invoke(request, answers);
        }
        else if (!request.CanGrantOnce)
        {
            return;
        }
        else
        {
            RequestDecision?.Invoke(request, true);
        }
    }

    private void RequestDeclineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentAgentApproval is { } agentPrompt)
        {
            AgentApprovalDecision?.Invoke(agentPrompt, false);
            RequestPrimaryButton.IsEnabled = false;
            RequestDeclineButton.IsEnabled = false;
            SetStatus("已提交拒绝决定，正在等待 Agent 继续");
            return;
        }
        var request = _currentRequest;
        if (request is null) return;
        if (request.Kind == ApprovalKind.UserInput)
        {
            CancelRequested?.Invoke();
            SetStatus("正在请求停止当前任务；停止确认前保留这项问题");
        }
        else
        {
            RequestDecision?.Invoke(request, false);
        }
    }

    private sealed class DialogOwnerWindow : Forms.IWin32Window
    {
        public DialogOwnerWindow(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }
}
