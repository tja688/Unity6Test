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
    /// 
    /// 设计说明（v2 - 文件轮询模式）：
    /// Unity 的 Domain Reload 会杀死所有托管线程，包括管道读取线程。
    /// 但子进程本身可能不会被杀死，导致进程变成"孤儿"——还在运行但无法通信。
    /// 
    /// 解决方案：
    /// 1. 不使用管道重定向（RedirectStandardOutput/Error）
    /// 2. 启动进程时，用 cmd /c 包装，将输出重定向到文件
    /// 3. 使用文件轮询读取输出（Domain Reload 安全）
    /// 4. Domain Reload 后可以无缝继续读取文件
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

        // 进程退出检测定时器
        private static double _lastProcessCheckTime;
        private const double ProcessCheckInterval = 1.0; // 每1秒检查一次进程状态

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
                    try
                    {
                        return _activeProcess.Id;
                    }
                    catch
                    {
                        return null;
                    }
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

        public static void BindAssemblyReloadEvents()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            Debug.Log("[CodexUnity] Assembly Reload 事件已绑定");
        }

        private static void OnBeforeAssemblyReload()
        {
            // 关键：在 Domain Reload 前保存状态
            // 不需要设置特殊标志，因为新的设计中，恢复只依赖于文件状态
            var state = CodexStore.LoadState();
            var wasRunning = _isRunning || state.activeStatus == "running";


            Debug.Log($"[CodexUnity] OnBeforeAssemblyReload: _isRunning={_isRunning}, state.activeStatus={state.activeStatus}");


            if (wasRunning && state.activePid > 0)
            {
                // 记录 Domain Reload 发生时的状态
                state.lastReloadTime = DateTime.UtcNow.Ticks;
                CodexStore.SaveState(state);
                Debug.Log($"[CodexUnity] 检测到运行中任务 (PID={state.activePid})，已记录 reload 时间");
            }

            // 清除内存中的进程引用（会被 Domain Reload 销毁）

            _activeProcess = null;
            _isRunning = false;
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
                // 没有进程，直接清理状态
                CleanupRunState("killed");
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

            CleanupRunState("killed");
            EnqueueSystemMessage($"已强杀进程 PID={pid.Value}", "warn");
        }

        private static void CleanupRunState(string newStatus)
        {
            var runId = ActiveRunId;


            var state = CodexStore.LoadState();
            state.activePid = 0;
            state.activeStatus = newStatus;
            CodexStore.SaveState(state);

            if (!string.IsNullOrEmpty(runId))
            {
                var meta = CodexStore.LoadRunMeta(runId);
                if (meta != null)
                {
                    meta.killed = newStatus == "killed";
                    meta.finishedAt = CodexStore.GetIso8601Timestamp();
                    CodexStore.SaveRunMeta(meta);
                }
            }


            _isRunning = false;
            _activeProcess = null;
            _currentRunId = null;


            RunStatusChanged?.Invoke();
        }

        /// <summary>
        /// 执行命令（v2 - 文件输出模式）
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
            _lastProcessCheckTime = 0;

            _currentRunId = CodexStore.GenerateRunId();
            var runDir = CodexStore.GetRunDir(_currentRunId);
            var outPath = CodexStore.GetOutPath(_currentRunId);
            var stdoutPath = CodexStore.GetStdoutPath(_currentRunId);
            var stderrPath = CodexStore.GetStderrPath(_currentRunId);

            if (!Directory.Exists(runDir))
            {
                Directory.CreateDirectory(runDir);
            }

            // 创建空的输出文件
            File.WriteAllText(stdoutPath, "", Encoding.UTF8);
            File.WriteAllText(stderrPath, "", Encoding.UTF8);

            var codexArgs = BuildArguments(prompt, model, effort, resume, outPath);

            var meta = new RunMeta
            {
                runId = _currentRunId,
                command = $"codex {codexArgs}",
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

            // 关键变化：使用 cmd /c 包装，将输出重定向到文件
            // 这样即使管道断开，进程输出仍然会写入文件
            var cmdArgs = $"/c \"\"{resolvedPath}\" {codexArgs} > \"{stdoutPath}\" 2> \"{stderrPath}\"\"";


            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArgs,
                WorkingDirectory = CodexStore.ProjectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                // 不再使用管道重定向！
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                RedirectStandardInput = false
            };

            try
            {
                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

                // 使用进程事件而不是后台线程来检测退出

                process.Exited += (sender, args) =>
                {
                    // 注意：这个回调在线程池线程中执行
                    // 不能直接操作 Unity API，需要通过队列传递
                    try
                    {
                        var exitInfo = new ExitInfo
                        {
                            runId = _currentRunId,
                            exitCode = process.ExitCode,
                            killed = _killRequested
                        };
                        _pendingExit = exitInfo;
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[CodexUnity] 进程退出事件处理失败: {e.Message}");
                    }
                };


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

                Debug.Log($"[CodexUnity] 启动 codex 进程 PID={process.Id}, runId={_currentRunId}");
                DebugLog($"[CodexUnity] 命令: cmd.exe {cmdArgs}");
                DebugLog($"[CodexUnity] 运行目录: {runDir}");
                DebugLog($"[CodexUnity] 工作目录: {CodexStore.ProjectRoot}");
                Debug.Log("[CodexUnity] 使用文件轮询模式读取输出（Domain Reload 安全）");
            }
            catch (Exception e)
            {
                _isRunning = false;
                onError?.Invoke($"启动 codex 失败: {e.Message}");
            }
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
                runId = _currentRunId ?? ActiveRunId,
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
            CheckProcessStatus();
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

            Debug.Log($"[CodexUnity] 进程已退出: runId={exitInfo.runId}, exitCode={exitInfo.exitCode}, killed={exitInfo.killed}");

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

            // 最后一次读取所有剩余输出
            TailActiveRunFilesForce(exitInfo.runId);


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

        /// <summary>
        /// 检查进程状态（定时执行，用于 Domain Reload 后的恢复场景）
        /// </summary>
        private static void CheckProcessStatus()
        {
            // 限制检查频率
            if (EditorApplication.timeSinceStartup - _lastProcessCheckTime < ProcessCheckInterval)
            {
                return;
            }
            _lastProcessCheckTime = EditorApplication.timeSinceStartup;

            var state = CodexStore.LoadState();
            if (state.activeStatus != "running" || state.activePid <= 0)
            {
                return;
            }

            // 检查进程是否还在运行
            if (!IsProcessAlive(state.activePid))
            {
                Debug.Log($"[CodexUnity] 检测到进程 {state.activePid} 已退出（可能在 Domain Reload 期间）");

                // 进程已死，完成最后的清理

                var exitInfo = new ExitInfo
                {
                    runId = state.activeRunId,
                    exitCode = -1, // 未知退出码
                    killed = false
                };
                _pendingExit = exitInfo;
            }
        }

        /// <summary>
        /// 文件轮询读取输出（Domain Reload 安全）
        /// </summary>
        private static void TailActiveRunFiles()
        {
            var state = CodexStore.LoadState();
            if (string.IsNullOrEmpty(state.activeRunId))
            {
                return;
            }

            // 只有 running 状态才需要轮询
            if (state.activeStatus != "running")
            {
                return;
            }

            // 限制轮询频率
            if (EditorApplication.timeSinceStartup - _lastTailTime < 0.2f)
            {
                return;
            }
            _lastTailTime = EditorApplication.timeSinceStartup;

            TailActiveRunFilesForce(state.activeRunId);
        }

        private static void TailActiveRunFilesForce(string runId)
        {
            var state = CodexStore.LoadState();


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
                state = CodexStore.LoadState(); // 重新加载以避免覆盖其他更改
                state.stdoutOffset = stdoutOffset;
                state.stderrOffset = stderrOffset;
                state.eventsOffset = eventsOffset;
                CodexStore.SaveState(state);


                Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
                DebugLog($"[CodexUnity] 读取输出: stdout {stdoutLines.Count}, stderr {stderrLines.Count}, events {eventLines.Count}");
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
        /// 检查并恢复未完成的运行（v2 - 简化版）
        /// 由于使用文件轮询模式，Domain Reload 后只需检查进程是否还在运行
        /// 如果进程还在，继续轮询；如果进程死了，标记完成
        /// </summary>
        public static void CheckAndRecoverPendingRun()
        {
            EnsureUpdateLoop();
            RefreshDebugFlag();

            var state = CodexStore.LoadState();
            var runId = !string.IsNullOrEmpty(state.activeRunId) ? state.activeRunId : state.lastRunId;


            Debug.Log($"[CodexUnity] CheckAndRecoverPendingRun: runId={runId}, status={state.activeStatus}, pid={state.activePid}");


            if (string.IsNullOrEmpty(runId))
            {
                Debug.Log("[CodexUnity] 没有找到需要恢复的运行");
                return;
            }

            // 重置轮询状态
            _tailStdoutPartial = string.Empty;
            _tailStderrPartial = string.Empty;
            _tailEventsPartial = string.Empty;

            // 首先读取所有遗漏的输出
            TailActiveRunFilesForce(runId);

            if (state.activeStatus != "running")
            {
                // 不是运行状态，检查是否需要完成
                AppendFinalSummaryIfNeeded(runId);
                return;
            }

            // 检查进程是否还在运行
            if (state.activePid > 0 && IsProcessAlive(state.activePid))
            {
                // 进程还在！继续轮询输出
                Debug.Log($"[CodexUnity] 进程 {state.activePid} 仍在运行，继续轮询输出");
                _isRunning = true; // 标记为运行中，但不持有进程引用
                AppendSystemMessage(runId, "info", $"检测到进程 {state.activePid} 仍在运行，继续监控...");
            }
            else
            {
                // 进程已死
                Debug.Log($"[CodexUnity] 进程 {state.activePid} 已结束");

                // 读取最后的输出

                TailActiveRunFilesForce(runId);

                // 更新状态

                state.activeStatus = "completed";
                state.activePid = 0;
                CodexStore.SaveState(state);


                AppendFinalSummaryIfNeeded(runId);
                AppendSystemMessage(runId, "info", "任务已完成（进程在 Domain Reload 期间结束）");
            }

            RunStatusChanged?.Invoke();
        }

        private static void RefreshDebugFlag()
        {
            _debugEnabled = CodexStore.LoadState().debug;
        }
    }
}
