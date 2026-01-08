using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace CodexUnity
{
    /// <summary>
    /// 负责拼命令、启动进程、写 meta/state
    /// </summary>
    public static class CodexRunner
    {
        private static string _currentRunId;
        private static bool _isRunning;
        private static double _pollStartTime;
        private static Action<string> _onComplete;
        private static Action<string> _onError;

        public static bool IsRunning => _isRunning;
        public static string CurrentRunId => _currentRunId;

        /// <summary>
        /// 检查 codex 是否可用
        /// </summary>
        public static (bool available, string version) CheckCodexAvailable()
        {
            try
            {
                // 使用 cmd.exe /c 包装命令，确保 PATH 环境变量被正确加载
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c codex --version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return (false, null);

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
                Debug.LogWarning($"[CodexUnity] 检查 codex 失败: {e.Message}");
                return (false, null);
            }
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

            // 校验 Git 仓库
            if (!CodexStore.HasGitRepository())
            {
                onError?.Invoke("请先在项目根目录执行 git init（本插件要求 git repo）");
                return;
            }

            // 生成 runId
            _currentRunId = CodexStore.GenerateRunId();
            var outPath = CodexStore.GetOutPath(_currentRunId);
            var runDir = CodexStore.GetRunDir(_currentRunId);

            // 确保运行目录存在
            if (!Directory.Exists(runDir))
            {
                Directory.CreateDirectory(runDir);
            }

            // 构建命令参数
            var args = BuildArguments(prompt, model, effort, resume, outPath);

            // 写 meta.json
            var meta = new RunMeta
            {
                runId = _currentRunId,
                command = $"codex {args}",
                prompt = prompt,
                model = model,
                effort = effort,
                time = CodexStore.GetIso8601Timestamp(),
                historyWritten = false
            };
            CodexStore.SaveRunMeta(meta);

            // 更新 state.json
            var state = CodexStore.LoadState();
            state.hasActiveThread = true;
            state.lastRunId = _currentRunId;
            state.lastRunOutPath = outPath;
            state.model = model;
            state.effort = effort;
            CodexStore.SaveState(state);

            // 记录回调
            _onComplete = onComplete;
            _onError = onError;
            _isRunning = true;
            _pollStartTime = EditorApplication.timeSinceStartup;

            // 启动进程
            try
            {
                // 使用 cmd.exe /c 包装命令，确保 PATH 环境变量被正确加载
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c codex {args}",
                    WorkingDirectory = CodexStore.ProjectRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                };

                var process = Process.Start(psi);
                if (process == null)
                {
                    _isRunning = false;
                    onError?.Invoke("无法启动 codex 进程");
                    return;
                }

                Debug.Log($"[CodexUnity] 启动 codex 进程 PID={process.Id}, runId={_currentRunId}");
                Debug.Log($"[CodexUnity] 命令: cmd.exe /c codex {args}");

                // 注册轮询
                EditorApplication.update += PollOutput;
            }
            catch (Exception e)
            {
                _isRunning = false;
                onError?.Invoke($"启动 codex 失败: {e.Message}");
            }
        }

        /// <summary>
        /// 构建命令参数
        /// </summary>
        private static string BuildArguments(string prompt, string model, string effort, bool resume, string outPath)
        {
            var sb = new StringBuilder();

            if (resume)
            {
                sb.Append("exec resume --last ");
            }
            else
            {
                sb.Append("exec ");
            }

            sb.Append("--full-auto ");
            sb.Append($"-C \"{CodexStore.ProjectRoot}\" ");
            sb.Append($"--model \"{model}\" ");
            sb.Append($"-c model_reasoning_effort={effort} ");
            sb.Append($"-o \"{outPath}\" ");
            sb.Append($"\"{EscapePrompt(prompt)}\"");

            return sb.ToString();
        }

        /// <summary>
        /// 转义 prompt 中的特殊字符
        /// </summary>
        private static string EscapePrompt(string prompt)
        {
            // 转义双引号和反斜杠
            return prompt.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        /// <summary>
        /// 轮询输出文件
        /// </summary>
        private static void PollOutput()
        {
            if (!_isRunning || string.IsNullOrEmpty(_currentRunId))
            {
                EditorApplication.update -= PollOutput;
                return;
            }

            var outPath = CodexStore.GetOutPath(_currentRunId);

            // 检查文件是否存在且有内容
            if (File.Exists(outPath))
            {
                try
                {
                    var fileInfo = new FileInfo(outPath);
                    if (fileInfo.Length > 0)
                    {
                        // 读取输出
                        var output = File.ReadAllText(outPath);

                        // 更新 meta
                        var meta = CodexStore.LoadRunMeta(_currentRunId);
                        if (meta != null)
                        {
                            meta.historyWritten = true;
                            CodexStore.SaveRunMeta(meta);
                        }

                        // 追加到历史
                        var historyItem = new HistoryItem
                        {
                            ts = CodexStore.GetIso8601Timestamp(),
                            role = "assistant",
                            text = output,
                            runId = _currentRunId
                        };
                        CodexStore.AppendHistory(historyItem);

                        // 停止轮询
                        _isRunning = false;
                        EditorApplication.update -= PollOutput;

                        Debug.Log($"[CodexUnity] 运行完成 runId={_currentRunId}");
                        _onComplete?.Invoke(output);
                        return;
                    }
                }
                catch (IOException)
                {
                    // 文件可能还在被写入，下次再试
                }
            }

            // 超时检查（10分钟）
            if (EditorApplication.timeSinceStartup - _pollStartTime > 600)
            {
                _isRunning = false;
                EditorApplication.update -= PollOutput;
                _onError?.Invoke("运行超时（10分钟）");
            }
        }

        /// <summary>
        /// 取消当前运行
        /// </summary>
        public static void Cancel()
        {
            if (_isRunning)
            {
                _isRunning = false;
                EditorApplication.update -= PollOutput;
                Debug.Log("[CodexUnity] 运行已取消");
            }
        }

        /// <summary>
        /// 检查并恢复未完成的运行
        /// </summary>
        public static void CheckAndRecoverPendingRun()
        {
            var state = CodexStore.LoadState();
            if (string.IsNullOrEmpty(state.lastRunId) || string.IsNullOrEmpty(state.lastRunOutPath))
            {
                return;
            }

            // 检查 meta 是否已标记为写入历史
            var meta = CodexStore.LoadRunMeta(state.lastRunId);
            if (meta != null && meta.historyWritten)
            {
                return;
            }

            // 检查 out.txt 是否存在且有内容
            if (File.Exists(state.lastRunOutPath))
            {
                try
                {
                    var fileInfo = new FileInfo(state.lastRunOutPath);
                    if (fileInfo.Length > 0)
                    {
                        var output = File.ReadAllText(state.lastRunOutPath);

                        // 检查历史中是否已有该 runId
                        var history = CodexStore.LoadHistory();
                        foreach (var item in history)
                        {
                            if (item.runId == state.lastRunId && item.role == "assistant")
                            {
                                // 已存在，更新 meta
                                if (meta != null)
                                {
                                    meta.historyWritten = true;
                                    CodexStore.SaveRunMeta(meta);
                                }
                                return;
                            }
                        }

                        // 补写历史
                        var historyItem = new HistoryItem
                        {
                            ts = CodexStore.GetIso8601Timestamp(),
                            role = "assistant",
                            text = output,
                            runId = state.lastRunId
                        };
                        CodexStore.AppendHistory(historyItem);

                        // 更新 meta
                        if (meta != null)
                        {
                            meta.historyWritten = true;
                            CodexStore.SaveRunMeta(meta);
                        }

                        Debug.Log($"[CodexUnity] 已恢复未写入的运行结果 runId={state.lastRunId}");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CodexUnity] 恢复运行结果失败: {e.Message}");
                }
            }
        }
    }
}
