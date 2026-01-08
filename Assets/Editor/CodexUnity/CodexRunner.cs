using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CodexUnity
{
    /// <summary>
    /// 负责拼命令、启动进程、写 meta/state、采集输出
    /// </summary>
    public static class CodexRunner
    {
        private class ExitInfo
        {
            public string runId;
            public int exitCode;
            public bool killed;
        }

        private static string _currentRunId;
        private static bool _isRunning;
        private static Process _activeProcess;
        private static string _codexExePath;
        private static bool _updateRegistered;
        private static bool _killRequested;
        private static ExitInfo _pendingExit;
        private static int _seqCounter;
        private static long _lastOutputTicks;
        private static double _lastTailTime;
        private static string _tailStdoutPartial = string.Empty;
        private static string _tailStderrPartial = string.Empty;
        private static string _tailEventsPartial = string.Empty;
        private static bool _debugEnabled;

        private static readonly ConcurrentQueue<HistoryItem> PendingItems = new ConcurrentQueue<HistoryItem>();

        private static Action<string> _onComplete;
        private static Action<string> _onError;

        public static bool IsRunning => _isRunning;
        public static string CurrentRunId => _currentRunId;

        public static int? ActivePid
        {
            get
            {
                if (_activeProcess != null)
                {
                    return _activeProcess.Id;
                }

                var state = CodexStore.LoadState();
                return state.activePid > 0 ? state.activePid : (int?)null;
            }
        }

        public static string ActiveRunId
        {
            get
            {
                if (!string.IsNullOrEmpty(_currentRunId))
                {
                    return _currentRunId;
                }

                return CodexStore.LoadState().activeRunId;
            }
        }

        public static DateTime? LastOutputTime
        {
            get
            {
                var ticks = Interlocked.Read(ref _lastOutputTicks);
                if (ticks <= 0)
                {
                    return null;
                }

                return new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
            }
        }

        public static event Action<HistoryItem> HistoryItemAppended;
        public static event Action RunStatusChanged;

        /// <summary>
        /// 检查 codex 是否可用
        /// </summary>
        public static (bool available, string version) CheckCodexAvailable()
        {
            try
            {
                RefreshDebugFlag();
                var resolvedPath = ResolveCodexPath();
                if (string.IsNullOrEmpty(resolvedPath))
                {
                    return (false, null);
                }

                var psi = new ProcessStartInfo
                {
                    FileName = resolvedPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    return (false, null);
                }

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);

                if (process.ExitCode == 0)
                {
                    return (true, output.Trim());
                }

                return (false, null);
            }
            catch (Exception e)
            {
                DebugLog($"[CodexUnity] 检查 codex 失败: {e.Message}");
                return (false, null);
            }
        }

        public static string GetRunDir(string runId)
        {
            return CodexStore.GetRunDir(runId);
        }

        public static bool IsProcessAlive(int pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static void KillActiveProcessTree()
        {
            var pid = ActivePid;
            if (!pid.HasValue || pid.Value <= 0)
            {
                return;
            }

            EnsureUpdateLoop();
            RefreshDebugFlag();
            _killRequested = true;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {pid.Value} /T /F",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    var error = process.StandardError.ReadToEnd();
                    process.WaitForExit(5000);
                    DebugLog($"[CodexUnity] taskkill output: {output}\n{error}");
                }
            }
            catch (Exception e)
            {
                DebugLog($"[CodexUnity] taskkill 失败: {e.Message}");
            }

            EnqueueSystemMessage($"已强杀进程 PID={pid.Value}", "warn");
            var state = CodexStore.LoadState();
            state.activePid = 0;
            state.activeStatus = "killed";
            CodexStore.SaveState(state);

            var runId = ActiveRunId;
            if (!string.IsNullOrEmpty(runId))
            {
                var meta = CodexStore.LoadRunMeta(runId);
                if (meta != null)
                {
                    meta.killed = true;
                    meta.finishedAt = CodexStore.GetIso8601Timestamp();
                    CodexStore.SaveRunMeta(meta);
                }
            }
            RunStatusChanged?.Invoke();
        }

        /// <summary>
        /// 执行命令
        /// </summary>
        public static void Execute(string prompt, string model, string effort, bool resume,
            Action<string> onComplete, Action<string> onError)
        {
            if (_isRunning)
            {
                onError?.Invoke("已有任务正在运行");
                return;
            }

            if (!CodexStore.HasGitRepository())
            {
                onError?.Invoke("请先在项目根目录执行 git init（本插件要求 git repo）");
                return;
            }

            var resolvedPath = ResolveCodexPath();
            if (string.IsNullOrEmpty(resolvedPath))
            {
                onError?.Invoke("codex not found in PATH");
                return;
            }

            EnsureUpdateLoop();
            RefreshDebugFlag();

            _onComplete = onComplete;
            _onError = onError;
            _seqCounter = 0;
            _killRequested = false;
            _tailStdoutPartial = string.Empty;
            _tailStderrPartial = string.Empty;
            _tailEventsPartial = string.Empty;
            _lastTailTime = 0;

            _currentRunId = CodexStore.GenerateRunId();
            var runDir = CodexStore.GetRunDir(_currentRunId);
            var outPath = CodexStore.GetOutPath(_currentRunId);

            if (!Directory.Exists(runDir))
            {
                Directory.CreateDirectory(runDir);
            }

            var args = BuildArguments(prompt, model, effort, resume, outPath);

            var meta = new RunMeta
            {
                runId = _currentRunId,
                command = $"codex {args}",
                prompt = prompt,
                model = model,
                effort = effort,
                time = CodexStore.GetIso8601Timestamp(),
                historyWritten = false,
                startedAt = CodexStore.GetIso8601Timestamp(),
                pid = 0,
                exitCode = 0,
                killed = false,
                stdoutOffset = 0,
                stderrOffset = 0,
                eventsOffset = 0
            };
            CodexStore.SaveRunMeta(meta);

            var state = CodexStore.LoadState();
            state.hasActiveThread = true;
            state.lastRunId = _currentRunId;
            state.lastRunOutPath = outPath;
            state.model = model;
            state.effort = effort;
            state.activeRunId = _currentRunId;
            state.activePid = 0;
            state.stdoutOffset = 0;
            state.stderrOffset = 0;
            state.eventsOffset = 0;
            state.activeStatus = "running";
            CodexStore.SaveState(state);

            _debugEnabled = state.debug;

            var psi = new ProcessStartInfo
            {
                FileName = resolvedPath,
                Arguments = args,
                WorkingDirectory = CodexStore.ProjectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            try
            {
                var process = new Process { StartInfo = psi };
                if (!process.Start())
                {
                    _isRunning = false;
                    onError?.Invoke("无法启动 codex 进程");
                    return;
                }

                _activeProcess = process;
                _isRunning = true;
                _lastOutputTicks = 0;

                meta.pid = process.Id;
                meta.startedAt = CodexStore.GetIso8601Timestamp();
                CodexStore.SaveRunMeta(meta);

                state.activePid = process.Id;
                state.activeStatus = "running";
                CodexStore.SaveState(state);

                DebugLog($"[CodexUnity] 启动 codex 进程 PID={process.Id}, runId={_currentRunId}");
                DebugLog($"[CodexUnity] 命令: {resolvedPath} {args}");
                DebugLog($"[CodexUnity] 运行目录: {runDir}");
                DebugLog($"[CodexUnity] 工作目录: {CodexStore.ProjectRoot}");

                var stdoutPath = CodexStore.GetStdoutPath(_currentRunId);
                var stderrPath = CodexStore.GetStderrPath(_currentRunId);

                _ = Task.Run(() => CaptureStream(process.StandardOutput, stdoutPath, "event", "codex/stdout", "stdout", null));
                _ = Task.Run(() => CaptureStream(process.StandardError, stderrPath, "stderr", "codex/stderr", "stderr", "warn"));
                _ = Task.Run(() => WaitForExit(process, _currentRunId));

                DebugLog("[CodexUnity] 采集线程已启动");
            }
            catch (Exception e)
            {
                _isRunning = false;
                onError?.Invoke($"启动 codex 失败: {e.Message}");
            }
        }

        private static void WaitForExit(Process process, string runId)
        {
            try
            {
                process.WaitForExit();
                var exitInfo = new ExitInfo
                {
                    runId = runId,
                    exitCode = process.ExitCode,
                    killed = _killRequested
                };

                _pendingExit = exitInfo;
            }
            catch (Exception e)
            {
                DebugLog($"[CodexUnity] 等待进程退出失败: {e.Message}");
            }
        }

        private static void CaptureStream(StreamReader reader, string logPath, string kind, string source, string title, string level)
        {
            try
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    AppendLine(logPath, line);
                    EnqueueHistoryItem(kind, source, title, level, line);
                }
            }
            catch (Exception e)
            {
                DebugLog($"[CodexUnity] 采集输出失败: {e.Message}");
            }
        }

        private static void AppendLine(string path, string line)
        {
            File.AppendAllText(path, line + "\n", Encoding.UTF8);
        }

        private static void EnqueueHistoryItem(string kind, string source, string title, string level, string text)
        {
            var item = new HistoryItem
            {
                ts = CodexStore.GetIso8601Timestamp(),
                kind = kind,
                role = kind == "user" || kind == "assistant" ? kind : null,
                title = title,
                level = level,
                text = text,
                raw = text,
                runId = _currentRunId,
                source = source,
                seq = Interlocked.Increment(ref _seqCounter)
            };

            PendingItems.Enqueue(item);
            Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
        }

        private static void EnqueueSystemMessage(string text, string level)
        {
            var item = new HistoryItem
            {
                ts = CodexStore.GetIso8601Timestamp(),
                kind = "system",
                role = null,
                title = "System",
                level = level,
                text = text,
                raw = text,
                runId = ActiveRunId,
                source = "ui"
            };

            PendingItems.Enqueue(item);
        }

        private static void EnsureUpdateLoop()
        {
            if (_updateRegistered)
            {
                return;
            }

            EditorApplication.update += Update;
            _updateRegistered = true;
        }

        private static void Update()
        {
            DrainPendingItems();
            HandlePendingExit();
            TailActiveRunFiles();
        }

        private static void DrainPendingItems()
        {
            var appended = false;
            while (PendingItems.TryDequeue(out var item))
            {
                CodexStore.AppendHistory(item);
                HistoryItemAppended?.Invoke(item);
                appended = true;
            }

            if (appended)
            {
                UpdateOffsetsForActiveRun();
                RunStatusChanged?.Invoke();
            }
        }

        private static void HandlePendingExit()
        {
            if (_pendingExit == null)
            {
                return;
            }

            var exitInfo = _pendingExit;
            _pendingExit = null;

            _isRunning = false;
            _activeProcess = null;

            var meta = CodexStore.LoadRunMeta(exitInfo.runId);
            if (meta != null)
            {
                meta.exitCode = exitInfo.exitCode;
                meta.finishedAt = CodexStore.GetIso8601Timestamp();
                meta.killed = exitInfo.killed;
                CodexStore.SaveRunMeta(meta);
            }

            var state = CodexStore.LoadState();
            state.activePid = 0;
            state.activeStatus = exitInfo.killed ? "killed" : (exitInfo.exitCode == 0 ? "completed" : "error");
            CodexStore.SaveState(state);

            AppendFinalSummaryIfNeeded(exitInfo.runId);
            RunStatusChanged?.Invoke();

            if (exitInfo.exitCode == 0 && !exitInfo.killed)
            {
                _onComplete?.Invoke("completed");
            }
            else if (exitInfo.killed)
            {
                _onError?.Invoke("运行已被终止");
            }
            else
            {
                _onError?.Invoke($"运行失败，退出码 {exitInfo.exitCode}");
            }
        }

        private static void TailActiveRunFiles()
        {
            if (_isRunning)
            {
                return;
            }

            var state = CodexStore.LoadState();
            if (string.IsNullOrEmpty(state.activeRunId) || state.activeStatus != "running")
            {
                return;
            }

            if (state.activePid <= 0 || !IsProcessAlive(state.activePid))
            {
                if (state.activeStatus == "running")
                {
                    state.activeStatus = "unknown";
                    CodexStore.SaveState(state);
                    AppendSystemMessage(state.activeRunId, "warn", "进程已结束或丢失，日志已恢复到最新");
                    RunStatusChanged?.Invoke();
                }
                return;
            }

            if (EditorApplication.timeSinceStartup - _lastTailTime < 0.2f)
            {
                return;
            }

            _lastTailTime = EditorApplication.timeSinceStartup;

            var runId = state.activeRunId;
            var stdoutPath = CodexStore.GetStdoutPath(runId);
            var stderrPath = CodexStore.GetStderrPath(runId);
            var eventsPath = CodexStore.GetEventsPath(runId);

            var stdoutOffset = state.stdoutOffset;
            var stderrOffset = state.stderrOffset;
            var eventsOffset = state.eventsOffset;

            var stdoutLines = CodexStore.ReadNewLines(stdoutPath, ref stdoutOffset, ref _tailStdoutPartial);
            var stderrLines = CodexStore.ReadNewLines(stderrPath, ref stderrOffset, ref _tailStderrPartial);
            var eventLines = CodexStore.ReadNewLines(eventsPath, ref eventsOffset, ref _tailEventsPartial);

            foreach (var line in stdoutLines)
            {
                AppendRecoveredLine(runId, "event", "codex/stdout", "stdout", null, line);
            }

            foreach (var line in stderrLines)
            {
                AppendRecoveredLine(runId, "stderr", "codex/stderr", "stderr", "warn", line);
            }

            foreach (var line in eventLines)
            {
                AppendRecoveredLine(runId, "event", "codex/event", "event", null, line);
            }

            var any = stdoutLines.Count > 0 || stderrLines.Count > 0 || eventLines.Count > 0;
            if (any)
            {
                state.stdoutOffset = stdoutOffset;
                state.stderrOffset = stderrOffset;
                state.eventsOffset = eventsOffset;
                CodexStore.SaveState(state);
                DebugLog($"[CodexUnity] 追尾输出: stdout {stdoutLines.Count}, stderr {stderrLines.Count}, events {eventLines.Count}");
                RunStatusChanged?.Invoke();
            }
        }

        private static void AppendRecoveredLine(string runId, string kind, string source, string title, string level, string line)
        {
            var item = new HistoryItem
            {
                ts = CodexStore.GetIso8601Timestamp(),
                kind = kind,
                role = kind == "user" || kind == "assistant" ? kind : null,
                title = title,
                level = level,
                text = line,
                raw = line,
                runId = runId,
                source = source,
                seq = Interlocked.Increment(ref _seqCounter)
            };

            CodexStore.AppendHistory(item);
            HistoryItemAppended?.Invoke(item);
            Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
        }

        private static void AppendSystemMessage(string runId, string level, string text)
        {
            var item = new HistoryItem
            {
                ts = CodexStore.GetIso8601Timestamp(),
                kind = "system",
                title = "System",
                level = level,
                text = text,
                raw = text,
                runId = runId,
                source = "ui"
            };

            CodexStore.AppendHistory(item);
            HistoryItemAppended?.Invoke(item);
        }

        private static void UpdateOffsetsForActiveRun()
        {
            var runId = _currentRunId;
            if (string.IsNullOrEmpty(runId))
            {
                runId = CodexStore.LoadState().activeRunId;
            }

            if (string.IsNullOrEmpty(runId))
            {
                return;
            }

            var state = CodexStore.LoadState();
            state.stdoutOffset = GetFileLengthSafe(CodexStore.GetStdoutPath(runId));
            state.stderrOffset = GetFileLengthSafe(CodexStore.GetStderrPath(runId));
            state.eventsOffset = GetFileLengthSafe(CodexStore.GetEventsPath(runId));
            CodexStore.SaveState(state);
        }

        private static long GetFileLengthSafe(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.Length : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void AppendFinalSummaryIfNeeded(string runId)
        {
            var outPath = CodexStore.GetOutPath(runId);
            if (!File.Exists(outPath))
            {
                return;
            }

            string output;
            try
            {
                output = File.ReadAllText(outPath, Encoding.UTF8);
            }
            catch (Exception)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(output))
            {
                return;
            }

            var history = CodexStore.LoadHistory();
            foreach (var item in history)
            {
                if (item.runId == runId && (item.kind == "assistant" || item.role == "assistant"))
                {
                    return;
                }
            }

            var historyItem = new HistoryItem
            {
                ts = CodexStore.GetIso8601Timestamp(),
                kind = "assistant",
                role = "assistant",
                title = "Summary",
                text = output,
                raw = output,
                runId = runId,
                source = "codex/out"
            };

            CodexStore.AppendHistory(historyItem);
            HistoryItemAppended?.Invoke(historyItem);

            var meta = CodexStore.LoadRunMeta(runId);
            if (meta != null)
            {
                meta.historyWritten = true;
                CodexStore.SaveRunMeta(meta);
            }
        }

        /// <summary>
        /// 构建命令参数
        /// 注意：exec 和 exec resume 支持的参数不同
        /// - exec 支持: -m, -c, -o, -C, --dangerously-bypass-approvals-and-sandbox
        /// - exec resume 支持: -m, -c, --last, --dangerously-bypass-approvals-and-sandbox (无 -o, -C)
        /// </summary>
        private static string BuildArguments(string prompt, string model, string effort, bool resume, string outPath)
        {
            var sb = new StringBuilder();

            if (resume)
            {
                // exec resume 子命令
                sb.Append("exec resume --last ");
                sb.Append("--dangerously-bypass-approvals-and-sandbox ");
                sb.Append($"--model \"{model}\" ");
                sb.Append($"-c model_reasoning_effort={effort} ");
                // resume 不支持 -o，prompt 直接放在最后
                sb.Append($"\"{EscapePrompt(prompt)}\"");
            }
            else
            {
                // exec 子命令（新任务）
                sb.Append("exec ");
                sb.Append("--dangerously-bypass-approvals-and-sandbox ");
                sb.Append($"--model \"{model}\" ");
                sb.Append($"-c model_reasoning_effort={effort} ");
                sb.Append($"-o \"{outPath}\" ");
                sb.Append($"\"{EscapePrompt(prompt)}\"");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 转义 prompt 中的特殊字符
        /// </summary>
        private static string EscapePrompt(string prompt)
        {
            return prompt.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>
        /// Common paths where codex might be installed (Windows)
        /// </summary>
        private static readonly string[] CommonCodexPaths = new[]
        {
            // npm global install (most common)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex"),
            // pnpm global
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm", "codex.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm", "codex"),
            // yarn global
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yarn", "bin", "codex.cmd"),
            // Chocolatey
            @"C:\ProgramData\chocolatey\bin\codex.exe",
            // Scoop
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "codex.cmd"),
        };

        private static string ResolveCodexPath()
        {
            // Return cached path if still valid
            if (!string.IsNullOrEmpty(_codexExePath) && File.Exists(_codexExePath))
            {
                return _codexExePath;
            }

            // Strategy 1: Check common installation paths first (fastest)
            foreach (var path in CommonCodexPaths)
            {
                if (File.Exists(path))
                {
                    _codexExePath = path;
                    DebugLog($"[CodexUnity] 在常见路径找到 codex: {path}");
                    return _codexExePath;
                }
            }

            // Strategy 2: Try 'where' command with extended PATH
            try
            {
                var extendedPath = BuildExtendedPath();
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where codex",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                // Inject extended PATH for the search

                psi.EnvironmentVariables["PATH"] = extendedPath;

                using var process = Process.Start(psi);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);
                    if (process.ExitCode == 0)
                    {
                        var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 0)
                        {
                            _codexExePath = lines[0].Trim();
                            DebugLog($"[CodexUnity] 通过 where 命令找到 codex: {_codexExePath}");
                            return _codexExePath;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                DebugLog($"[CodexUnity] where 命令失败: {e.Message}");
            }

            // Strategy 3: Try npm root -g to find global npm modules
            try
            {
                var npmPath = FindNpmPath();
                if (!string.IsNullOrEmpty(npmPath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = npmPath,
                        Arguments = "root -g",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        var output = process.StandardOutput.ReadToEnd().Trim();
                        process.WaitForExit(10000);
                        if (process.ExitCode == 0 && !string.IsNullOrEmpty(output))
                        {
                            // npm root -g returns node_modules path, codex is in parent/codex.cmd
                            var npmBinDir = Directory.GetParent(output)?.FullName;
                            if (!string.IsNullOrEmpty(npmBinDir))
                            {
                                var codexCmd = Path.Combine(npmBinDir, "codex.cmd");
                                if (File.Exists(codexCmd))
                                {
                                    _codexExePath = codexCmd;
                                    DebugLog($"[CodexUnity] 通过 npm root -g 找到 codex: {_codexExePath}");
                                    return _codexExePath;
                                }


                                var codexPlain = Path.Combine(npmBinDir, "codex");
                                if (File.Exists(codexPlain))
                                {
                                    _codexExePath = codexPlain;
                                    DebugLog($"[CodexUnity] 通过 npm root -g 找到 codex: {_codexExePath}");
                                    return _codexExePath;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                DebugLog($"[CodexUnity] npm root -g 失败: {e.Message}");
            }

            DebugLog("[CodexUnity] 无法找到 codex 可执行文件");
            return null;
        }

        /// <summary>
        /// Build an extended PATH that includes common node/npm bin directories
        /// </summary>
        private static string BuildExtendedPath()
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var additionalPaths = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yarn", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims"),
                @"C:\Program Files\nodejs",
                @"C:\Program Files (x86)\nodejs",
            };

            // Add nvm paths if found
            var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME");
            if (!string.IsNullOrEmpty(nvmHome))
            {
                additionalPaths.Add(nvmHome);
                // Try to find symlink or default node version
                var nvmSymlink = Environment.GetEnvironmentVariable("NVM_SYMLINK");
                if (!string.IsNullOrEmpty(nvmSymlink))
                {
                    additionalPaths.Add(nvmSymlink);
                }
            }

            // nvm4w commonly uses this structure
            if (Directory.Exists(@"C:\nvm4w\nodejs"))
            {
                additionalPaths.Add(@"C:\nvm4w\nodejs");
            }

            var allPaths = new HashSet<string>(currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries));
            foreach (var p in additionalPaths)
            {
                if (Directory.Exists(p))
                {
                    allPaths.Add(p);
                }
            }

            return string.Join(";", allPaths);
        }

        /// <summary>
        /// Find npm executable path
        /// </summary>
        private static string FindNpmPath()
        {
            var candidates = new[]
            {
                @"C:\Program Files\nodejs\npm.cmd",
                @"C:\nvm4w\nodejs\npm.cmd",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "npm.cmd"),
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            // Try from PATH
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where npm",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process != null)
                {
                    var output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);
                    if (process.ExitCode == 0)
                    {
                        var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        if (lines.Length > 0)
                        {
                            return lines[0].Trim();
                        }
                    }
                }
            }
            catch
            {
                // Ignore
            }

            return null;
        }

        private static void DebugLog(string message)
        {
            if (_debugEnabled)
            {
                Debug.Log(message);
            }
        }

        /// <summary>
        /// 检查并恢复未完成的运行
        /// </summary>
        public static void CheckAndRecoverPendingRun()
        {
            EnsureUpdateLoop();
            RefreshDebugFlag();

            var state = CodexStore.LoadState();
            var runId = !string.IsNullOrEmpty(state.activeRunId) ? state.activeRunId : state.lastRunId;
            if (string.IsNullOrEmpty(runId))
            {
                return;
            }

            _tailStdoutPartial = string.Empty;
            _tailStderrPartial = string.Empty;
            _tailEventsPartial = string.Empty;

            DebugLog($"[CodexUnity] 恢复运行 runId={runId}, status={state.activeStatus}, pid={state.activePid}");
            var stdoutPath = CodexStore.GetStdoutPath(runId);
            var stderrPath = CodexStore.GetStderrPath(runId);
            var eventsPath = CodexStore.GetEventsPath(runId);

            var stdoutOffset = state.stdoutOffset;
            var stderrOffset = state.stderrOffset;
            var eventsOffset = state.eventsOffset;

            var stdoutLines = CodexStore.ReadNewLines(stdoutPath, ref stdoutOffset, ref _tailStdoutPartial);
            var stderrLines = CodexStore.ReadNewLines(stderrPath, ref stderrOffset, ref _tailStderrPartial);
            var eventLines = CodexStore.ReadNewLines(eventsPath, ref eventsOffset, ref _tailEventsPartial);

            foreach (var line in stdoutLines)
            {
                AppendRecoveredLine(runId, "event", "codex/stdout", "stdout", null, line);
            }

            foreach (var line in stderrLines)
            {
                AppendRecoveredLine(runId, "stderr", "codex/stderr", "stderr", "warn", line);
            }

            foreach (var line in eventLines)
            {
                AppendRecoveredLine(runId, "event", "codex/event", "event", null, line);
            }

            var any = stdoutLines.Count > 0 || stderrLines.Count > 0 || eventLines.Count > 0;
            if (any)
            {
                state.stdoutOffset = stdoutOffset;
                state.stderrOffset = stderrOffset;
                state.eventsOffset = eventsOffset;
                CodexStore.SaveState(state);
            }

            if (state.activeStatus == "running" && state.activePid > 0 && !IsProcessAlive(state.activePid))
            {
                state.activeStatus = "unknown";
                CodexStore.SaveState(state);
                AppendSystemMessage(runId, "warn", "进程已结束或丢失，日志已恢复到最新");
            }

            AppendFinalSummaryIfNeeded(runId);
        }

        private static void RefreshDebugFlag()
        {
            _debugEnabled = CodexStore.LoadState().debug;
        }
    }
}
