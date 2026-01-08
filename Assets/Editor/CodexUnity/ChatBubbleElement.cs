using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace CodexUnity
{
    public class ChatBubbleElement : VisualElement
    {
        private readonly VisualElement _root;
        private readonly Label _titleLabel;
        private readonly Label _timestampLabel;
        private readonly Label _bodyLabel;
        private readonly Label _rawLabel;
        private readonly VisualElement _rawContainer;
        private readonly Button _rawToggle;
        private bool _rawVisible;

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
            _rawLabel = _root.Q<Label>("rawLabel");
            _rawContainer = _root.Q<VisualElement>("rawContainer");
            _rawToggle = _root.Q<Button>("rawToggle");

            if (_rawToggle != null)
            {
                _rawToggle.clicked += ToggleRaw;
            }
        }

        public void Bind(HistoryItem item, bool animate, int staggerIndex = 0)
        {
            if (item == null || _root == null)
            {
                return;
            }

            var kind = GetKind(item);
            var level = string.IsNullOrEmpty(item.level) ? "info" : item.level;

            ApplyKindClasses(kind);
            ApplyLevelClasses(level);

            if (_titleLabel != null)
            {
                _titleLabel.text = string.IsNullOrEmpty(item.title) ? GetDefaultTitle(kind) : item.title;
            }

            if (_timestampLabel != null)
            {
                _timestampLabel.text = FormatTimestamp(item.ts);
            }

            if (_bodyLabel != null)
            {
                _bodyLabel.text = item.text ?? string.Empty;
            }

            var rawText = string.IsNullOrEmpty(item.raw) ? string.Empty : item.raw;
            var showRaw = !string.IsNullOrEmpty(rawText) && rawText != (item.text ?? string.Empty);

            if (_rawLabel != null)
            {
                _rawLabel.text = rawText;
            }

            if (_rawContainer != null)
            {
                _rawContainer.style.display = DisplayStyle.None;
            }

            if (_rawToggle != null)
            {
                _rawToggle.style.display = showRaw ? DisplayStyle.Flex : DisplayStyle.None;
                _rawToggle.text = "Show raw";
            }

            _rawVisible = false;

            if (animate)
            {
                _root.AddToClassList("is-new");
                var delay = Mathf.Clamp(staggerIndex * 24, 0, 240);
                _root.schedule.Execute(() =>
                {
                    _root.AddToClassList("reveal");
                    _root.RemoveFromClassList("is-new");
                }).ExecuteLater(delay);
            }
        }

        private void ToggleRaw()
        {
            if (_rawContainer == null || _rawToggle == null)
            {
                return;
            }

            _rawVisible = !_rawVisible;
            _rawContainer.style.display = _rawVisible ? DisplayStyle.Flex : DisplayStyle.None;
            _rawToggle.text = _rawVisible ? "Hide raw" : "Show raw";
        }

        private void ApplyKindClasses(string kind)
        {
            _root.EnableInClassList("user", kind == "user");
            _root.EnableInClassList("assistant", kind == "assistant");
            _root.EnableInClassList("event", kind == "event");
            _root.EnableInClassList("stderr", kind == "stderr");
            _root.EnableInClassList("system", kind == "system");
        }

        private void ApplyLevelClasses(string level)
        {
            _root.EnableInClassList("level-warn", level == "warn");
            _root.EnableInClassList("level-error", level == "error");
        }

        private static string GetKind(HistoryItem item)
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

        private static string GetDefaultTitle(string kind)
        {
            switch (kind)
            {
                case "user":
                    return "User";
                case "assistant":
                    return "Assistant";
                case "stderr":
                    return "Stderr";
                case "system":
                    return "System";
                default:
                    return "Event";
            }
        }

        private static string FormatTimestamp(string ts)
        {
            if (string.IsNullOrEmpty(ts))
            {
                return "--:--:--";
            }

            if (DateTime.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }

            return ts.Length > 8 ? ts.Substring(ts.Length - 8) : ts;
        }
    }
}
