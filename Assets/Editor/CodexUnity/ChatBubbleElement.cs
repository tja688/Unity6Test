using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace CodexUnity
{
    /// <summary>
    /// 聊天气泡元素 - 支持流式更新和状态显示
    /// </summary>
    public class ChatBubbleElement : VisualElement
    {
        private readonly VisualElement _root;
        private readonly Label _titleLabel;
        private readonly Label _timestampLabel;
        private readonly Label _bodyLabel;
        private readonly VisualElement _statusContainer;
        private readonly VisualElement _statusDot;
        private readonly Label _statusText;

        private string _runId;
        private bool _isStreaming;
        private int _updateCount;
        private IVisualElementScheduledItem _flashSchedule;

        public string RunId => _runId;
        public bool IsStreaming => _isStreaming;

        public ChatBubbleElement(VisualTreeAsset template)
        {
            if (template == null)
            {
                return;
            }

            _root = template.CloneTree();
            Add(_root);

            _titleLabel = _root.Q<Label>("titleLabel");
            _timestampLabel = _root.Q<Label>("timestampLabel");
            _bodyLabel = _root.Q<Label>("bodyLabel");
            _statusContainer = _root.Q<VisualElement>("statusContainer");
            _statusDot = _root.Q<VisualElement>("statusDot");
            _statusText = _root.Q<Label>("statusText");
        }

        /// <summary>
        /// 初始化用户消息气泡
        /// </summary>
        public void BindUserMessage(string message, string timestamp)
        {
            _runId = null;
            _isStreaming = false;

            ApplyKindClass("user");

            if (_titleLabel != null)
            {
                _titleLabel.text = "You";
            }

            if (_timestampLabel != null)
            {
                _timestampLabel.text = FormatTimestamp(timestamp);
            }

            if (_bodyLabel != null)
            {
                _bodyLabel.text = message ?? string.Empty;
            }

            HideStatus();
        }

        /// <summary>
        /// 初始化 Assistant 响应气泡（开始流式输出）
        /// </summary>
        public void BindAssistantStreaming(string runId, string timestamp)
        {
            _runId = runId;
            _isStreaming = true;
            _updateCount = 0;

            ApplyKindClass("assistant");
            _root.AddToClassList("streaming");

            if (_titleLabel != null)
            {
                _titleLabel.text = "Codex";
            }

            if (_timestampLabel != null)
            {
                _timestampLabel.text = FormatTimestamp(timestamp);
            }

            if (_bodyLabel != null)
            {
                _bodyLabel.text = "Starting...";
            }

            ShowStatus("Processing...");
            StartFlashAnimation();
        }

        /// <summary>
        /// 更新流式输出内容
        /// </summary>
        public void UpdateStreamContent(string content, int lineCount = 0)
        {
            if (!_isStreaming)
            {
                return;
            }

            _updateCount++;

            if (_bodyLabel != null)
            {
                // 截取最后的内容，避免显示过长
                var displayText = TruncateForDisplay(content, 500);
                _bodyLabel.text = displayText;
            }

            if (_statusText != null)
            {
                _statusText.text = $"Processing... ({lineCount} lines)";
            }

            // 更新时间戳
            if (_timestampLabel != null)
            {
                _timestampLabel.text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// 完成流式输出，显示最终结果
        /// </summary>
        public void CompleteStream(string finalContent, bool success = true)
        {
            _isStreaming = false;
            StopFlashAnimation();

            _root.RemoveFromClassList("streaming");
            _root.AddToClassList(success ? "completed" : "error");

            if (_bodyLabel != null)
            {
                _bodyLabel.text = string.IsNullOrWhiteSpace(finalContent)

                    ? (success ? "Task completed." : "Task failed.")

                    : finalContent;
            }

            if (_timestampLabel != null)
            {
                _timestampLabel.text = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }

            ShowStatus(success ? "Completed" : "Failed");
        }

        /// <summary>
        /// 绑定系统消息
        /// </summary>
        public void BindSystemMessage(string message, string timestamp)
        {
            _runId = null;
            _isStreaming = false;

            ApplyKindClass("system");

            if (_titleLabel != null)
            {
                _titleLabel.text = "System";
            }

            if (_timestampLabel != null)
            {
                _timestampLabel.text = FormatTimestamp(timestamp);
            }

            if (_bodyLabel != null)
            {
                _bodyLabel.text = message ?? string.Empty;
            }

            HideStatus();
        }

        // === Legacy API for compatibility ===


        public void Bind(HistoryItem item, bool animate, int staggerIndex = 0)
        {
            if (item == null || _root == null)
            {
                return;
            }

            var kind = GetKind(item);
            _runId = item.runId;

            ApplyKindClass(kind);

            if (_titleLabel != null)
            {
                _titleLabel.text = GetTitleForKind(kind);
            }

            if (_timestampLabel != null)
            {
                _timestampLabel.text = FormatTimestamp(item.ts);
            }

            if (_bodyLabel != null)
            {
                _bodyLabel.text = item.text ?? string.Empty;
            }

            HideStatus();
        }

        // === Private Helpers ===

        private void ApplyKindClass(string kind)
        {
            _root.RemoveFromClassList("user");
            _root.RemoveFromClassList("assistant");
            _root.RemoveFromClassList("system");
            _root.RemoveFromClassList("streaming");
            _root.RemoveFromClassList("completed");
            _root.RemoveFromClassList("error");

            if (!string.IsNullOrEmpty(kind))
            {
                _root.AddToClassList(kind);
            }
        }

        private void ShowStatus(string text)
        {
            if (_statusContainer != null)
            {
                _statusContainer.RemoveFromClassList("hidden");
            }

            if (_statusText != null)
            {
                _statusText.text = text;
            }
        }

        private void HideStatus()
        {
            if (_statusContainer != null)
            {
                _statusContainer.AddToClassList("hidden");
            }
        }

        private void StartFlashAnimation()
        {
            StopFlashAnimation();

            if (_statusDot == null)
            {
                return;
            }

            var flashState = false;
            _flashSchedule = _statusDot.schedule.Execute(() =>
            {
                flashState = !flashState;
                _statusDot.style.opacity = flashState ? 1f : 0.3f;
            }).Every(400);
        }

        private void StopFlashAnimation()
        {
            if (_flashSchedule != null)
            {
                _flashSchedule.Pause();
                _flashSchedule = null;
            }

            if (_statusDot != null)
            {
                _statusDot.style.opacity = 1f;
            }
        }

        private static string TruncateForDisplay(string content, int maxLength)
        {
            if (string.IsNullOrEmpty(content))
            {
                return string.Empty;
            }

            // 获取最后 N 个字符
            if (content.Length > maxLength)
            {
                var truncated = content.Substring(content.Length - maxLength);
                // 找到第一个换行符，从那里开始显示
                var firstNewline = truncated.IndexOf('\n');
                if (firstNewline > 0 && firstNewline < 50)
                {
                    truncated = truncated.Substring(firstNewline + 1);
                }
                return "..." + truncated;
            }

            return content;
        }

        private static string GetKind(HistoryItem item)
        {
            if (!string.IsNullOrEmpty(item.kind))
            {
                if (item.kind == "user" || item.kind == "assistant" || item.kind == "system")
                {
                    return item.kind;
                }
            }

            if (!string.IsNullOrEmpty(item.role))
            {
                return item.role;
            }

            return "assistant";
        }

        private static string GetTitleForKind(string kind)
        {
            switch (kind)
            {
                case "user":
                    return "You";
                case "assistant":
                    return "Codex";
                case "system":
                    return "System";
                default:
                    return "Codex";
            }
        }

        private static string FormatTimestamp(string ts)
        {
            if (string.IsNullOrEmpty(ts))
            {
                return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }

            return ts.Length > 8 ? ts.Substring(ts.Length - 8) : ts;
        }
    }
}
