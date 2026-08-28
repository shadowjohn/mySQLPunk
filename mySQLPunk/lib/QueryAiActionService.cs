using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace mySQLPunk.lib
{
    public sealed class QueryAiCustomAction
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Instruction { get; set; }
        public bool Pinned { get; set; }

        public QueryAiCustomAction Clone()
        {
            return new QueryAiCustomAction
            {
                Id = Id,
                Name = Name,
                Instruction = Instruction,
                Pinned = Pinned
            };
        }
    }

    /// <summary>
    /// 保存使用者自訂的查詢 AI 動作。檔案只包含名稱、提示內容與釘選狀態，
    /// 不保存編輯器 SQL、連線資訊或 AI 認證。
    /// </summary>
    public sealed class QueryAiActionService
    {
        public const int MaxActions = 50;
        public const int MaxNameLength = 80;
        public const int MaxInstructionLength = 4000;

        private readonly string _path;

        public QueryAiActionService(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("AI action path is required.", "path");
            _path = path;
        }

        public List<QueryAiCustomAction> Load()
        {
            try
            {
                if (!File.Exists(_path)) return new List<QueryAiCustomAction>();
                List<QueryAiCustomAction> actions = JsonConvert.DeserializeObject<List<QueryAiCustomAction>>(
                    File.ReadAllText(_path, Encoding.UTF8));
                return Normalize(actions, false);
            }
            catch
            {
                return new List<QueryAiCustomAction>();
            }
        }

        public List<QueryAiCustomAction> GetPinned()
        {
            return Load().Where(action => action.Pinned).Select(action => action.Clone()).ToList();
        }

        public QueryAiCustomAction Save(QueryAiCustomAction action)
        {
            QueryAiCustomAction normalized = NormalizeOne(action, true);
            List<QueryAiCustomAction> actions = Load();
            int existingIndex = actions.FindIndex(item =>
                string.Equals(item.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
            if (actions.Any(item => !string.Equals(item.Id, normalized.Id, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(item.Name, normalized.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(Localization.Format("AiAction.DuplicateName", normalized.Name));
            if (existingIndex < 0 && actions.Count >= MaxActions)
                throw new InvalidOperationException(Localization.Format("AiAction.TooMany", MaxActions));

            if (existingIndex >= 0) actions[existingIndex] = normalized;
            else actions.Add(normalized);
            WriteJson(actions);
            return normalized.Clone();
        }

        public void Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            List<QueryAiCustomAction> actions = Load();
            if (actions.RemoveAll(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) > 0)
                WriteJson(actions);
        }

        private static List<QueryAiCustomAction> Normalize(IEnumerable<QueryAiCustomAction> actions, bool rejectInvalid)
        {
            List<QueryAiCustomAction> output = new List<QueryAiCustomAction>();
            foreach (QueryAiCustomAction action in (actions ?? Enumerable.Empty<QueryAiCustomAction>()).Take(MaxActions))
            {
                try
                {
                    QueryAiCustomAction normalized = NormalizeOne(action, false);
                    if (output.Any(item => string.Equals(item.Id, normalized.Id, StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(item.Name, normalized.Name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    output.Add(normalized);
                }
                catch
                {
                    if (rejectInvalid) throw;
                }
            }
            return output;
        }

        private static QueryAiCustomAction NormalizeOne(QueryAiCustomAction action, bool createId)
        {
            if (action == null) throw new InvalidOperationException(Localization.T("AiAction.Invalid"));
            string name = (action.Name ?? string.Empty).Trim();
            string instruction = (action.Instruction ?? string.Empty).Trim();
            string id = (action.Id ?? string.Empty).Trim();
            if (name.Length == 0 || name.Length > MaxNameLength ||
                instruction.Length == 0 || instruction.Length > MaxInstructionLength ||
                id.Length > 100 || !createId && id.Length == 0)
                throw new InvalidOperationException(Localization.T("AiAction.Invalid"));

            return new QueryAiCustomAction
            {
                Id = createId && id.Length == 0 ? Guid.NewGuid().ToString("N") : id,
                Name = name,
                Instruction = instruction,
                Pinned = action.Pinned
            };
        }

        private void WriteJson(IEnumerable<QueryAiCustomAction> actions)
        {
            List<QueryAiCustomAction> normalized = Normalize(actions, true);
            string directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            string temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(
                temp,
                JsonConvert.SerializeObject(normalized, Formatting.Indented),
                new UTF8Encoding(false));
            try
            {
                if (File.Exists(_path)) File.Replace(temp, _path, null, true);
                else File.Move(temp, _path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }
}
