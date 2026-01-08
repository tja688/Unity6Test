using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace CodexUnity
{
    /// <summary>
    /// Codex Unity 主窗口 (UI Toolkit)
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

        private readonly List<HistoryItem> _history = new List<HistoryItem>();

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
            CodexRunner.RunStatusChanged += RefreshRunStatus;
        }

        private void OnDisable()
        {
            CodexRunner.HistoryItemAppended -= OnHistoryItemAppended;
            CodexRunner.RunStatusChanged -= RefreshRunStatus;
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
            LoadHistory();
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

        private void LoadHistory()
        {
            _history.Clear();
            _historyScroll?.Clear();

            var historyItems = CodexStore.LoadHistory();
            foreach (var item in historyItems)
            {
                _history.Add(item);
                AddBubble(item, false);
            }

            ScrollToBottom();
        }

        private void OnHistoryItemAppended(HistoryItem item)
        {
            if (item == null)
            {
                return;
            }

            _history.Add(item);
            AddBubble(item, true);
        }

        private void AddBubble(HistoryItem item, bool scroll)
        {
            if (_historyScroll == null)
            {
                return;
            }

            if (_bubbleTemplate == null)
            {
                _historyScroll.Add(new Label(item.text ?? string.Empty));
                return;
            }

            var bubble = new ChatBubbleElement(_bubbleTemplate);
            var staggerIndex = item.seq > 0 ? item.seq : _history.Count;
            bubble.Bind(item, scroll, staggerIndex);
            _historyScroll.Add(bubble);

            if (scroll)
            {
                ScrollToBottom();
            }
        }

        private void ScrollToBottom()
        {
            if (_historyScroll == null || _historyScroll.contentContainer.childCount == 0)
            {
                return;
            }

            var last = _historyScroll.contentContainer[_historyScroll.contentContainer.childCount - 1];
            _historyScroll.schedule.Execute(() => _historyScroll.ScrollTo(last)).ExecuteLater(1);
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
            AddBubble(userItem, true);
            _history.Add(userItem);

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

            LoadHistory();
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
    }
}
