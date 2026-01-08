using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CodexUnity
{
    /// <summary>
    /// Codex Unity 主窗口
    /// </summary>
    public class CodexWindow : EditorWindow
    {
        // UI 状态
        private string _promptText = "";
        private string _modelText = "gpt-5.1-codex-mini";
        private ReasoningEffort _effort = ReasoningEffort.medium;
        private Vector2 _historyScrollPos;
        private Vector2 _promptScrollPos;

        // 环境检查
        private bool _codexAvailable;
        private string _codexVersion;
        private bool _hasGitRepo;
        private bool _environmentChecked;

        // 历史记录缓存
        private List<HistoryItem> _history = new List<HistoryItem>();
        private string _historyDisplay = "";

        // 状态
        private CodexState _state;
        private string _statusMessage = "";
        private MessageType _statusType = MessageType.Info;

        // 样式
        private GUIStyle _historyStyle;
        private GUIStyle _warningBoxStyle;
        private bool _stylesInitialized;

        [MenuItem("Tools/Codex")]
        public static void ShowWindow()
        {
            var window = GetWindow<CodexWindow>("Codex");
            window.minSize = new Vector2(400, 500);
            window.Show();
        }

        private void OnEnable()
        {
            // 加载状态和历史
            RefreshData();

            // 检查环境
            CheckEnvironment();
        }

        private void OnFocus()
        {
            // 窗口获得焦点时刷新数据
            RefreshData();
        }

        private void RefreshData()
        {
            CodexStore.EnsureDirectoriesExist();

            _state = CodexStore.LoadState();
            _history = CodexStore.LoadHistory();
            _historyDisplay = BuildHistoryDisplay();

            // 恢复模型和 effort 设置
            if (!string.IsNullOrEmpty(_state.model))
            {
                _modelText = _state.model;
            }
            if (!string.IsNullOrEmpty(_state.effort) && Enum.TryParse<ReasoningEffort>(_state.effort, out var effort))
            {
                _effort = effort;
            }
        }

        private void CheckEnvironment()
        {
            _hasGitRepo = CodexStore.HasGitRepository();
            var (available, version) = CodexRunner.CheckCodexAvailable();
            _codexAvailable = available;
            _codexVersion = version;
            _environmentChecked = true;
        }

        private void InitStyles()
        {
            if (_stylesInitialized) return;

            _historyStyle = new GUIStyle(EditorStyles.textArea)
            {
                wordWrap = true,
                richText = true,
                padding = new RectOffset(8, 8, 8, 8)
            };

            _warningBoxStyle = new GUIStyle(EditorStyles.helpBox)
            {
                wordWrap = true,
                padding = new RectOffset(10, 10, 10, 10),
                fontSize = 11
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            InitStyles();

            EditorGUILayout.Space(5);

            // 环境检查结果
            DrawEnvironmentStatus();

            EditorGUILayout.Space(10);

            // 风险声明
            DrawRiskWarning();

            EditorGUILayout.Space(10);

            // 历史显示区
            DrawHistoryArea();

            EditorGUILayout.Space(10);

            // 输入区
            DrawInputArea();

            EditorGUILayout.Space(10);

            // 状态提示
            DrawStatusArea();

            EditorGUILayout.Space(5);

            // 按钮区
            DrawButtonArea();

            EditorGUILayout.Space(10);

            // 自动刷新
            if (CodexRunner.IsRunning)
            {
                Repaint();
            }
        }

        private void DrawEnvironmentStatus()
        {
            EditorGUILayout.LabelField("环境检查", EditorStyles.boldLabel);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                // Git 状态
                if (_hasGitRepo)
                {
                    EditorGUILayout.LabelField("✓ Git 仓库已初始化", EditorStyles.miniLabel);
                }
                else
                {
                    EditorGUILayout.LabelField("✗ 未检测到 Git 仓库 - 请先执行 git init",
                        new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.red } });
                }

                // Codex 状态
                if (_codexAvailable)
                {
                    EditorGUILayout.LabelField($"✓ Codex CLI: {_codexVersion}", EditorStyles.miniLabel);
                }
                else
                {
                    EditorGUILayout.LabelField("✗ codex not found in PATH",
                        new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.red } });
                }
            }
        }

        private void DrawRiskWarning()
        {
            using (new EditorGUILayout.VerticalScope(_warningBoxStyle))
            {
                EditorGUILayout.LabelField("⚠ 高风险警告", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "• 当前使用 --dangerously-bypass-approvals-and-sandbox 模式，Codex 拥有完全的系统访问权限。\n" +
                    "• Codex 可以修改任意文件、执行任意命令，无需确认。\n" +
                    "• 请务必使用 Git 管理风险，确保可以回滚任何更改。",
                    EditorStyles.wordWrappedLabel);
            }
        }

        private void DrawHistoryArea()
        {
            EditorGUILayout.LabelField("对话历史", EditorStyles.boldLabel);

            using (var scrollView = new EditorGUILayout.ScrollViewScope(_historyScrollPos,
                GUILayout.Height(200), GUILayout.ExpandWidth(true)))
            {
                _historyScrollPos = scrollView.scrollPosition;

                if (string.IsNullOrEmpty(_historyDisplay))
                {
                    EditorGUILayout.LabelField("（无历史）", EditorStyles.centeredGreyMiniLabel);
                }
                else
                {
                    EditorGUILayout.TextArea(_historyDisplay, _historyStyle, GUILayout.ExpandHeight(true));
                }
            }
        }

        private void DrawInputArea()
        {
            EditorGUILayout.LabelField("输入", EditorStyles.boldLabel);

            // Model
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Model:", GUILayout.Width(50));
            _modelText = EditorGUILayout.TextField(_modelText);
            EditorGUILayout.EndHorizontal();

            // Reasoning Effort
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Effort:", GUILayout.Width(50));
            _effort = (ReasoningEffort)EditorGUILayout.EnumPopup(_effort);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // Prompt
            EditorGUILayout.LabelField("Prompt:", GUILayout.Width(50));
            using (var scrollView = new EditorGUILayout.ScrollViewScope(_promptScrollPos,
                GUILayout.Height(80), GUILayout.ExpandWidth(true)))
            {
                _promptScrollPos = scrollView.scrollPosition;
                _promptText = EditorGUILayout.TextArea(_promptText, GUILayout.ExpandHeight(true));
            }
        }

        private void DrawStatusArea()
        {
            // 运行状态
            using (new EditorGUILayout.HorizontalScope())
            {
                if (CodexRunner.IsRunning)
                {
                    var dots = new string('.', (int)(EditorApplication.timeSinceStartup * 2) % 4);
                    EditorGUILayout.LabelField($"⏳ Running{dots}",
                        new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = new Color(0.2f, 0.6f, 1f) } });
                }
                else if (_state != null && !string.IsNullOrEmpty(_state.lastRunId))
                {
                    EditorGUILayout.LabelField($"上次运行: {_state.lastRunId}", EditorStyles.miniLabel);
                }
                else
                {
                    EditorGUILayout.LabelField("就绪", EditorStyles.miniLabel);
                }

                // 线程状态
                GUILayout.FlexibleSpace();
                if (_state != null && _state.hasActiveThread)
                {
                    EditorGUILayout.LabelField("🔗 会话中", EditorStyles.miniLabel);
                }
            }

            // 错误/状态消息
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                EditorGUILayout.HelpBox(_statusMessage, _statusType);
            }
        }

        private void DrawButtonArea()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = CanSend();

                if (GUILayout.Button("Send", GUILayout.Height(30)))
                {
                    Send();
                }

                GUI.enabled = !CodexRunner.IsRunning;

                if (GUILayout.Button("New Task", GUILayout.Height(30), GUILayout.Width(100)))
                {
                    NewTask();
                }

                GUI.enabled = true;
            }
        }

        private bool CanSend()
        {
            return !CodexRunner.IsRunning
                   && !string.IsNullOrWhiteSpace(_promptText)
                   && _codexAvailable
                   && _hasGitRepo;
        }

        private void Send()
        {
            _statusMessage = "";
            _statusType = MessageType.Info;

            // 二次校验
            if (!_hasGitRepo)
            {
                _statusMessage = "请先在项目根目录执行 git init（本插件要求 git repo）";
                _statusType = MessageType.Error;
                return;
            }

            if (!_codexAvailable)
            {
                _statusMessage = "codex not found in PATH";
                _statusType = MessageType.Error;
                return;
            }

            if (string.IsNullOrWhiteSpace(_promptText))
            {
                _statusMessage = "请输入 prompt";
                _statusType = MessageType.Warning;
                return;
            }

            // 追加用户消息到历史
            var userItem = new HistoryItem
            {
                ts = CodexStore.GetIso8601Timestamp(),
                role = "user",
                text = _promptText
            };
            CodexStore.AppendHistory(userItem);

            // 刷新显示
            _history.Add(userItem);
            _historyDisplay = BuildHistoryDisplay();

            // 执行命令
            var prompt = _promptText;
            var model = _modelText;
            var effort = _effort.ToString();
            var resume = _state.hasActiveThread;

            _promptText = ""; // 清空输入框

            CodexRunner.Execute(prompt, model, effort, resume,
                onComplete: output =>
                {
                    RefreshData();
                    _statusMessage = "运行完成";
                    _statusType = MessageType.Info;
                    Repaint();
                },
                onError: error =>
                {
                    _statusMessage = error;
                    _statusType = MessageType.Error;
                    Repaint();
                });
        }

        private void NewTask()
        {
            if (EditorUtility.DisplayDialog("新建任务",
                "确定要清空当前对话历史并开始新任务吗？\n（Codex 侧的会话历史仍然保留在 .codex 目录中）",
                "确定", "取消"))
            {
                // 清空历史
                CodexStore.ClearHistory();

                // 重置状态
                var state = CodexStore.LoadState();
                state.hasActiveThread = false;
                state.lastRunId = null;
                state.lastRunOutPath = null;
                CodexStore.SaveState(state);

                // 刷新
                RefreshData();
                _statusMessage = "已开始新任务";
                _statusType = MessageType.Info;
            }
        }

        private string BuildHistoryDisplay()
        {
            if (_history == null || _history.Count == 0)
            {
                return "";
            }

            var sb = new StringBuilder();
            foreach (var item in _history)
            {
                var roleLabel = item.role == "user" ? "👤 用户" : "🤖 Codex";
                sb.AppendLine($"[{item.ts}]");
                sb.AppendLine($"<b>{roleLabel}:</b>");
                sb.AppendLine(item.text);
                sb.AppendLine();
                sb.AppendLine("─────────────────────────");
                sb.AppendLine();
            }
            return sb.ToString();
        }
    }
}
