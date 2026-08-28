using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using mySQLPunk;

namespace mySQLPunk.lib
{
    /// <summary>一則對話訊息（role: system / user / assistant）。</summary>
    public class AiChatMessage
    {
        public string Role;
        public string Content;

        public AiChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }

    /// <summary>一個 AI 服務供應商的預設組態。使用者訂閱哪家就選哪家。</summary>
    public class AiProviderPreset
    {
        public string Id;
        public string DisplayName;
        public string Endpoint;
        public string DefaultModel;
        /// <summary>bearer（Authorization: Bearer）、x-api-key（Anthropic）、api-key（Azure）、none（本機服務）</summary>
        public string AuthStyle;
        public bool NeedsKey;
        /// <summary>取得金鑰／註冊認證的網頁（本機服務則是下載頁），讓使用者一鍵跳過去。</summary>
        public string KeySignupUrl;

        public AiProviderPreset(string id, string displayName, string endpoint, string defaultModel, string authStyle, bool needsKey, string keySignupUrl)
        {
            Id = id; DisplayName = displayName; Endpoint = endpoint;
            DefaultModel = defaultModel; AuthStyle = authStyle; NeedsKey = needsKey;
            KeySignupUrl = keySignupUrl;
        }
    }

    public enum AiCliAccountState
    {
        SignedIn,
        NotFound,
        Unknown,
        Unsupported
    }

    /// <summary>
    /// 從 CLI 自己的本機設定檔讀到的非敏感帳號資訊。
    /// 只保留帳號標籤與登入方式，token、金鑰和 credential 值一律不離開解析流程。
    /// </summary>
    public sealed class AiCliAccountInfo
    {
        public AiCliAccountState State;
        public string Label;
        public string Method;
    }

    public sealed class AiCliDetectionResult
    {
        public AiProviderPreset Preset;
        public string Executable;
        public string ExecutablePath;
        public AiCliAccountInfo Account;

        public bool Installed
        {
            get { return !string.IsNullOrWhiteSpace(ExecutablePath); }
        }
    }

    /// <summary>AI 助理的服務設定。除 Anthropic 走原生 Messages API 外，其餘都走 OpenAI 相容介面。</summary>
    public class AiChatSettings
    {
        public string Provider;
        public string Endpoint;
        public string Model;

        public AiProviderPreset Preset
        {
            get { return AiChatService.FindPreset(Provider); }
        }

        public static AiChatSettings Load()
        {
            AiChatSettings s = new AiChatSettings
            {
                Provider = ApplicationOptionSettings.GetString("AiProvider"),
                Endpoint = (ApplicationOptionSettings.GetString("AiEndpoint") ?? "").Trim(),
                Model = (ApplicationOptionSettings.GetString("AiModel") ?? "").Trim()
            };
            if (string.IsNullOrWhiteSpace(s.Provider)) s.Provider = "openai";

            AiProviderPreset preset = AiChatService.FindPreset(s.Provider);
            if (string.IsNullOrWhiteSpace(s.Endpoint)) s.Endpoint = preset.Endpoint;
            if (string.IsNullOrWhiteSpace(s.Model)) s.Model = preset.DefaultModel;
            s.Model = AiChatService.NormalizeCliModel(s.Provider, s.Model);
            return s;
        }
    }

    public static class AiChatService
    {
        /// <summary>舊版單一金鑰的儲存位置（相容用）。</summary>
        public const string LegacyApiKeyCredentialTarget = "mySQLPunk:ai-api-key";

        /// <summary>
        /// 支援的供應商清單。使用者訂閱哪家就選哪家；本機服務（Ollama / LM Studio）不用金鑰。
        /// GitHub Models 官方已進入退場期，僅保留相容選項。
        /// </summary>
        public static readonly AiProviderPreset[] Presets = new[]
        {
            new AiProviderPreset("codex-cli",  "Codex CLI（ChatGPT 訂閱）",  "", "", "cli", false, "https://developers.openai.com/codex/cli"),
            new AiProviderPreset("claude-cli", "Claude Code CLI（Claude 訂閱）", "", "", "cli", false, "https://claude.com/claude-code"),
            new AiProviderPreset("gemini-cli", "Gemini CLI（Google 帳號）",  "", "", "cli", false, "https://github.com/google-gemini/gemini-cli"),
            new AiProviderPreset("openai",    "OpenAI",                 "https://api.openai.com/v1",                                  "gpt-4o-mini",               "bearer",    true,  "https://platform.openai.com/api-keys"),
            new AiProviderPreset("anthropic", "Anthropic Claude",       "https://api.anthropic.com",                                  "claude-haiku-4-5",          "x-api-key", true,  "https://console.anthropic.com/settings/keys"),
            new AiProviderPreset("gemini",    "Google Gemini",          "https://generativelanguage.googleapis.com/v1beta/openai",    "gemini-2.0-flash",          "bearer",    true,  "https://aistudio.google.com/apikey"),
            new AiProviderPreset("azure",     "Azure OpenAI",           "",                                                            "",                          "api-key",   true,  "https://portal.azure.com"),
            new AiProviderPreset("openrouter","OpenRouter",             "https://openrouter.ai/api/v1",                               "openai/gpt-4o-mini",        "bearer",    true,  "https://openrouter.ai/settings/keys"),
            new AiProviderPreset("groq",      "Groq",                   "https://api.groq.com/openai/v1",                             "llama-3.3-70b-versatile",   "bearer",    true,  "https://console.groq.com/keys"),
            new AiProviderPreset("deepseek",  "DeepSeek",               "https://api.deepseek.com/v1",                                "deepseek-chat",             "bearer",    true,  "https://platform.deepseek.com/api_keys"),
            new AiProviderPreset("xai",       "xAI Grok",               "https://api.x.ai/v1",                                        "grok-3-mini",               "bearer",    true,  "https://console.x.ai"),
            new AiProviderPreset("ollama",    "Ollama（本機）",          "http://localhost:11434/v1",                                  "llama3.1",                  "none",      false, "https://ollama.com/download"),
            new AiProviderPreset("lmstudio",  "LM Studio（本機）",       "http://localhost:1234/v1",                                   "",                          "none",      false, "https://lmstudio.ai"),
            new AiProviderPreset("github",    "GitHub Models（退場中）", "https://models.github.ai/inference",                         "openai/gpt-4o-mini",        "bearer",    true,  "https://github.com/settings/tokens"),
            new AiProviderPreset("custom",    "自訂 OpenAI 相容端點",    "",                                                            "",                          "bearer",    true,  ""),
        };

        public static AiProviderPreset FindPreset(string providerId)
        {
            foreach (AiProviderPreset p in Presets)
            {
                if (string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return Presets[0];
        }

        // ── 金鑰：一家一把，存 Windows 認證管理員 ────────────────────

        public static string ApiKeyTargetFor(string providerId)
        {
            return "mySQLPunk:ai-api-key:" + (providerId ?? "").ToLowerInvariant();
        }

        public static bool TryReadApiKey(string providerId, out string apiKey)
        {
            if (WindowsCredentialService.TryReadPassword(ApiKeyTargetFor(providerId), out apiKey)
                && !string.IsNullOrWhiteSpace(apiKey)) return true;
            // 相容 1.0.0.9 之前的單一金鑰
            return WindowsCredentialService.TryReadPassword(LegacyApiKeyCredentialTarget, out apiKey)
                && !string.IsNullOrWhiteSpace(apiKey);
        }

        public static bool HasApiKey(string providerId)
        {
            string key;
            return TryReadApiKey(providerId, out key);
        }

        // ── 對話 ────────────────────────────────────────────────────

        /// <summary>同步呼叫（請包在 Task.Run 裡），回傳 assistant 的完整回覆文字。</summary>
        public static string ChatCompletion(AiChatSettings settings, IList<AiChatMessage> messages)
        {
            if (settings.Preset.AuthStyle == "cli")
            {
                return CliChat(settings, messages);
            }
            if (string.Equals(settings.Provider, "anthropic", StringComparison.OrdinalIgnoreCase))
            {
                return AnthropicChat(settings, messages);
            }
            return OpenAiCompatibleChat(settings, messages);
        }

        // ── 本機 CLI 後端（走使用者訂閱，不用 API 金鑰）────────────
        // Codex CLI（codex exec）、Claude Code（claude -p）、Gemini CLI 都有
        // 官方的非互動模式：prompt 從 stdin 餵進去、回覆從 stdout 收回來。
        // 用 cmd /c 啟動，npm 的 .cmd shim 與 winget 的 exe 都吃得到。

        /// <summary>該 CLI 供應商的預設執行檔名（Endpoint 欄位可覆寫成完整路徑）。</summary>
        public static string CliExecutableFor(string providerId)
        {
            switch ((providerId ?? "").ToLowerInvariant())
            {
                case "codex-cli": return "codex";
                case "claude-cli": return "claude";
                case "gemini-cli": return "gemini";
                default: return null;
            }
        }

        private static string CliChat(AiChatSettings settings, IList<AiChatMessage> messages)
        {
            string exe = !string.IsNullOrWhiteSpace(settings.Endpoint) ? settings.Endpoint : CliExecutableFor(settings.Provider);
            string model = NormalizeCliModel(settings.Provider, settings.Model);
            string promptText = BuildCliPrompt(messages);
            string workspace = EnsureCliWorkspaceDirectory();

            string[][] argumentTries;
            switch (settings.Provider)
            {
                case "codex-cli":
                    // read-only sandbox + mySQLPunk 專屬空白工作目錄：純問答，不讓它動到使用者檔案。
                    // 官方支援 projects.<path>.trust_level；只信任這個專屬目錄，不把整個 TEMP 設成可信任。
                    // 各版本 codex 支援的旗標不一，不吃就逐步降階重試
                    ValidateCliModel(model);
                    argumentTries = new[]
                    {
                        BuildCodexArguments(model, true, true, workspace, true),
                        BuildCodexArguments(model, true, false, workspace, true),
                        BuildCodexArguments(model, false, false, workspace, true),
                        BuildCodexArguments(model, false, false, workspace, false)
                    };
                    break;
                case "claude-cli":
                    // Claude 的非互動 -p 模式會略過 workspace trust 對話。
                    ValidateCliModel(model);
                    argumentTries = new[] { model.Length > 0 ? new[] { "-p", "--model", model } : new[] { "-p" } };
                    break;
                case "gemini-cli":
                    ValidateCliModel(model);
                    string[] trustedGeminiArguments = model.Length > 0
                        ? new[] { "--skip-trust", "-m", model }
                        : new[] { "--skip-trust" };
                    string[] legacyGeminiArguments = model.Length > 0 ? new[] { "-m", model } : new string[0];
                    argumentTries = new[] { trustedGeminiArguments, legacyGeminiArguments };
                    break;
                default:
                    throw new InvalidOperationException("unknown cli provider: " + settings.Provider);
            }

            InvalidOperationException lastError = null;
            for (int i = 0; i < argumentTries.Length; i++)
            {
                try
                {
                    return RunCliProcess(exe, argumentTries[i], promptText, 180000);
                }
                catch (InvalidOperationException ex)
                {
                    lastError = ex;
                    // 只有「旗標不認得」這類用法錯誤才降階重試，其它錯誤直接回報
                    string msg = ex.Message ?? "";
                    bool usageError = msg.IndexOf("unexpected argument", StringComparison.OrdinalIgnoreCase) >= 0
                        || msg.IndexOf("unrecognized", StringComparison.OrdinalIgnoreCase) >= 0
                        || msg.IndexOf("invalid option", StringComparison.OrdinalIgnoreCase) >= 0
                        || msg.IndexOf("USAGE", StringComparison.Ordinal) >= 0
                        || msg.IndexOf("Usage:", StringComparison.Ordinal) >= 0;
                    if (!usageError || i == argumentTries.Length - 1) throw;
                }
            }
            throw lastError;
        }

        private static string[] BuildCodexArguments(string model, bool skipGitCheck, bool readOnlySandbox, string workspace, bool trustWorkspace)
        {
            List<string> arguments = new List<string> { "exec" };
            if (trustWorkspace)
            {
                arguments.Add("-c");
                arguments.Add(BuildCodexTrustOverride(workspace));
            }
            if (skipGitCheck) arguments.Add("--skip-git-repo-check");
            if (readOnlySandbox)
            {
                arguments.Add("-s");
                arguments.Add("read-only");
            }
            if (!string.IsNullOrWhiteSpace(model))
            {
                arguments.Add("-m");
                arguments.Add(model);
            }
            arguments.Add("-");
            return arguments.ToArray();
        }

        /// <summary>Codex CLI 的單次專案信任覆寫，不修改使用者的全域 config.toml。</summary>
        public static string BuildCodexTrustOverride(string workspace)
        {
            string fullPath = Path.GetFullPath(workspace ?? "");
            string tomlPath = fullPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "projects.\"" + tomlPath + "\".trust_level=\"trusted\"";
        }

        /// <summary>所有 AI CLI 共用的隔離工作目錄；避免信任整個 Windows 暫存根目錄。</summary>
        public static string EnsureCliWorkspaceDirectory()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
            string workspace = Path.GetFullPath(Path.Combine(root, "mySQLPunk", "AiCliWorkspace"));
            Directory.CreateDirectory(workspace);
            return workspace;
        }

        private static void ValidateCliModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return;
            for (int i = 0; i < model.Length; i++)
            {
                char c = model[i];
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == '/' || c == ':') continue;
                throw new InvalidOperationException(Localization.T("Ai.CliInvalidModel"));
            }
        }

        /// <summary>CLI 是一次一問，把系統指示、上下文與對話攤平成一份 prompt。</summary>
        private static string BuildCliPrompt(IList<AiChatMessage> messages)
        {
            StringBuilder prompt = new StringBuilder();
            foreach (AiChatMessage m in messages)
            {
                if (m.Role == "system")
                {
                    prompt.AppendLine(m.Content);
                    prompt.AppendLine();
                }
            }
            bool hasHistory = false;
            foreach (AiChatMessage m in messages)
            {
                if (m.Role == "system") continue;
                hasHistory = true;
                prompt.AppendLine((m.Role == "assistant" ? "[助理]" : "[使用者]"));
                prompt.AppendLine(m.Content);
                prompt.AppendLine();
            }
            if (hasHistory) prompt.AppendLine("請以助理身分直接回覆最後一則使用者訊息，不要重複前面的對話。");

            return prompt.ToString();
        }

        /// <summary>跑 CLI 的 --version 當「測試連線」：確認裝了、抓得到。</summary>
        public static string CliVersion(AiChatSettings settings)
        {
            string exe = !string.IsNullOrWhiteSpace(settings.Endpoint) ? settings.Endpoint : CliExecutableFor(settings.Provider);
            return RunCliProcess(exe, new[] { "--version" }, null, 20000);
        }

        private static string RunCliProcess(string exe, IList<string> arguments, string stdin, int timeoutMs)
        {
            string resolvedExe = ResolveCliExecutablePath(exe);
            if (string.IsNullOrWhiteSpace(resolvedExe))
                throw new InvalidOperationException(Localization.Format("Ai.CliNotFound", exe));

            var psi = BuildCliProcessStartInfo(resolvedExe, arguments);
            psi.EnvironmentVariables["NO_COLOR"] = "1";
            psi.EnvironmentVariables["FORCE_COLOR"] = "0";
            psi.EnvironmentVariables["CLICOLOR"] = "0";

            System.Diagnostics.Process started;
            try
            {
                started = System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(Localization.Format("Ai.CliFailed", exe, ex.Message), ex);
            }

            using (var process = started)
            {
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                process.OutputDataReceived += (s, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                process.ErrorDataReceived += (s, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (stdin != null)
                {
                    using (var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
                    {
                        writer.Write(stdin);
                    }
                }
                else
                {
                    process.StandardInput.Close();
                }

                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                    throw new InvalidOperationException(Localization.Format("Ai.CliTimeout", exe));
                }
                process.WaitForExit(); // 等非同步輸出讀完

                // CLI 常在輸出裡夾 ANSI 色碼與進度控制字元，不洗掉的話
                // 錯誤訊息會變成一串亂碼，真正的原因反而看不到
                string output = SanitizeCliText(stdout.ToString());
                if (process.ExitCode != 0)
                {
                    string detail = SanitizeCliText(stderr.ToString());
                    if (detail.Length == 0) detail = output;
                    if (detail.IndexOf("不是內部或外部命令", StringComparison.Ordinal) >= 0
                        || detail.IndexOf("is not recognized", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        throw new InvalidOperationException(Localization.Format("Ai.CliNotFound", exe));
                    }
                    throw new InvalidOperationException(Localization.Format("Ai.CliFailed", exe, Truncate(detail, 400)));
                }
                if (output.Length == 0)
                {
                    throw new InvalidOperationException(Localization.Format("Ai.CliFailed", exe, Truncate(SanitizeCliText(stderr.ToString()), 400)));
                }
                return output;
            }
        }

        /// <summary>
        /// 解析 CLI 的實際檔案，避免透過 cmd.exe 再找一次 PATH，導致「明明偵測得到、執行時卻找不到」。
        /// </summary>
        public static string ResolveCliExecutablePath(string executable)
        {
            string candidate = (executable ?? "").Trim();
            if (candidate.Length >= 2 && candidate[0] == '"' && candidate[candidate.Length - 1] == '"')
                candidate = candidate.Substring(1, candidate.Length - 2);
            if (candidate.Length == 0) return null;

            bool explicitPath = Path.IsPathRooted(candidate)
                || candidate.IndexOf(Path.DirectorySeparatorChar) >= 0
                || candidate.IndexOf(Path.AltDirectorySeparatorChar) >= 0;
            if (explicitPath) return FirstExistingCliPath(candidate);

            for (int i = 0; i < candidate.Length; i++)
            {
                char c = candidate[i];
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') continue;
                return null;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (string folder in new[]
            {
                Path.Combine(localAppData, "Microsoft", "WinGet", "Links"),
                Path.Combine(localAppData, "Microsoft", "WindowsApps"),
                Path.Combine(appData, "npm")
            })
            {
                string found = FirstExistingCliPath(Path.Combine(folder, candidate));
                if (found != null) return found;
            }

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = QuoteWindowsArgument(candidate),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.Default,
                    StandardErrorEncoding = Encoding.Default
                };
                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(5000))
                    {
                        try { process.Kill(); } catch { }
                        return null;
                    }
                    if (process.ExitCode != 0) return null;
                    foreach (string line in stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string found = FirstExistingCliPath(line.Trim());
                        if (found != null) return found;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string FirstExistingCliPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (File.Exists(path)) return Path.GetFullPath(path);
                if (Path.GetExtension(path).Length == 0)
                {
                    foreach (string extension in new[] { ".exe", ".cmd", ".bat" })
                    {
                        string withExtension = path + extension;
                        if (File.Exists(withExtension)) return Path.GetFullPath(withExtension);
                    }
                }
            }
            catch { }
            return null;
        }

        private static System.Diagnostics.ProcessStartInfo BuildCliProcessStartInfo(string resolvedExe, IList<string> arguments)
        {
            string extension = Path.GetExtension(resolvedExe);
            bool isBatch = string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = EnsureCliWorkspaceDirectory()
            };

            string joinedArguments = JoinWindowsArguments(arguments);
            if (isBatch)
            {
                psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                psi.EnvironmentVariables["MYSQLPUNK_AI_CLI"] = resolvedExe;
                string command = "chcp 65001 >nul & call \"%MYSQLPUNK_AI_CLI%\"";
                if (joinedArguments.Length > 0) command += " " + joinedArguments;
                psi.Arguments = "/d /s /c \"" + command + "\"";
            }
            else
            {
                psi.FileName = resolvedExe;
                psi.Arguments = joinedArguments;
            }
            return psi;
        }

        private static string JoinWindowsArguments(IList<string> arguments)
        {
            if (arguments == null || arguments.Count == 0) return "";
            StringBuilder joined = new StringBuilder();
            for (int i = 0; i < arguments.Count; i++)
            {
                if (i > 0) joined.Append(' ');
                joined.Append(QuoteWindowsArgument(arguments[i] ?? ""));
            }
            return joined.ToString();
        }

        private static string QuoteWindowsArgument(string argument)
        {
            if (argument == null || argument.Length == 0) return "\"\"";
            bool needsQuotes = false;
            for (int i = 0; i < argument.Length; i++)
            {
                char c = argument[i];
                if (char.IsWhiteSpace(c) || c == '"') { needsQuotes = true; break; }
            }
            if (!needsQuotes) return argument;

            StringBuilder quoted = new StringBuilder(argument.Length + 2);
            quoted.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    quoted.Append('\\', backslashes * 2 + 1);
                    quoted.Append('"');
                    backslashes = 0;
                    continue;
                }
                quoted.Append('\\', backslashes);
                backslashes = 0;
                quoted.Append(c);
            }
            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        /// <summary>把 ANSI escape（CSI/OSC）與不可列印的控制字元從 CLI 輸出裡清掉。</summary>
        private static string SanitizeCliText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\x1b')
                {
                    if (i + 1 < text.Length && text[i + 1] == '[')
                    {
                        // CSI:吃到結尾字母為止
                        i++;
                        while (i + 1 < text.Length)
                        {
                            i++;
                            char t = text[i];
                            if (t >= '@' && t <= '~') break;
                        }
                    }
                    else if (i + 1 < text.Length && text[i + 1] == ']')
                    {
                        // OSC:吃到 BEL 或 ST 為止
                        i++;
                        while (i + 1 < text.Length)
                        {
                            i++;
                            if (text[i] == '\x07') break;
                            if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '\\') { i++; break; }
                        }
                    }
                    continue;
                }
                if (c == '\r' || c == '\n' || c == '\t' || c >= ' ') sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        private static string OpenAiCompatibleChat(AiChatSettings settings, IList<AiChatMessage> messages)
        {
            // codex 系列模型（gpt-5-codex / gpt-5.1-codex…）只支援 Responses API，
            // 不支援 chat/completions，偵測到就自動改走
            if (string.Equals(settings.Provider, "openai", StringComparison.OrdinalIgnoreCase)
                && settings.Model != null
                && settings.Model.IndexOf("codex", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return OpenAiResponsesChat(settings, messages);
            }

            string baseUrl = settings.Endpoint.TrimEnd('/');
            string url;
            if (string.Equals(settings.Provider, "azure", StringComparison.OrdinalIgnoreCase))
            {
                // Azure 的端點填到 deployment 為止，例如
                // https://res.openai.azure.com/openai/deployments/gpt-4o-mini
                url = baseUrl + "/chat/completions";
                if (url.IndexOf("api-version=", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    url += "?api-version=2024-06-01";
                }
            }
            else
            {
                url = baseUrl + "/chat/completions";
            }

            JArray messageArray = new JArray();
            foreach (AiChatMessage m in messages)
            {
                messageArray.Add(new JObject { ["role"] = m.Role, ["content"] = m.Content });
            }
            JObject body = new JObject
            {
                ["model"] = settings.Model,
                ["messages"] = messageArray
            };

            string responseText = PostJson(settings, url, body.ToString());
            JObject parsed = JObject.Parse(responseText);
            JToken choices = parsed["choices"];
            if (choices is JArray choiceArray && choiceArray.Count > 0)
            {
                JToken message = choiceArray[0]["message"];
                if (message != null)
                {
                    string content = (string)message["content"];
                    if (!string.IsNullOrEmpty(content)) return content.Trim();
                }
            }
            throw new InvalidOperationException("AI 服務回應了無法解析的內容: " + Truncate(responseText, 300));
        }

        /// <summary>OpenAI Responses API：system 放 instructions，回覆在 output 陣列的 message 項目裡。</summary>
        private static string OpenAiResponsesChat(AiChatSettings settings, IList<AiChatMessage> messages)
        {
            string url = settings.Endpoint.TrimEnd('/') + "/responses";

            StringBuilder instructions = new StringBuilder();
            JArray input = new JArray();
            foreach (AiChatMessage m in messages)
            {
                if (m.Role == "system")
                {
                    if (instructions.Length > 0) instructions.AppendLine();
                    instructions.Append(m.Content);
                }
                else
                {
                    input.Add(new JObject { ["role"] = m.Role, ["content"] = m.Content });
                }
            }
            JObject body = new JObject
            {
                ["model"] = settings.Model,
                ["input"] = input
            };
            if (instructions.Length > 0) body["instructions"] = instructions.ToString();

            string responseText = PostJson(settings, url, body.ToString());
            JObject parsed = JObject.Parse(responseText);

            StringBuilder sb = new StringBuilder();
            if (parsed["output"] is JArray output)
            {
                foreach (JToken item in output)
                {
                    if ((string)item["type"] != "message") continue;
                    if (item["content"] is JArray parts)
                    {
                        foreach (JToken part in parts)
                        {
                            if ((string)part["type"] == "output_text") sb.Append((string)part["text"]);
                        }
                    }
                }
            }
            if (sb.Length > 0) return sb.ToString().Trim();

            string outputText = (string)parsed["output_text"];
            if (!string.IsNullOrWhiteSpace(outputText)) return outputText.Trim();
            throw new InvalidOperationException("AI 服務回應了無法解析的內容: " + Truncate(responseText, 300));
        }

        /// <summary>Anthropic 原生 Messages API：system 獨立欄位、回覆在 content 陣列裡。</summary>
        private static string AnthropicChat(AiChatSettings settings, IList<AiChatMessage> messages)
        {
            string url = settings.Endpoint.TrimEnd('/') + "/v1/messages";

            StringBuilder system = new StringBuilder();
            JArray messageArray = new JArray();
            foreach (AiChatMessage m in messages)
            {
                if (m.Role == "system")
                {
                    if (system.Length > 0) system.AppendLine();
                    system.Append(m.Content);
                }
                else
                {
                    messageArray.Add(new JObject { ["role"] = m.Role, ["content"] = m.Content });
                }
            }
            JObject body = new JObject
            {
                ["model"] = settings.Model,
                ["max_tokens"] = 4096,
                ["messages"] = messageArray
            };
            if (system.Length > 0) body["system"] = system.ToString();

            string responseText = PostJson(settings, url, body.ToString());
            JObject parsed = JObject.Parse(responseText);
            if (parsed["content"] is JArray blocks)
            {
                StringBuilder sb = new StringBuilder();
                foreach (JToken block in blocks)
                {
                    if ((string)block["type"] == "text") sb.Append((string)block["text"]);
                }
                if (sb.Length > 0) return sb.ToString().Trim();
            }
            throw new InvalidOperationException("AI 服務回應了無法解析的內容: " + Truncate(responseText, 300));
        }

        // ── 模型清單與本機偵測（「看使用者支援什麼」）──────────────

        /// <summary>跟服務要可用的模型清單（同時當「測試連線」用：能拿到清單代表端點與金鑰都通）。</summary>
        public static List<string> ListModels(AiChatSettings settings)
        {
            if (settings.Preset.AuthStyle == "cli")
            {
                throw new InvalidOperationException(Localization.T("Ai.CliNoModels"));
            }
            List<string> models = new List<string>();
            string baseUrl = settings.Endpoint.TrimEnd('/');
            string url = string.Equals(settings.Provider, "anthropic", StringComparison.OrdinalIgnoreCase)
                ? baseUrl + "/v1/models"
                : baseUrl + "/models";

            string responseText = GetJson(settings, url);
            JObject parsed = JObject.Parse(responseText);
            if (parsed["data"] is JArray data)
            {
                foreach (JToken item in data)
                {
                    string id = (string)item["id"];
                    if (!string.IsNullOrWhiteSpace(id)) models.Add(id);
                }
            }
            models.Sort(StringComparer.OrdinalIgnoreCase);
            return models;
        }

        /// <summary>
        /// CLI 沒有「列模型」的 API，但各家可用的型號是已知的——給一份常用清單讓使用者挑，
        /// 留空就用該 CLI 的預設模型。
        /// </summary>
        public static string[] KnownCliModels(string providerId)
        {
            switch ((providerId ?? "").ToLowerInvariant())
            {
                case "codex-cli":
                    return new[] { "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna" };
                case "claude-cli":
                    return new[] { "sonnet", "opus", "haiku" };
                case "gemini-cli":
                    return new[] { "gemini-2.5-pro", "gemini-2.5-flash" };
                default:
                    return new string[0];
            }
        }

        /// <summary>已退場且 ChatGPT 訂閱路徑不再接受的 Codex 型號，改由 CLI 自行選擇目前預設。</summary>
        public static string NormalizeCliModel(string providerId, string model)
        {
            string value = (model ?? "").Trim();
            if (!string.Equals(providerId, "codex-cli", StringComparison.OrdinalIgnoreCase)) return value;

            string lower = value.ToLowerInvariant();
            if (lower == "gpt-5-codex"
                || lower == "gpt-5.2-codex"
                || lower == "codex-mini-latest"
                || lower.StartsWith("gpt-5.1-codex", StringComparison.Ordinal))
                return "";
            return value;
        }

        /// <summary>偵測本機已安裝的 AI CLI（codex / claude / gemini）：用 where 快查 PATH，不真的執行。</summary>
        public static List<AiProviderPreset> DetectInstalledClis()
        {
            var found = new List<AiProviderPreset>();
            foreach (AiCliDetectionResult result in DetectCliProviders())
            {
                if (result.Installed) found.Add(result.Preset);
            }
            return found;
        }

        /// <summary>
        /// 列出所有支援的訂閱型 CLI，包含可執行檔路徑與非敏感登入資訊。
        /// 此方法不執行 CLI，也不驗證遠端訂閱權限。
        /// </summary>
        public static List<AiCliDetectionResult> DetectCliProviders()
        {
            var results = new List<AiCliDetectionResult>();
            foreach (AiProviderPreset preset in Presets)
            {
                if (preset.AuthStyle != "cli") continue;
                string executable = CliExecutableFor(preset.Id);
                results.Add(new AiCliDetectionResult
                {
                    Preset = preset,
                    Executable = executable,
                    ExecutablePath = ResolveCliExecutablePath(executable),
                    Account = DetectCliAccount(preset.Id)
                });
            }
            return results;
        }

        public static AiCliAccountInfo ParseCliAccountInfo(string providerId, string rawJson)
        {
            try
            {
                JObject document = JObject.Parse(rawJson ?? "");
                switch ((providerId ?? "").ToLowerInvariant())
                {
                    case "codex-cli":
                        return ParseCodexAccount(document);
                    case "claude-cli":
                        string claudeEmail = SafeAccountLabel((string)document.SelectToken("oauthAccount.emailAddress"));
                        return BuildAccount(
                            AiCliAccountState.SignedIn,
                            claudeEmail,
                            "Claude.ai",
                            claudeEmail != null);
                    case "gemini-cli":
                        string geminiAccount = SafeAccountLabel((string)document["active"]);
                        return BuildAccount(
                            AiCliAccountState.SignedIn,
                            geminiAccount,
                            "Google",
                            geminiAccount != null);
                    default:
                        return BuildAccount(AiCliAccountState.Unsupported, null, null, false);
                }
            }
            catch
            {
                return BuildAccount(AiCliAccountState.Unknown, null, null, false);
            }
        }

        private static AiCliAccountInfo DetectCliAccount(string providerId)
        {
            string relativePath;
            switch ((providerId ?? "").ToLowerInvariant())
            {
                case "codex-cli": relativePath = Path.Combine(".codex", "auth.json"); break;
                case "claude-cli": relativePath = ".claude.json"; break;
                case "gemini-cli": relativePath = Path.Combine(".gemini", "google_accounts.json"); break;
                default: return BuildAccount(AiCliAccountState.Unsupported, null, null, false);
            }

            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(home)) home = Environment.GetEnvironmentVariable("USERPROFILE");
                if (string.IsNullOrWhiteSpace(home))
                    return BuildAccount(AiCliAccountState.Unknown, null, null, false);

                string path = Path.Combine(home, relativePath);
                if (!File.Exists(path))
                    return BuildAccount(AiCliAccountState.NotFound, null, null, false);
                return ParseCliAccountInfo(providerId, File.ReadAllText(path, Encoding.UTF8));
            }
            catch
            {
                return BuildAccount(AiCliAccountState.Unknown, null, null, false);
            }
        }

        private static AiCliAccountInfo ParseCodexAccount(JObject document)
        {
            string mode = ((string)document["auth_mode"] ?? "").Trim();
            string method = mode.Equals("chatgpt", StringComparison.OrdinalIgnoreCase)
                ? "ChatGPT"
                : mode.IndexOf("api", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "OpenAI API Key"
                    : "OpenAI Codex";
            string email = TryReadJwtEmail((string)document.SelectToken("tokens.id_token"));
            bool hasLoginMetadata = email != null || mode.Length > 0;
            return BuildAccount(AiCliAccountState.SignedIn, email, method, hasLoginMetadata);
        }

        private static string TryReadJwtEmail(string token)
        {
            try
            {
                string[] parts = (token ?? "").Split('.');
                if (parts.Length < 2) return null;
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                while (payload.Length % 4 != 0) payload += "=";
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                return SafeAccountLabel((string)JObject.Parse(json)["email"]);
            }
            catch
            {
                return null;
            }
        }

        private static string SafeAccountLabel(string value)
        {
            string label = (value ?? "").Trim();
            if (label.Length == 0 || label.Length > 320) return null;
            for (int i = 0; i < label.Length; i++)
            {
                if (char.IsControl(label[i])) return null;
            }
            return label;
        }

        private static AiCliAccountInfo BuildAccount(
            AiCliAccountState requestedState,
            string label,
            string method,
            bool hasLoginMetadata)
        {
            return new AiCliAccountInfo
            {
                State = requestedState == AiCliAccountState.SignedIn && !hasLoginMetadata
                    ? AiCliAccountState.Unknown
                    : requestedState,
                Label = label,
                Method = hasLoginMetadata ? method : null
            };
        }

        /// <summary>偵測本機推論服務（Ollama / LM Studio），回傳偵測到的供應商與其模型。</summary>
        public static List<KeyValuePair<AiProviderPreset, List<string>>> DetectLocalServices()
        {
            var found = new List<KeyValuePair<AiProviderPreset, List<string>>>();
            foreach (string id in new[] { "ollama", "lmstudio" })
            {
                AiProviderPreset preset = FindPreset(id);
                try
                {
                    AiChatSettings probe = new AiChatSettings { Provider = id, Endpoint = preset.Endpoint, Model = preset.DefaultModel };
                    List<string> models = ListModels(probe);
                    found.Add(new KeyValuePair<AiProviderPreset, List<string>>(preset, models));
                }
                catch
                {
                    // 沒開就跳過
                }
            }
            return found;
        }

        // ── HTTP 底層 ────────────────────────────────────────────────

        private static HttpWebRequest CreateRequest(AiChatSettings settings, string url, string method)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.ContentType = "application/json";
            request.Timeout = method == "GET" ? 8000 : 120000;
            request.ReadWriteTimeout = 120000;
            request.UserAgent = "mySQLPunk-ai-assistant";
            System.Net.IWebProxy proxy = ConnectionProxySettingsService.CreateWebProxyFromOptions();
            if (proxy != null) request.Proxy = proxy;

            AiProviderPreset preset = settings.Preset;
            string apiKey;
            bool hasKey = TryReadApiKey(settings.Provider, out apiKey);
            if (hasKey) apiKey = apiKey.Trim();
            switch (preset.AuthStyle)
            {
                case "x-api-key":
                    if (hasKey) request.Headers["x-api-key"] = apiKey;
                    request.Headers["anthropic-version"] = "2023-06-01";
                    break;
                case "api-key":
                    if (hasKey) request.Headers["api-key"] = apiKey;
                    break;
                case "none":
                    break;
                default:
                    if (hasKey) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey;
                    break;
            }
            return request;
        }

        private static string PostJson(AiChatSettings settings, string url, string json)
        {
            HttpWebRequest request = CreateRequest(settings, url, "POST");
            byte[] payload = Encoding.UTF8.GetBytes(json);
            using (Stream stream = request.GetRequestStream())
            {
                stream.Write(payload, 0, payload.Length);
            }
            return ReadResponse(request);
        }

        private static string GetJson(AiChatSettings settings, string url)
        {
            HttpWebRequest request = CreateRequest(settings, url, "GET");
            return ReadResponse(request);
        }

        private static string ReadResponse(HttpWebRequest request)
        {
            try
            {
                using (WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                // 盡量把服務端的錯誤訊息帶出來（額度、金鑰、模型名稱錯誤都在這裡）
                string detail = ex.Message;
                try
                {
                    if (ex.Response != null)
                    {
                        using (StreamReader reader = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8))
                        {
                            string raw = reader.ReadToEnd();
                            JObject err = JObject.Parse(raw);
                            string msg = (string)(err["error"] is JObject errObj ? errObj["message"] : err["message"]);
                            if (!string.IsNullOrWhiteSpace(msg)) detail = msg;
                            else if (!string.IsNullOrWhiteSpace(raw)) detail = raw;
                        }
                    }
                }
                catch { }
                throw new InvalidOperationException(detail, ex);
            }
        }

        /// <summary>取出回覆裡最後一段 ```sql 程式碼區塊（沒有語言標記的區塊也接受）。</summary>
        public static string ExtractLastSqlBlock(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return null;
            string result = null;
            int index = 0;
            while (true)
            {
                int start = reply.IndexOf("```", index, StringComparison.Ordinal);
                if (start < 0) break;
                int lineEnd = reply.IndexOf('\n', start);
                if (lineEnd < 0) break;
                string lang = reply.Substring(start + 3, lineEnd - start - 3).Trim().ToLowerInvariant();
                int end = reply.IndexOf("```", lineEnd, StringComparison.Ordinal);
                if (end < 0) break;
                string block = reply.Substring(lineEnd + 1, end - lineEnd - 1).Trim();
                if (block.Length > 0 && (lang == "sql" || lang == "")) result = block;
                index = end + 3;
            }
            return result;
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max) + "…";
        }
    }
}
