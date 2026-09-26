using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace mySQLPunk.lib
{
    /// <summary>工作區要讀寫的本機位置；由呼叫端提供，測試可指向暫存資料夾。</summary>
    public sealed class WorkspaceSources
    {
        /// <summary>目前設定檔的連線 JSON（與「匯出連線」相同的 {connections, groups} 格式）。</summary>
        public string ConnectionsJson { get; set; }
        public string SnippetsPath { get; set; }
        public string AiActionsPath { get; set; }
        public ScheduledJobStore JobStore { get; set; }
        public string DictionaryDirectory { get; set; }
    }

    public sealed class WorkspaceExportOptions
    {
        /// <summary>預設不含登入帳號（隊友通常用自己的帳號）。</summary>
        public bool IncludeUserNames { get; set; }
        /// <summary>Webhook 網址常含權杖，預設不匯出。</summary>
        public bool IncludeWebhooks { get; set; }
    }

    public enum WorkspaceItemState
    {
        New,
        Changed,
        Same
    }

    public sealed class WorkspaceImportItem
    {
        public string Category { get; set; }
        public string Name { get; set; }
        public WorkspaceItemState State { get; set; }
        internal Action Apply { get; set; }
    }

    public sealed class WorkspaceImportPlan
    {
        public WorkspaceImportPlan()
        {
            Items = new List<WorkspaceImportItem>();
            Warnings = new List<string>();
        }

        public string Directory { get; set; }
        /// <summary>工作區內的連線檔（交給既有的連線匯入預覽處理）；沒有連線時為 null。</summary>
        public string ConnectionsPath { get; set; }
        public List<WorkspaceImportItem> Items { get; private set; }
        public List<string> Warnings { get; private set; }
    }

    /// <summary>
    /// 協同合作第一階段：把連線（不含密碼與憑證）、SQL 片段、AI 動作、自動執行作業與資料產生器字典匯出成資料夾，
    /// 所有 JSON 以固定順序與排序過的鍵輸出、不含時間戳記，適合放進 Git 版控與比對；匯入時逐項比較為新增／變更／相同，
    /// 只套用使用者選擇的項目，既有密碼與本機憑證不受影響。
    /// </summary>
    public static class WorkspaceBundleService
    {
        public const string Format = "mysqlpunk-workspace";
        public const int Version = 1;
        public const string ManifestFile = "workspace.json";
        public const string ConnectionsFile = "connections.json";
        public const string SnippetsFile = "snippets.json";
        public const string AiActionsFile = "ai-actions.json";
        public const string JobsFolder = "automation";
        public const string DictionariesFolder = "datagen-dictionaries";
        private static readonly string[] SecretKeyFragments = { "password", "passwd", "pwd", "secret", "passphrase", "token", "private_key", "privatekey", "credential" };

        // ------------------------------------------------------------ Export

        public static Dictionary<string, int> Export(string directory, WorkspaceSources sources, WorkspaceExportOptions options)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("directory");
            options = options ?? new WorkspaceExportOptions();
            Directory.CreateDirectory(directory);
            Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.Ordinal);

            if (!string.IsNullOrWhiteSpace(sources.ConnectionsJson))
            {
                JObject connections = SanitizeConnections(JToken.Parse(sources.ConnectionsJson), options.IncludeUserNames);
                WriteJson(Path.Combine(directory, ConnectionsFile), connections);
                counts["connections"] = ((JArray)connections["connections"]).Count;
            }

            JArray snippets = ReadArray(sources.SnippetsPath);
            if (snippets != null)
            {
                WriteJson(Path.Combine(directory, SnippetsFile), new JArray(snippets.OfType<JObject>().OrderBy(item => (string)item["Shortcut"], StringComparer.OrdinalIgnoreCase)));
                counts["snippets"] = snippets.Count;
            }

            JArray actions = ReadArray(sources.AiActionsPath);
            if (actions != null)
            {
                WriteJson(Path.Combine(directory, AiActionsFile), new JArray(actions.OfType<JObject>().OrderBy(item => (string)item["Name"], StringComparer.OrdinalIgnoreCase)));
                counts["aiActions"] = actions.Count;
            }

            string jobsDirectory = Path.Combine(directory, JobsFolder);
            if (Directory.Exists(jobsDirectory))
            {
                foreach (string stale in Directory.GetFiles(jobsDirectory, "*.json")) File.Delete(stale);
            }
            if (sources.JobStore != null)
            {
                List<ScheduledJobDefinition> jobs = sources.JobStore.LoadJobs().Jobs;
                if (jobs.Count > 0) Directory.CreateDirectory(jobsDirectory);
                HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ScheduledJobDefinition job in jobs.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    JObject value = JObject.FromObject(job);
                    if (!options.IncludeWebhooks) value.Remove("WebhookUrl");
                    string file = UniqueFileName(Slug(job.Name), used) + ".json";
                    WriteJson(Path.Combine(jobsDirectory, file), value);
                }
                counts["jobs"] = jobs.Count;
            }

            string dictionaryTarget = Path.Combine(directory, DictionariesFolder);
            if (Directory.Exists(dictionaryTarget))
            {
                foreach (string stale in Directory.GetFiles(dictionaryTarget, "*" + DataGeneratorDictionaryStore.FileExtension)) File.Delete(stale);
            }
            List<string> dictionaries = string.IsNullOrWhiteSpace(sources.DictionaryDirectory)
                ? new List<string>()
                : DataGeneratorDictionaryStore.List(sources.DictionaryDirectory).Where(name => !DataGeneratorDictionaryStore.IsBuiltIn(name)).ToList();
            if (dictionaries.Count > 0) Directory.CreateDirectory(dictionaryTarget);
            foreach (string name in dictionaries)
            {
                DataGeneratorDictionary dictionary = DataGeneratorDictionaryStore.Load(sources.DictionaryDirectory, name);
                if (dictionary == null) continue;
                File.WriteAllText(Path.Combine(dictionaryTarget, name + DataGeneratorDictionaryStore.FileExtension), DataGeneratorDictionaryStore.Serialize(dictionary), new UTF8Encoding(false));
            }
            counts["dictionaries"] = dictionaries.Count;

            JObject manifest = new JObject
            {
                ["format"] = Format,
                ["version"] = Version,
                ["includesUserNames"] = options.IncludeUserNames,
                ["includesWebhooks"] = options.IncludeWebhooks,
                ["contents"] = JObject.FromObject(counts)
            };
            WriteJson(Path.Combine(directory, ManifestFile), manifest);
            string gitattributes = Path.Combine(directory, ".gitattributes");
            if (!File.Exists(gitattributes)) File.WriteAllText(gitattributes, "*.json text eol=lf\n*.txt text eol=lf\n", new UTF8Encoding(false));
            return counts;
        }

        /// <summary>移除密碼、權杖、本機憑證參照與連線狀態；帳號可選擇保留（沿用設定檔的混淆格式）。</summary>
        public static JObject SanitizeConnections(JToken root, bool includeUserNames)
        {
            JArray source = root is JArray ? (JArray)root : (root["connections"] as JArray ?? new JArray());
            JArray groups = root is JObject && root["groups"] is JArray ? (JArray)root["groups"] : new JArray();
            List<JObject> cleaned = new List<JObject>();
            foreach (JObject connection in source.OfType<JObject>())
            {
                JObject copy = new JObject();
                foreach (JProperty property in connection.Properties().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    string name = property.Name.ToLowerInvariant();
                    if (name == "isconnect" || name == "pdo") continue;
                    if (name == "username" && !includeUserNames) continue;
                    if (SecretKeyFragments.Any(fragment => name.Contains(fragment))) continue;
                    copy[property.Name] = property.Value.DeepClone();
                }
                cleaned.Add(copy);
            }
            return new JObject
            {
                ["connections"] = new JArray(cleaned
                    .OrderBy(item => (string)item["conn_group"] ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => (string)item["conn_name"] ?? string.Empty, StringComparer.OrdinalIgnoreCase)),
                ["groups"] = new JArray(groups.Select(item => item.ToString()).Where(item => item.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            };
        }

        // ------------------------------------------------------------ Import

        public static WorkspaceImportPlan Plan(string directory, WorkspaceSources sources)
        {
            JObject manifest = ReadObject(Path.Combine(directory, ManifestFile));
            if (manifest == null || (string)manifest["format"] != Format) throw new InvalidOperationException(Localization.T("Workspace.Error.NotWorkspace"));
            if ((int?)manifest["version"] != Version) throw new InvalidOperationException(Localization.Format("Workspace.Error.Version", (string)manifest["version"]));

            WorkspaceImportPlan plan = new WorkspaceImportPlan { Directory = directory };
            string connectionsPath = Path.Combine(directory, ConnectionsFile);
            if (File.Exists(connectionsPath)) plan.ConnectionsPath = connectionsPath;

            PlanSnippets(plan, directory, sources);
            PlanAiActions(plan, directory, sources);
            PlanJobs(plan, directory, sources);
            PlanDictionaries(plan, directory, sources);
            return plan;
        }

        /// <summary>套用選擇的項目；回傳套用數量。每個項目獨立處理，失敗的項目寫進 errors。</summary>
        public static int Apply(IEnumerable<WorkspaceImportItem> items, List<string> errors)
        {
            int applied = 0;
            foreach (WorkspaceImportItem item in items.Where(item => item.State != WorkspaceItemState.Same && item.Apply != null))
            {
                try
                {
                    item.Apply();
                    applied++;
                }
                catch (Exception exception) when (exception is InvalidOperationException || exception is IOException || exception is JsonException || exception is UnauthorizedAccessException)
                {
                    if (errors != null) errors.Add(item.Category + " " + item.Name + ": " + exception.Message);
                }
            }
            return applied;
        }

        private static void PlanSnippets(WorkspaceImportPlan plan, string directory, WorkspaceSources sources)
        {
            JArray incoming = ReadArray(Path.Combine(directory, SnippetsFile));
            if (incoming == null || string.IsNullOrWhiteSpace(sources.SnippetsPath)) return;
            SqlSnippetService service = new SqlSnippetService(sources.SnippetsPath);
            List<SqlCodeSnippet> current = service.LoadCustom();
            foreach (JObject value in incoming.OfType<JObject>())
            {
                SqlCodeSnippet snippet = value.ToObject<SqlCodeSnippet>();
                if (snippet == null || string.IsNullOrWhiteSpace(snippet.Shortcut)) continue;
                SqlCodeSnippet existing = current.FirstOrDefault(item => string.Equals(item.Shortcut, snippet.Shortcut, StringComparison.OrdinalIgnoreCase));
                WorkspaceItemState state = existing == null ? WorkspaceItemState.New
                    : existing.Name == snippet.Name && existing.Sql == snippet.Sql && (existing.Description ?? string.Empty) == (snippet.Description ?? string.Empty) ? WorkspaceItemState.Same
                    : WorkspaceItemState.Changed;
                SqlCodeSnippet captured = snippet;
                plan.Items.Add(new WorkspaceImportItem
                {
                    Category = Localization.T("Workspace.Category.Snippet"),
                    Name = snippet.Shortcut + " · " + snippet.Name,
                    State = state,
                    Apply = () =>
                    {
                        if (existing != null) captured.Id = existing.Id;
                        string temporary = Path.Combine(Path.GetTempPath(), "mysqlpunk-snippet-" + Guid.NewGuid().ToString("N") + ".json");
                        try
                        {
                            File.WriteAllText(temporary, JsonConvert.SerializeObject(new[] { captured }), new UTF8Encoding(false));
                            service.Import(temporary);
                        }
                        finally
                        {
                            if (File.Exists(temporary)) File.Delete(temporary);
                        }
                    }
                });
            }
        }

        private static void PlanAiActions(WorkspaceImportPlan plan, string directory, WorkspaceSources sources)
        {
            JArray incoming = ReadArray(Path.Combine(directory, AiActionsFile));
            if (incoming == null || string.IsNullOrWhiteSpace(sources.AiActionsPath)) return;
            QueryAiActionService service = new QueryAiActionService(sources.AiActionsPath);
            List<QueryAiCustomAction> current = service.Load();
            foreach (JObject value in incoming.OfType<JObject>())
            {
                QueryAiCustomAction action = value.ToObject<QueryAiCustomAction>();
                if (action == null || string.IsNullOrWhiteSpace(action.Name)) continue;
                QueryAiCustomAction existing = current.FirstOrDefault(item => string.Equals(item.Name, action.Name, StringComparison.OrdinalIgnoreCase));
                WorkspaceItemState state = existing == null ? WorkspaceItemState.New
                    : JToken.DeepEquals(Normalize(JObject.FromObject(existing), "Id"), Normalize(value, "Id")) ? WorkspaceItemState.Same
                    : WorkspaceItemState.Changed;
                QueryAiCustomAction captured = action;
                plan.Items.Add(new WorkspaceImportItem
                {
                    Category = Localization.T("Workspace.Category.AiAction"),
                    Name = action.Name,
                    State = state,
                    Apply = () =>
                    {
                        captured.Id = existing != null ? existing.Id : captured.Id;
                        service.Save(captured);
                    }
                });
            }
        }

        private static void PlanJobs(WorkspaceImportPlan plan, string directory, WorkspaceSources sources)
        {
            string jobsDirectory = Path.Combine(directory, JobsFolder);
            if (!Directory.Exists(jobsDirectory) || sources.JobStore == null) return;
            List<ScheduledJobDefinition> current = sources.JobStore.LoadJobs().Jobs;
            foreach (string file in Directory.GetFiles(jobsDirectory, "*.json").OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                JObject value = ReadObject(file);
                ScheduledJobDefinition job;
                try
                {
                    job = value == null ? null : value.ToObject<ScheduledJobDefinition>();
                }
                catch (JsonException exception)
                {
                    plan.Warnings.Add(Path.GetFileName(file) + ": " + exception.Message);
                    continue;
                }
                if (job == null || string.IsNullOrWhiteSpace(job.Name)) continue;
                ScheduledJobDefinition existing = current.FirstOrDefault(item => string.Equals(item.Name, job.Name, StringComparison.OrdinalIgnoreCase));
                WorkspaceItemState state = WorkspaceItemState.New;
                if (existing != null)
                {
                    JObject local = JObject.FromObject(existing);
                    if (value["WebhookUrl"] == null) local.Remove("WebhookUrl");
                    state = JToken.DeepEquals(Normalize(local, "Id", "CreatedUtc", "UpdatedUtc"), Normalize(value, "Id", "CreatedUtc", "UpdatedUtc")) ? WorkspaceItemState.Same : WorkspaceItemState.Changed;
                }
                ScheduledJobDefinition captured = job;
                plan.Items.Add(new WorkspaceImportItem
                {
                    Category = Localization.T("Workspace.Category.Job"),
                    Name = job.Name,
                    State = state,
                    Apply = () =>
                    {
                        if (existing != null)
                        {
                            captured.Id = existing.Id;
                            // 工作區沒有 Webhook 時保留本機設定的 Webhook（可能含權杖）。
                            if (value["WebhookUrl"] == null) captured.WebhookUrl = existing.WebhookUrl;
                        }
                        else if (string.IsNullOrWhiteSpace(captured.Id) || current.Any(item => string.Equals(item.Id, captured.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            captured.Id = Guid.NewGuid().ToString("N");
                        }
                        sources.JobStore.SaveJob(captured);
                    }
                });
            }
        }

        private static void PlanDictionaries(WorkspaceImportPlan plan, string directory, WorkspaceSources sources)
        {
            string folder = Path.Combine(directory, DictionariesFolder);
            if (!Directory.Exists(folder) || string.IsNullOrWhiteSpace(sources.DictionaryDirectory)) return;
            foreach (string file in Directory.GetFiles(folder, "*" + DataGeneratorDictionaryStore.FileExtension).OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!DataGeneratorDictionaryStore.IsValidName(name) || DataGeneratorDictionaryStore.IsBuiltIn(name))
                {
                    plan.Warnings.Add(Localization.Format("Workspace.Warning.DictionaryName", name));
                    continue;
                }
                string text = File.ReadAllText(file, Encoding.UTF8);
                DataGeneratorDictionary existing = DataGeneratorDictionaryStore.Load(sources.DictionaryDirectory, name);
                WorkspaceItemState state = existing == null ? WorkspaceItemState.New
                    : DataGeneratorDictionaryStore.Serialize(existing) == DataGeneratorDictionaryStore.Serialize(DataGeneratorDictionaryStore.Parse(name, text)) ? WorkspaceItemState.Same
                    : WorkspaceItemState.Changed;
                plan.Items.Add(new WorkspaceImportItem
                {
                    Category = Localization.T("Workspace.Category.Dictionary"),
                    Name = name,
                    State = state,
                    Apply = () => DataGeneratorDictionaryStore.Save(sources.DictionaryDirectory, name, text)
                });
            }
        }

        // ------------------------------------------------------------ Helpers

        private static JObject Normalize(JObject value, params string[] ignored)
        {
            JObject copy = new JObject();
            foreach (JProperty property in value.Properties().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                if (ignored.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
                copy[property.Name] = property.Value.DeepClone();
            }
            return copy;
        }

        private static void WriteJson(string path, JToken value)
        {
            string text = Canonical(value).ToString(Formatting.Indented).Replace("\r\n", "\n") + "\n";
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        /// <summary>物件鍵依字母排序，讓每次匯出的內容一致、方便 Git 比對。</summary>
        public static JToken Canonical(JToken token)
        {
            JObject obj = token as JObject;
            if (obj != null)
            {
                JObject sorted = new JObject();
                foreach (JProperty property in obj.Properties().OrderBy(item => item.Name, StringComparer.Ordinal)) sorted[property.Name] = Canonical(property.Value);
                return sorted;
            }
            JArray array = token as JArray;
            if (array != null) return new JArray(array.Select(Canonical));
            return token.DeepClone();
        }

        private static JArray ReadArray(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            return JToken.Parse(File.ReadAllText(path, Encoding.UTF8)) as JArray;
        }

        private static JObject ReadObject(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                return JToken.Parse(File.ReadAllText(path, Encoding.UTF8)) as JObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Slug(string value)
        {
            string slug = new string((value ?? string.Empty).Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray()).Trim('-');
            if (slug.Length > 60) slug = slug.Substring(0, 60);
            return slug.Length == 0 ? "job" : slug;
        }

        private static string UniqueFileName(string slug, HashSet<string> used)
        {
            string candidate = slug;
            int suffix = 2;
            while (!used.Add(candidate)) candidate = slug + "-" + suffix++;
            return candidate;
        }
    }
}
