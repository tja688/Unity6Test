using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CodexUnity.Views
{
    /// <summary>
    /// 实例详情视图 - 显示单个实例的对话历史和控制
    /// </summary>
    public class InstanceDetailView : VisualElement
    {
        private readonly string _instanceId;
        private CodexRunnerInstance _runner;
        private InstanceInfo _instanceInfo;

        // UI 元素
        private TextField _promptField;
        private TextField _modelField;
        private DropdownField _effortField;
        private ScrollView _historyScroll;
        private Label _statusTextLabel;
        private Label _statusMetaLabel;
        private VisualElement _statusIndicator;
        private HelpBox _statusBox;
        private Button _sendButton;
        private Button _killButton;
        private Button _backButton;
        private Label _instanceNameLabel;
        private Label _foldoutArrow;
        private VisualElement _promptContent;
        private bool _isPromptExpanded = true;

        private VisualTreeAsset _bubbleTemplate;

        // 消息归并状态
        private ChatBubbleElement _currentAssistantBubble;
        private string _currentRunId;
        private readonly StringBuilder _streamBuffer = new();
        private int _streamLineCount;
        private double _lastScrollTime;

        private bool _codexAvailable;
        private bool _hasGitRepo;

        public event Action OnBackRequested;

        public InstanceDetailView(string instanceId)
        {
            _instanceId = instanceId;
            _runner = InstanceManager.Instance.GetOrCreateRunner(instanceId);
            _instanceInfo = InstanceManager.Instance.GetInstanceInfo(instanceId);

            style.flexGrow = 1;
            style.flexDirection = FlexDirection.Column;

            BuildUI();
            BindEvents();
            LoadConversation();
            RefreshRunStatus();
            UpdateSendState();
        }

        private void BuildUI()
        {
            // 加载资源
            var bubbleTemplate = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Editor/CodexUnity/UI/ChatBubble.uxml");
            var bubbleStyle = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/Editor/CodexUnity/UI/ChatBubble.uss");
            var windowStyle = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/Editor/CodexUnity/UI/CodexWindow.uss");
            _bubbleTemplate = bubbleTemplate;

            if (windowStyle != null) styleSheets.Add(windowStyle);
            if (bubbleStyle != null) styleSheets.Add(bubbleStyle);

            // === 顶部导航栏 (包含返回、标题、状态) ===
            var navBar = new VisualElement();
            navBar.style.flexDirection = FlexDirection.Row;
            navBar.style.alignItems = Align.Center;
            navBar.style.paddingTop = 6;
            navBar.style.paddingBottom = 6;
            navBar.style.paddingLeft = 10;
            navBar.style.paddingRight = 10;
            navBar.style.backgroundColor = new Color(0.133f, 0.133f, 0.157f);
            navBar.style.borderBottomWidth = 1;
            navBar.style.borderBottomColor = new Color(1, 1, 1, 0.06f);

            // 返回按钮
            _backButton = new Button(() => OnBackRequested?.Invoke());
            _backButton.text = "←";
            _backButton.AddToClassList("ghost-button");
            _backButton.style.fontSize = 14;
            _backButton.style.paddingLeft = 6;
            _backButton.style.paddingRight = 6;
            navBar.Add(_backButton);

            // 实例名称 (可编辑)
            _instanceNameLabel = new Label(_instanceInfo?.name ?? $"Instance {_instanceId[..8]}");
            _instanceNameLabel.style.fontSize = 13;
            _instanceNameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _instanceNameLabel.style.marginLeft = 8;
            _instanceNameLabel.style.color = new Color(0.91f, 0.91f, 0.93f);
            _instanceNameLabel.style.flexGrow = 1;
            navBar.Add(_instanceNameLabel);

            // 重命名按钮
            var renameButton = new Button(ShowRenameDialog);
            renameButton.text = "✎";
            renameButton.AddToClassList("ghost-button");
            renameButton.style.fontSize = 12;
            renameButton.style.marginLeft = 4;
            renameButton.style.paddingLeft = 4;
            renameButton.style.paddingRight = 4;
            navBar.Add(renameButton);

            // 状态指示器
            _statusIndicator = new VisualElement();
            _statusIndicator.style.width = 8;
            _statusIndicator.style.height = 8;
            _statusIndicator.style.borderTopLeftRadius = 4;
            _statusIndicator.style.borderTopRightRadius = 4;
            _statusIndicator.style.borderBottomLeftRadius = 4;
            _statusIndicator.style.borderBottomRightRadius = 4;
            _statusIndicator.style.marginLeft = 10;
            _statusIndicator.style.backgroundColor = new Color(0.42f, 0.42f, 0.47f);
            navBar.Add(_statusIndicator);

            // 状态文本
            _statusTextLabel = new Label("Idle");
            _statusTextLabel.style.fontSize = 11;
            _statusTextLabel.style.marginLeft = 6;
            _statusTextLabel.style.color = new Color(0.66f, 0.66f, 0.69f);
            navBar.Add(_statusTextLabel);

            // 状态详情
            _statusMetaLabel = new Label("");
            _statusMetaLabel.style.fontSize = 9;
            _statusMetaLabel.style.marginLeft = 8;
            _statusMetaLabel.style.color = new Color(0.42f, 0.42f, 0.47f);
            navBar.Add(_statusMetaLabel);

            Add(navBar);

            // === 主内容区域 ===
            var mainContent = new VisualElement();
            mainContent.style.flexGrow = 1;
            mainContent.style.flexDirection = FlexDirection.Column;
            mainContent.style.paddingTop = 8;
            mainContent.style.paddingBottom = 8;
            mainContent.style.paddingLeft = 10;
            mainContent.style.paddingRight = 10;
            mainContent.style.backgroundColor = new Color(0.102f, 0.102f, 0.122f);

            // === 对话区域 (更大空间) ===
            var historyCard = new VisualElement();
            historyCard.AddToClassList("card");
            historyCard.style.flexGrow = 1;
            historyCard.style.backgroundColor = new Color(0.165f, 0.165f, 0.196f);
            historyCard.style.borderTopLeftRadius = 8;
            historyCard.style.borderTopRightRadius = 8;
            historyCard.style.borderBottomLeftRadius = 8;
            historyCard.style.borderBottomRightRadius = 8;
            historyCard.style.paddingTop = 8;
            historyCard.style.paddingBottom = 8;
            historyCard.style.paddingLeft = 8;
            historyCard.style.paddingRight = 8;
            historyCard.style.marginBottom = 6;

            _historyScroll = new ScrollView(ScrollViewMode.Vertical);
            _historyScroll.style.flexGrow = 1;
            _historyScroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            historyCard.Add(_historyScroll);
            mainContent.Add(historyCard);

            // === 可折叠 Prompt 区域 ===
            var promptCard = new VisualElement();
            promptCard.AddToClassList("card");
            promptCard.style.backgroundColor = new Color(0.165f, 0.165f, 0.196f);
            promptCard.style.borderTopLeftRadius = 8;
            promptCard.style.borderTopRightRadius = 8;
            promptCard.style.borderBottomLeftRadius = 8;
            promptCard.style.borderBottomRightRadius = 8;
            promptCard.style.paddingTop = 6;
            promptCard.style.paddingBottom = 6;
            promptCard.style.paddingLeft = 8;
            promptCard.style.paddingRight = 8;

            // Prompt 标题栏 (可折叠 + 按钮)
            var promptHeader = new VisualElement();
            promptHeader.style.flexDirection = FlexDirection.Row;
            promptHeader.style.alignItems = Align.Center;
            promptHeader.style.justifyContent = Justify.SpaceBetween;

            // 左侧: 折叠箭头 + 标题
            var headerLeft = new VisualElement();
            headerLeft.style.flexDirection = FlexDirection.Row;
            headerLeft.style.alignItems = Align.Center;

            _foldoutArrow = new Label("▼");
            _foldoutArrow.style.fontSize = 10;
            _foldoutArrow.style.marginRight = 6;
            _foldoutArrow.style.color = new Color(0.66f, 0.66f, 0.69f);
            headerLeft.Add(_foldoutArrow);

            var promptTitle = new Label("Prompt");
            promptTitle.style.fontSize = 12;
            promptTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            promptTitle.style.color = new Color(0.91f, 0.91f, 0.93f);
            headerLeft.Add(promptTitle);

            // 点击标题折叠
            headerLeft.RegisterCallback<ClickEvent>(_ => TogglePromptFoldout());

            promptHeader.Add(headerLeft);

            // 右侧: 按钮
            var headerButtons = new VisualElement();
            headerButtons.style.flexDirection = FlexDirection.Row;

            _sendButton = new Button(Send);
            _sendButton.text = "Send";
            _sendButton.AddToClassList("primary-button");
            _sendButton.style.marginRight = 4;
            _sendButton.style.paddingTop = 4;
            _sendButton.style.paddingBottom = 4;
            headerButtons.Add(_sendButton);

            _killButton = new Button(KillRun);
            _killButton.text = "Kill";
            _killButton.AddToClassList("danger-button");
            _killButton.style.paddingTop = 4;
            _killButton.style.paddingBottom = 4;
            headerButtons.Add(_killButton);

            promptHeader.Add(headerButtons);
            promptCard.Add(promptHeader);

            // Prompt 内容区域 (可折叠)
            _promptContent = new VisualElement();
            _promptContent.style.marginTop = 6;

            // 选项行
            var optionsRow = new VisualElement();
            optionsRow.style.flexDirection = FlexDirection.Row;
            optionsRow.style.marginBottom = 6;

            _modelField = new TextField("Model");
            _modelField.value = _runner.State.model ?? "gpt-5.1-codex-mini";
            _modelField.style.flexGrow = 1;
            _modelField.style.marginRight = 6;
            optionsRow.Add(_modelField);

            _effortField = new DropdownField("Effort", new List<string> { "minimal", "low", "medium", "high", "xhigh" }, 2);
            _effortField.value = _runner.State.effort ?? "medium";
            _effortField.style.flexGrow = 1;
            optionsRow.Add(_effortField);

            _promptContent.Add(optionsRow);

            // Prompt 输入框
            _promptField = new TextField();
            _promptField.multiline = true;
            _promptField.style.minHeight = 60;
            _promptField.style.maxHeight = 120;
            // 恢复草稿
            _promptField.value = _runner.State.draftPrompt ?? "";
            // 实时保存草稿
            _promptField.RegisterValueChangedCallback(evt =>
            {
                _runner.State.draftPrompt = evt.newValue;
                _runner.SaveState();
                UpdateSendState();
            });
            _promptContent.Add(_promptField);

            promptCard.Add(_promptContent);
            mainContent.Add(promptCard);

            // 状态消息框
            _statusBox = new HelpBox();
            _statusBox.style.display = DisplayStyle.None;
            _statusBox.style.marginTop = 4;
            mainContent.Add(_statusBox);

            Add(mainContent);
        }

        private void TogglePromptFoldout()
        {
            _isPromptExpanded = !_isPromptExpanded;
            _promptContent.style.display = _isPromptExpanded ? DisplayStyle.Flex : DisplayStyle.None;
            _foldoutArrow.text = _isPromptExpanded ? "▼" : "▶";
        }

        private void ShowRenameDialog()
        {
            var currentName = _instanceInfo?.name ?? "";
            var newName = EditorInputDialog.Show("重命名实例", "输入新名称:", currentName);
            if (!string.IsNullOrEmpty(newName) && newName != currentName)
            {
                InstanceManager.Instance.RenameInstance(_instanceId, newName);
                _instanceInfo = InstanceManager.Instance.GetInstanceInfo(_instanceId);
                _instanceNameLabel.text = newName;
            }
        }

        private void BindEvents()
        {
            _runner.OnHistoryItemAppended += OnHistoryItemAppended;
            _runner.OnStatusChanged += OnStatusChanged;

            _hasGitRepo = CodexStore.HasGitRepository();
            _codexAvailable = CodexRunnerInstance.CheckCodexAvailableStatic();
        }

        public void Cleanup()
        {
            if (_runner != null)
            {
                _runner.OnHistoryItemAppended -= OnHistoryItemAppended;
                _runner.OnStatusChanged -= OnStatusChanged;
            }
        }

        private void LoadConversation()
        {
            _historyScroll?.Clear();
            _currentAssistantBubble = null;
            _currentRunId = null;
            _streamBuffer.Clear();
            _streamLineCount = 0;

            var historyItems = _runner.LoadHistory();

            ChatBubbleElement lastAssistantBubble = null;
            StringBuilder assistantContent = new StringBuilder();

            foreach (var item in historyItems)
            {
                var kind = GetItemKind(item);

                if (kind == "user")
                {
                    if (lastAssistantBubble != null && assistantContent.Length > 0)
                    {
                        lastAssistantBubble.CompleteStream(GetFinalContent(assistantContent.ToString()), true);
                    }
                    lastAssistantBubble = null;
                    assistantContent.Clear();

                    var userBubble = CreateBubble();
                    userBubble.BindUserMessage(item.text, item.ts);
                    _historyScroll?.Add(userBubble);
                }
                else if (kind == "assistant")
                {
                    if (lastAssistantBubble != null)
                    {
                        assistantContent.AppendLine(item.text);
                    }
                    else
                    {
                        lastAssistantBubble = CreateBubble();
                        lastAssistantBubble.BindSystemMessage(item.text, item.ts);
                        lastAssistantBubble.Bind(item, false, 0);
                        _historyScroll?.Add(lastAssistantBubble);
                    }
                }
                else if (kind == "system")
                {
                    var sysBubble = CreateBubble();
                    sysBubble.BindSystemMessage(item.text, item.ts);
                    _historyScroll?.Add(sysBubble);
                }
            }

            if (lastAssistantBubble != null && assistantContent.Length > 0)
            {
                lastAssistantBubble.CompleteStream(GetFinalContent(assistantContent.ToString()), true);
            }

            ScrollToBottom();
        }

        private void OnHistoryItemAppended(HistoryItem item)
        {
            if (item == null) return;

            var kind = GetItemKind(item);

            if (kind == "user")
            {
                if (_currentAssistantBubble != null && _currentAssistantBubble.IsStreaming)
                {
                    _currentAssistantBubble.CompleteStream(GetFinalContent(_streamBuffer.ToString()), true);
                }

                _currentAssistantBubble = null;
                _streamBuffer.Clear();
                _streamLineCount = 0;

                var userBubble = CreateBubble();
                userBubble.BindUserMessage(item.text, item.ts);
                _historyScroll?.Add(userBubble);
                ScrollToBottom();
                return;
            }

            if (kind == "assistant")
            {
                if (_currentAssistantBubble != null && _currentAssistantBubble.IsStreaming)
                {
                    _currentAssistantBubble.CompleteStream(item.text, true);
                    _currentAssistantBubble = null;
                    _streamBuffer.Clear();
                    _streamLineCount = 0;
                }
                else
                {
                    var bubble = CreateBubble();
                    bubble.Bind(item, false, 0);
                    _historyScroll?.Add(bubble);
                }
                ScrollToBottom();
                return;
            }

            if (kind == "system")
            {
                var sysBubble = CreateBubble();
                sysBubble.BindSystemMessage(item.text, item.ts);
                _historyScroll?.Add(sysBubble);
                ScrollToBottom();
                return;
            }

            // 流式输出
            if (_currentAssistantBubble == null || !_currentAssistantBubble.IsStreaming)
            {
                _currentAssistantBubble = CreateBubble();
                _currentAssistantBubble.BindAssistantStreaming(item.runId, item.ts);
                _historyScroll?.Add(_currentAssistantBubble);
                _currentRunId = item.runId;
                _streamBuffer.Clear();
                _streamLineCount = 0;
            }

            if (!string.IsNullOrEmpty(item.text))
            {
                _streamBuffer.AppendLine(item.text);
                _streamLineCount++;
            }

            _currentAssistantBubble.UpdateStreamContent(_streamBuffer.ToString(), _streamLineCount);

            if (EditorApplication.timeSinceStartup - _lastScrollTime > 0.3)
            {
                _lastScrollTime = EditorApplication.timeSinceStartup;
                ScrollToBottom();
            }
        }

        private void OnStatusChanged(InstanceStatus status)
        {
            RefreshRunStatus();

            if (status != InstanceStatus.Running && _currentAssistantBubble != null && _currentAssistantBubble.IsStreaming)
            {
                var success = status == InstanceStatus.Completed;
                var finalContent = GetFinalContent(_streamBuffer.ToString());

                if (!string.IsNullOrEmpty(_runner.State.lastRunId))
                {
                    var outPath = CodexStore.GetOutPath(_runner.State.lastRunId);
                    if (System.IO.File.Exists(outPath))
                    {
                        try
                        {
                            var outContent = System.IO.File.ReadAllText(outPath, Encoding.UTF8);
                            if (!string.IsNullOrWhiteSpace(outContent))
                            {
                                finalContent = outContent;
                            }
                        }
                        catch { }
                    }
                }

                _currentAssistantBubble.CompleteStream(finalContent, success);
                _currentAssistantBubble = null;
                _streamBuffer.Clear();
                _streamLineCount = 0;
                ScrollToBottom();
            }
        }

        private ChatBubbleElement CreateBubble()
        {
            return new ChatBubbleElement(_bubbleTemplate);
        }

        private void ScrollToBottom()
        {
            if (_historyScroll == null || _historyScroll.contentContainer.childCount == 0) return;
            var last = _historyScroll.contentContainer[_historyScroll.contentContainer.childCount - 1];
            _historyScroll.schedule.Execute(() => _historyScroll.ScrollTo(last)).ExecuteLater(10);
        }

        private void RefreshRunStatus()
        {
            var state = _runner.State;
            var runId = state.activeRunId;
            var pid = state.activePid;
            var status = state.status;

            string headline = "Idle";
            Color statusColor = new Color(0.42f, 0.42f, 0.47f); // 灰色

            switch (status)
            {
                case InstanceStatus.Running:
                    if (pid > 0 && CodexRunnerInstance.IsProcessAlive(pid))
                    {
                        headline = "Running";
                        statusColor = new Color(0.15f, 0.87f, 0.51f); // 绿色
                    }
                    else
                    {
                        headline = "Warning";
                        statusColor = new Color(0.99f, 0.79f, 0.34f); // 黄色
                    }
                    break;
                case InstanceStatus.Completed:
                    headline = "Completed";
                    statusColor = new Color(0.15f, 0.87f, 0.51f); // 绿色
                    break;
                case InstanceStatus.Error:
                    headline = "Error";
                    statusColor = new Color(1f, 0.42f, 0.42f); // 红色
                    break;
            }

            // 更新状态指示器颜色
            _statusIndicator.style.backgroundColor = statusColor;
            _statusTextLabel.text = headline;
            _statusTextLabel.style.color = statusColor;

            // 更新状态详情
            var outputTime = _runner.LastOutputTime;
            var outputText = outputTime.HasValue ? outputTime.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "--";
            _statusMetaLabel.text = string.IsNullOrEmpty(runId)
                ? ""
                : $"run:{runId[..Math.Min(8, runId.Length)]} pid:{pid} @{outputText}";

            _killButton?.SetEnabled(status == InstanceStatus.Running);
            UpdateSendState();
        }

        private void UpdateSendState()
        {
            var isRunning = _runner.IsRunning || _runner.State.status == InstanceStatus.Running;
            var isCompiling = EditorApplication.isCompiling;
            var promptText = _promptField?.value ?? string.Empty;
            var canSend = !isRunning
                          && !isCompiling
                          && !string.IsNullOrWhiteSpace(promptText)
                          && _codexAvailable
                          && _hasGitRepo;

            _sendButton?.SetEnabled(canSend);
        }

        private void SetStatusMessage(string message, HelpBoxMessageType type)
        {
            if (_statusBox == null) return;

            if (string.IsNullOrEmpty(message))
            {
                _statusBox.style.display = DisplayStyle.None;
                return;
            }

            _statusBox.text = message;
            _statusBox.messageType = type;
            _statusBox.style.display = DisplayStyle.Flex;
        }

        private void Send()
        {
            SetStatusMessage(string.Empty, HelpBoxMessageType.Info);

            if (!_hasGitRepo)
            {
                SetStatusMessage("请先在项目根目录执行 git init", HelpBoxMessageType.Error);
                return;
            }

            if (!_codexAvailable)
            {
                SetStatusMessage("codex not found in PATH", HelpBoxMessageType.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(_promptField.value))
            {
                SetStatusMessage("请输入 prompt", HelpBoxMessageType.Warning);
                return;
            }

            var prompt = _promptField.value;

            // 添加用户消息到历史
            var userItem = new HistoryItem
            {
                ts = CodexStore.GetIso8601Timestamp(),
                kind = "user",
                role = "user",
                text = prompt,
                source = "ui"
            };
            CodexStore.AppendInstanceHistory(_instanceId, userItem);

            // 创建用户气泡
            var userBubble = CreateBubble();
            userBubble.BindUserMessage(prompt, userItem.ts);
            _historyScroll?.Add(userBubble);
            ScrollToBottom();

            _promptField.value = string.Empty;

            // 更新实例活跃时间
            InstanceManager.Instance.SetLastActiveInstance(_instanceId);

            var resume = _runner.State.hasActiveThread;
            var model = _modelField.value;
            var effort = _effortField.value;

            // 保存设置
            _runner.State.model = model;
            _runner.State.effort = effort;
            _runner.State.draftPrompt = "";  // 清空草稿
            _runner.SaveState();

            _runner.Execute(prompt, model, effort, resume,
                onComplete: _ =>
                {
                    RefreshRunStatus();
                    SetStatusMessage("运行完成", HelpBoxMessageType.Info);
                },
                onError: error =>
                {
                    SetStatusMessage(error, HelpBoxMessageType.Error);
                    if (_currentAssistantBubble != null && _currentAssistantBubble.IsStreaming)
                    {
                        _currentAssistantBubble.CompleteStream(error, false);
                        _currentAssistantBubble = null;
                    }
                });

            UpdateSendState();
        }



        private void KillRun()
        {
            if (!EditorUtility.DisplayDialog("强杀进程", "确定要强制终止当前进程吗？", "强杀", "取消"))
            {
                return;
            }

            _runner.KillActiveProcessTree();
            RefreshRunStatus();
        }

        private static string GetItemKind(HistoryItem item)
        {
            if (!string.IsNullOrEmpty(item.kind)) return item.kind;
            if (!string.IsNullOrEmpty(item.role)) return item.role;
            return "event";
        }

        private static string GetFinalContent(string streamContent)
        {
            if (string.IsNullOrWhiteSpace(streamContent))
            {
                return "Task completed.";
            }

            var lines = streamContent.Split('\n');
            var meaningfulLines = new List<string>();

            for (int i = lines.Length - 1; i >= 0 && meaningfulLines.Count < 20; i--)
            {
                var line = lines[i].Trim();
                if (!string.IsNullOrEmpty(line))
                {
                    meaningfulLines.Insert(0, line);
                }
            }

            return meaningfulLines.Count == 0 ? "Task completed." : string.Join("\n", meaningfulLines);
        }
    }
}
