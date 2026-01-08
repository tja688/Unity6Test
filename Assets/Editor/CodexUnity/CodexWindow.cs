using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace CodexUnity
{
    /// <summary>
    /// Codex Unity 主窗口 (UI Toolkit)
    /// 实现消息归并：每轮对话只显示两个气泡（用户 + 助手）
    /// </summary>
    public class CodexWindow : EditorWindow
    {
        private const string WindowUxmlPath = "Assets/Editor/CodexUnity/UI/CodexWindow.uxml";
        private const string WindowUssPath = "Assets/Editor/CodexUnity/UI/CodexWindow.uss";
        private const string BubbleUxmlPath = "Assets/Editor/CodexUnity/UI/ChatBubble.uxml";

        private TextField _promptField;
        private TextField _modelField;
        private DropdownField _effortField;
        private Toggle _debugToggle;
        private ScrollView _historyScroll;
        private Label _gitStatusLabel;
        private Label _codexStatusLabel;
        private Label _statusTextLabel;
        private Label _statusMetaLabel;
        private VisualElement _statusBar;
        private HelpBox _statusBox;
        private Button _sendButton;
        private Button _newTaskButton;
        private Button _killButton;
        private Button _openRunButton;
        private Button _copyCommandButton;

        private VisualTreeAsset _bubbleTemplate;

        private CodexState _state;
        private bool _codexAvailable;
        private string _codexVersion;
        private bool _hasGitRepo;

        // 消息归并状态
        private ChatBubbleElement _currentAssistantBubble;
        private string _currentRunId;
        private StringBuilder _streamBuffer = new StringBuilder();
        private int _streamLineCount;
        private double _lastScrollTime;

        [MenuItem("Tools/Codex")]
        public static void ShowWindow()
        {
            var window = GetWindow<CodexWindow>("Codex");
            window.minSize = new Vector2(300, 400);
            window.Show();
        }

        private void OnEnable()
        {
            CodexRunner.HistoryItemAppended += OnHistoryItemAppended;
            CodexRunner.RunStatusChanged += OnRunStatusChanged;
        }

        private void OnDisable()
        {
            CodexRunner.HistoryItemAppended -= OnHistoryItemAppended;
            CodexRunner.RunStatusChanged -= OnRunStatusChanged;
        }

        public void CreateGUI()
        {
            rootVisualElement.Clear();

            var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(WindowUxmlPath);
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(WindowUssPath);
            _bubbleTemplate = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(BubbleUxmlPath);

            if (visualTree == null)
            {
                rootVisualElement.Add(new Label("Missing UXML: " + WindowUxmlPath));
                return;
            }

            var root = visualTree.CloneTree();
            rootVisualElement.Add(root);

            if (styleSheet != null)
            {
                rootVisualElement.styleSheets.Add(styleSheet);
            }

            var bubbleStyle = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/Editor/CodexUnity/UI/ChatBubble.uss");
            if (bubbleStyle != null)
            {
                rootVisualElement.styleSheets.Add(bubbleStyle);
            }

            BindElements();
            RefreshData();
            CheckEnvironment();
            LoadConversation();
            RefreshRunStatus();
            UpdateSendState();
            SetStatusMessage(string.Empty, HelpBoxMessageType.Info);
        }

        private void OnFocus()
        {
            CheckEnvironment();
            RefreshRunStatus();
        }

        private void BindElements()
        {
            _promptField = rootVisualElement.Q<TextField>("promptField");
            _modelField = rootVisualElement.Q<TextField>("modelField");
            _effortField = rootVisualElement.Q<DropdownField>("effortField");
            _debugToggle = rootVisualElement.Q<Toggle>("debugToggle");
            _historyScroll = rootVisualElement.Q<ScrollView>("historyScroll");
            _gitStatusLabel = rootVisualElement.Q<Label>("gitStatusLabel");
            _codexStatusLabel = rootVisualElement.Q<Label>("codexStatusLabel");
            _statusTextLabel = rootVisualElement.Q<Label>("statusText");
            _statusMetaLabel = rootVisualElement.Q<Label>("statusMeta");
            _statusBar = rootVisualElement.Q<VisualElement>("statusBar");
            _statusBox = rootVisualElement.Q<HelpBox>("statusBox");
            _sendButton = rootVisualElement.Q<Button>("sendButton");
            _newTaskButton = rootVisualElement.Q<Button>("newTaskButton");
            _killButton = rootVisualElement.Q<Button>("killButton");
            _openRunButton = rootVisualElement.Q<Button>("openRunButton");
            _copyCommandButton = rootVisualElement.Q<Button>("copyCommandButton");

            _promptField?.RegisterValueChangedCallback(_ => UpdateSendState());

            if (_sendButton != null)
            {
                _sendButton.clicked += Send;
            }

            if (_newTaskButton != null)
            {
                _newTaskButton.clicked += NewTask;
            }

            if (_killButton != null)
            {
                _killButton.clicked += KillRun;
            }

            if (_openRunButton != null)
            {
                _openRunButton.clicked += OpenRunFolder;
            }

            if (_copyCommandButton != null)
            {
                _copyCommandButton.clicked += CopyRunCommand;
            }

            if (_effortField != null)
            {
                _effortField.choices = new List<string> { "minimal", "low", "medium", "high", "xhigh" };
                _effortField.RegisterValueChangedCallback(_ => SaveUiState());
            }

            _modelField?.RegisterValueChangedCallback(_ => SaveUiState());
            if (_debugToggle != null)
            {
                _debugToggle.RegisterValueChangedCallback(evt =>
                {
                    var state = CodexStore.LoadState();
                    state.debug = evt.newValue;
                    CodexStore.SaveState(state);
                });
            }
        }

        private void RefreshData()
        {
            CodexStore.EnsureDirectoriesExist();
            _state = CodexStore.LoadState();

            _modelField?.SetValueWithoutNotify(string.IsNullOrEmpty(_state.model) ? "gpt-5.1-codex-mini" : _state.model);
            _effortField?.SetValueWithoutNotify(string.IsNullOrEmpty(_state.effort) ? "medium" : _state.effort);
            _debugToggle?.SetValueWithoutNotify(_state.debug);
        }

        private void SaveUiState()
        {
            var state = CodexStore.LoadState();
            state.model = _modelField.value;
            state.effort = _effortField.value;
            CodexStore.SaveState(state);
        }

        private void CheckEnvironment()
        {
            _hasGitRepo = CodexStore.HasGitRepository();
            var (available, version) = CodexRunner.CheckCodexAvailable();
            _codexAvailable = available;
            _codexVersion = version;

            if (_gitStatusLabel != null)
            {
                SetStatusLabel(_gitStatusLabel, _hasGitRepo, "Git: Ready", "Git: Not initialized");
            }

            if (_codexStatusLabel != null)
            {
                var okText = _codexAvailable ? $"Codex: {_codexVersion}" : "Codex: Not found";
                SetStatusLabel(_codexStatusLabel, _codexAvailable, okText, "Codex: Not found");
            }
        }

        private void SetStatusLabel(Label label, bool ok, string okText, string errorText)
        {
            label.text = ok ? okText : errorText;
            label.EnableInClassList("status-ok", ok);
            label.EnableInClassList("status-error", !ok);
        }

        /// <summary>
        /// 加载历史对话（归并后的形式）
        /// </summary>
        private void LoadConversation()
        {
            _historyScroll?.Clear();
            _currentAssistantBubble = null;
            _currentRunId = null;
            _streamBuffer.Clear();
            _streamLineCount = 0;

            var historyItems = CodexStore.LoadHistory();

            // 按 runId 分组，归并消息

            string lastUserRunId = null;
            ChatBubbleElement lastAssistantBubble = null;
            StringBuilder assistantContent = new StringBuilder();

            foreach (var item in historyItems)
            {
                var kind = GetItemKind(item);

                if (kind == "user")
                {
                    // 如果有未完成的助手气泡，先完成它
                    if (lastAssistantBubble != null && assistantContent.Length > 0)
                    {
                        lastAssistantBubble.CompleteStream(GetFinalContent(assistantContent.ToString()), true);
                    }
                    lastAssistantBubble = null;
                    assistantContent.Clear();

                    // 创建用户气泡
                    var userBubble = CreateBubble();
                    userBubble.BindUserMessage(item.text, item.ts);
                    _historyScroll?.Add(userBubble);
                    lastUserRunId = item.runId;
                }
                else if (kind == "assistant")
                {
                    // 完整的助手回复
                    if (lastAssistantBubble != null)
                    {
                        assistantContent.AppendLine(item.text);
                    }
                    else
                    {
                        lastAssistantBubble = CreateBubble();
                        lastAssistantBubble.BindSystemMessage(item.text, item.ts);
                        // 转换为 assistant 样式
                        lastAssistantBubble.Bind(item, false, 0);
                        _historyScroll?.Add(lastAssistantBubble);
                    }
                }
                else if (kind == "system")
                {
                    // 系统消息单独显示
                    var sysBubble = CreateBubble();
                    sysBubble.BindSystemMessage(item.text, item.ts);
                    _historyScroll?.Add(sysBubble);
                }
                // 其他消息类型（event, stderr）在历史加载时忽略
            }

            // 完成最后一个助手气泡
            if (lastAssistantBubble != null && assistantContent.Length > 0)
            {
                lastAssistantBubble.CompleteStream(GetFinalContent(assistantContent.ToString()), true);
            }

            ScrollToBottom();
        }

        /// <summary>
        /// 处理新增的历史项目（实时流式更新）
        /// </summary>
        private void OnHistoryItemAppended(HistoryItem item)
        {
            if (item == null)
            {
                return;
            }

            var kind = GetItemKind(item);

            // 用户消息：直接显示新气泡
            if (kind == "user")
            {
                // 如果有正在进行的助手气泡，先完成它
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

            // 助手最终回复
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
                    // 没有正在进行的气泡，创建一个新的
                    var bubble = CreateBubble();
                    bubble.Bind(item, false, 0);
                    _historyScroll?.Add(bubble);
                }
                ScrollToBottom();
                return;
            }

            // 系统消息
            if (kind == "system")
            {
                var sysBubble = CreateBubble();
                sysBubble.BindSystemMessage(item.text, item.ts);
                _historyScroll?.Add(sysBubble);
                ScrollToBottom();
                return;
            }

            // 流式输出（event, stderr 等）：归并到当前助手气泡
            if (_currentAssistantBubble == null || !_currentAssistantBubble.IsStreaming)
            {
                // 创建新的流式助手气泡
                _currentAssistantBubble = CreateBubble();
                _currentAssistantBubble.BindAssistantStreaming(item.runId, item.ts);
                _historyScroll?.Add(_currentAssistantBubble);
                _currentRunId = item.runId;
                _streamBuffer.Clear();
                _streamLineCount = 0;
            }

            // 追加内容到缓冲区
            if (!string.IsNullOrEmpty(item.text))
            {
                _streamBuffer.AppendLine(item.text);
                _streamLineCount++;
            }

            // 更新气泡显示（显示最新内容）
            _currentAssistantBubble.UpdateStreamContent(_streamBuffer.ToString(), _streamLineCount);

            // 节流滚动
            if (EditorApplication.timeSinceStartup - _lastScrollTime > 0.3)
            {
                _lastScrollTime = EditorApplication.timeSinceStartup;
                ScrollToBottom();
            }
        }

        /// <summary>
        /// 运行状态变化时检查是否需要完成流式气泡
        /// </summary>
        private void OnRunStatusChanged()
        {
            RefreshRunStatus();

            // 检查是否任务已完成
            _state = CodexStore.LoadState();
            if (_state.activeStatus != "running" && _currentAssistantBubble != null && _currentAssistantBubble.IsStreaming)
            {
                var success = _state.activeStatus == "completed";
                var finalContent = GetFinalContent(_streamBuffer.ToString());

                // 尝试读取 out.txt 获取最终摘要

                if (!string.IsNullOrEmpty(_state.lastRunId))
                {
                    var outPath = CodexStore.GetOutPath(_state.lastRunId);
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
                        catch
                        {
                            // Ignore
                        }
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
            if (_bubbleTemplate == null)
            {
                return new ChatBubbleElement(null);
            }
            return new ChatBubbleElement(_bubbleTemplate);
        }

        private void ScrollToBottom()
        {
            if (_historyScroll == null || _historyScroll.contentContainer.childCount == 0)
            {
                return;
            }

            var last = _historyScroll.contentContainer[_historyScroll.contentContainer.childCount - 1];
            _historyScroll.schedule.Execute(() => _historyScroll.ScrollTo(last)).ExecuteLater(10);
        }

        private void RefreshRunStatus()
        {
            _state = CodexStore.LoadState();
            var runId = _state.activeRunId;
            var pid = _state.activePid;
            var status = _state.activeStatus;

            var statusClass = "status-idle";
            var headline = "Idle";

            if (status == "running")
            {
                if (pid > 0 && CodexRunner.IsProcessAlive(pid))
                {
                    statusClass = "status-running";
                    headline = "Running";
                }
                else
                {
                    statusClass = "status-warning";
                    headline = "Warning";
                    _state.activeStatus = "unknown";
                    CodexStore.SaveState(_state);
                }
            }
            else if (status == "completed")
            {
                statusClass = "status-completed";
                headline = "Completed";
            }
            else if (status == "error")
            {
                statusClass = "status-error";
                headline = "Error";
            }
            else if (status == "killed")
            {
                statusClass = "status-error";
                headline = "Killed";
            }
            else if (status == "unknown")
            {
                statusClass = "status-warning";
                headline = "Unknown";
            }

            _statusBar?.EnableInClassList("status-idle", statusClass == "status-idle");
            _statusBar?.EnableInClassList("status-running", statusClass == "status-running");
            _statusBar?.EnableInClassList("status-warning", statusClass == "status-warning");
            _statusBar?.EnableInClassList("status-error", statusClass == "status-error");
            _statusBar?.EnableInClassList("status-completed", statusClass == "status-completed");

            if (_statusTextLabel != null)
            {
                _statusTextLabel.text = headline;
            }

            if (_statusMetaLabel != null)
            {
                var outputTime = CodexRunner.LastOutputTime;
                var outputText = outputTime.HasValue ? outputTime.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "--";
                var meta = string.IsNullOrEmpty(runId)
                    ? "No active run"
                    : $"runId {runId}  pid {pid}  last output {outputText}";
                _statusMetaLabel.text = meta;
            }

            _killButton?.SetEnabled(status == "running");
            _openRunButton?.SetEnabled(!string.IsNullOrEmpty(runId));
            _copyCommandButton?.SetEnabled(!string.IsNullOrEmpty(runId));

            UpdateSendState();
        }

        private void UpdateSendState()
        {
            var isRunning = CodexRunner.IsRunning || (_state != null && _state.activeStatus == "running");
            var promptText = _promptField != null ? _promptField.value : string.Empty;
            var canSend = !isRunning
                          && !string.IsNullOrWhiteSpace(promptText)
                          && _codexAvailable
                          && _hasGitRepo;

            _sendButton?.SetEnabled(canSend);
        }

        private void SetStatusMessage(string message, HelpBoxMessageType type)
        {
            if (_statusBox == null)
            {
                return;
            }

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
                SetStatusMessage("请先在项目根目录执行 git init（本插件要求 git repo）", HelpBoxMessageType.Error);
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
            var userItem = new HistoryItem
            {
                ts = CodexStore.GetIso8601Timestamp(),
                kind = "user",
                role = "user",
                text = prompt,
                source = "ui"
            };

            CodexStore.AppendHistory(userItem);

            // 创建用户气泡

            var userBubble = CreateBubble();
            userBubble.BindUserMessage(prompt, userItem.ts);
            _historyScroll?.Add(userBubble);
            ScrollToBottom();

            _promptField.value = string.Empty;

            var resume = _state.hasActiveThread;
            var model = _modelField.value;
            var effort = _effortField.value;

            CodexRunner.Execute(prompt, model, effort, resume,
                onComplete: _ =>
                {
                    RefreshRunStatus();
                    SetStatusMessage("运行完成", HelpBoxMessageType.Info);
                },
                onError: error =>
                {
                    SetStatusMessage(error, HelpBoxMessageType.Error);
                    // 完成流式气泡（失败状态）
                    if (_currentAssistantBubble != null && _currentAssistantBubble.IsStreaming)
                    {
                        _currentAssistantBubble.CompleteStream(error, false);
                        _currentAssistantBubble = null;
                    }
                });

            UpdateSendState();
        }

        private void NewTask()
        {
            if (!EditorUtility.DisplayDialog("新建任务",
                "确定要清空当前对话历史并开始新任务吗？\n（Codex 侧的会话历史仍然保留在 .codex 目录中）",
                "确定", "取消"))
            {
                return;
            }

            CodexStore.ClearHistory();

            var state = CodexStore.LoadState();
            state.hasActiveThread = false;
            state.lastRunId = null;
            state.lastRunOutPath = null;
            state.activeRunId = null;
            state.activePid = 0;
            state.stdoutOffset = 0;
            state.stderrOffset = 0;
            state.eventsOffset = 0;
            state.activeStatus = "idle";
            CodexStore.SaveState(state);

            _currentAssistantBubble = null;
            _streamBuffer.Clear();
            _streamLineCount = 0;

            LoadConversation();
            RefreshRunStatus();
            SetStatusMessage("已开始新任务", HelpBoxMessageType.Info);
        }

        private void KillRun()
        {
            if (!EditorUtility.DisplayDialog("强杀进程", "确定要强制终止当前 Codex 进程吗？", "强杀", "取消"))
            {
                return;
            }

            CodexRunner.KillActiveProcessTree();
            RefreshRunStatus();
        }

        private void OpenRunFolder()
        {
            var runId = _state.activeRunId;
            if (string.IsNullOrEmpty(runId))
            {
                return;
            }

            var runDir = CodexRunner.GetRunDir(runId);
            if (string.IsNullOrEmpty(runDir))
            {
                return;
            }

            EditorUtility.RevealInFinder(runDir);
        }

        private void CopyRunCommand()
        {
            var runId = _state.activeRunId;
            if (string.IsNullOrEmpty(runId))
            {
                return;
            }

            var meta = CodexStore.LoadRunMeta(runId);
            if (meta == null || string.IsNullOrEmpty(meta.command))
            {
                return;
            }

            EditorGUIUtility.systemCopyBuffer = meta.command;
            SetStatusMessage("已复制命令", HelpBoxMessageType.Info);
        }

        // === Helper Methods ===

        private static string GetItemKind(HistoryItem item)
        {
            if (!string.IsNullOrEmpty(item.kind))
            {
                return item.kind;
            }
            if (!string.IsNullOrEmpty(item.role))
            {
                return item.role;
            }
            return "event";
        }

        private static string GetFinalContent(string streamContent)
        {
            if (string.IsNullOrWhiteSpace(streamContent))
            {
                return "Task completed.";
            }

            // 尝试提取最后有意义的内容
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

            if (meaningfulLines.Count == 0)
            {
                return "Task completed.";
            }

            return string.Join("\n", meaningfulLines);
        }
    }
}
