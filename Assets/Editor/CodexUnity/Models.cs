using System;

namespace CodexUnity
{
    /// <summary>
    /// 会话状态
    /// </summary>
    [Serializable]
    public class CodexState
    {
        public bool debug;
        public string activeRunId;
        public int activePid;
        public long stdoutOffset;
        public long stderrOffset;
        public long eventsOffset;
        public string activeStatus;
        public bool hasActiveThread;
        public string lastRunId;
        public string lastRunOutPath;
        public string model;
        public string effort;
    }

    /// <summary>
    /// 历史记录条目
    /// </summary>
    [Serializable]
    public class HistoryItem
    {
        public string ts;       // ISO8601 时间戳
        public string role;     // "user" 或 "assistant"
        public string text;     // 消息内容
        public string runId;    // 运行 ID（可选）
        public string kind;     // "user" | "assistant" | "event" | "stderr" | "system"
        public string title;    // 气泡标题
        public string level;    // "info" | "warn" | "error"
        public int seq;         // 单 run 内递增序号
        public string source;   // "codex/stdout" 等
        public string raw;      // 原始行
    }

    /// <summary>
    /// 运行元数据
    /// </summary>
    [Serializable]
    public class RunMeta
    {
        public string runId;
        public string command;
        public string prompt;
        public string model;
        public string effort;
        public string time;         // ISO8601 时间戳
        public bool historyWritten; // 是否已写入历史
        public int pid;
        public string startedAt;
        public string finishedAt;
        public int exitCode;
        public bool killed;
        public long stdoutOffset;
        public long stderrOffset;
        public long eventsOffset;
    }

    /// <summary>
    /// Reasoning Effort 选项
    /// </summary>
    public enum ReasoningEffort
    {
        minimal,
        low,
        medium,
        high,
        xhigh
    }
}
