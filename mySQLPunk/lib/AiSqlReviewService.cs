using System;
using System.Collections.Generic;

namespace mySQLPunk.lib
{
    public enum AiSqlDiffKind
    {
        Same,
        Added,
        Removed,
        Changed
    }

    public sealed class AiSqlDiffRow
    {
        public int? OriginalLineNumber { get; set; }
        public string OriginalText { get; set; }
        public int? SuggestedLineNumber { get; set; }
        public string SuggestedText { get; set; }
        public AiSqlDiffKind Kind { get; set; }
        public int ChangeGroup { get; set; }
    }

    public enum AiSqlApplyFailure
    {
        None,
        BlankSuggestion,
        EditorChanged,
        InvalidTarget
    }

    /// <summary>
    /// 建立 AI SQL 的逐行差異，並以送出時的編輯器快照保護確認套用流程。
    /// </summary>
    public static class AiSqlReviewService
    {
        private const int MaximumLcsLines = 500;
        private const long MaximumLcsCells = 250000;

        private sealed class LineChange
        {
            public AiSqlDiffKind Kind { get; set; }
            public int? OriginalLineNumber { get; set; }
            public string OriginalText { get; set; }
            public int? SuggestedLineNumber { get; set; }
            public string SuggestedText { get; set; }
        }

        public static List<AiSqlDiffRow> BuildDiff(string originalSql, string suggestedSql)
        {
            string[] originalLines = SplitLines(originalSql);
            string[] suggestedLines = SplitLines(suggestedSql);
            if (originalLines.Length > MaximumLcsLines
                || suggestedLines.Length > MaximumLcsLines
                || (long)originalLines.Length * suggestedLines.Length > MaximumLcsCells)
            {
                return AssignChangeGroups(BuildLineByLineDiff(originalLines, suggestedLines));
            }

            int[,] lcs = new int[originalLines.Length + 1, suggestedLines.Length + 1];
            for (int originalIndex = originalLines.Length - 1; originalIndex >= 0; originalIndex--)
            {
                for (int suggestedIndex = suggestedLines.Length - 1; suggestedIndex >= 0; suggestedIndex--)
                {
                    lcs[originalIndex, suggestedIndex] = string.Equals(
                        originalLines[originalIndex],
                        suggestedLines[suggestedIndex],
                        StringComparison.Ordinal)
                        ? lcs[originalIndex + 1, suggestedIndex + 1] + 1
                        : Math.Max(lcs[originalIndex + 1, suggestedIndex], lcs[originalIndex, suggestedIndex + 1]);
                }
            }

            List<LineChange> changes = new List<LineChange>();
            int oldLine = 0;
            int newLine = 0;
            while (oldLine < originalLines.Length && newLine < suggestedLines.Length)
            {
                if (string.Equals(originalLines[oldLine], suggestedLines[newLine], StringComparison.Ordinal))
                {
                    changes.Add(new LineChange
                    {
                        Kind = AiSqlDiffKind.Same,
                        OriginalLineNumber = oldLine + 1,
                        OriginalText = originalLines[oldLine],
                        SuggestedLineNumber = newLine + 1,
                        SuggestedText = suggestedLines[newLine]
                    });
                    oldLine++;
                    newLine++;
                }
                else if (lcs[oldLine + 1, newLine] >= lcs[oldLine, newLine + 1])
                {
                    changes.Add(new LineChange
                    {
                        Kind = AiSqlDiffKind.Removed,
                        OriginalLineNumber = oldLine + 1,
                        OriginalText = originalLines[oldLine]
                    });
                    oldLine++;
                }
                else
                {
                    changes.Add(new LineChange
                    {
                        Kind = AiSqlDiffKind.Added,
                        SuggestedLineNumber = newLine + 1,
                        SuggestedText = suggestedLines[newLine]
                    });
                    newLine++;
                }
            }

            while (oldLine < originalLines.Length)
            {
                changes.Add(new LineChange
                {
                    Kind = AiSqlDiffKind.Removed,
                    OriginalLineNumber = oldLine + 1,
                    OriginalText = originalLines[oldLine]
                });
                oldLine++;
            }
            while (newLine < suggestedLines.Length)
            {
                changes.Add(new LineChange
                {
                    Kind = AiSqlDiffKind.Added,
                    SuggestedLineNumber = newLine + 1,
                    SuggestedText = suggestedLines[newLine]
                });
                newLine++;
            }

            return AssignChangeGroups(AlignChangedLines(changes));
        }

        public static string BuildSelectedSql(
            IList<AiSqlDiffRow> rows,
            ISet<int> selectedChangeGroups,
            string originalSql)
        {
            if (rows == null || rows.Count == 0) return string.Empty;

            ISet<int> selected = selectedChangeGroups ?? new HashSet<int>();
            List<string> lines = new List<string>();
            foreach (AiSqlDiffRow row in rows)
            {
                if (row == null) continue;
                bool useSuggestion = row.ChangeGroup > 0 && selected.Contains(row.ChangeGroup);
                switch (row.Kind)
                {
                    case AiSqlDiffKind.Added:
                        if (useSuggestion) lines.Add(row.SuggestedText ?? string.Empty);
                        break;
                    case AiSqlDiffKind.Removed:
                        if (!useSuggestion) lines.Add(row.OriginalText ?? string.Empty);
                        break;
                    case AiSqlDiffKind.Changed:
                        lines.Add(useSuggestion
                            ? row.SuggestedText ?? string.Empty
                            : row.OriginalText ?? string.Empty);
                        break;
                    default:
                        lines.Add(row.OriginalText ?? string.Empty);
                        break;
                }
            }
            return string.Join(DetectNewLine(originalSql), lines);
        }

        public static bool TryApply(
            string currentEditorText,
            string expectedEditorSnapshot,
            int selectionStart,
            int selectionLength,
            string suggestedSql,
            out string updatedEditorText,
            out AiSqlApplyFailure failure)
        {
            string current = currentEditorText ?? string.Empty;
            string expected = expectedEditorSnapshot ?? string.Empty;
            string suggestion = suggestedSql ?? string.Empty;
            updatedEditorText = current;

            if (string.IsNullOrWhiteSpace(suggestion))
            {
                failure = AiSqlApplyFailure.BlankSuggestion;
                return false;
            }
            if (!string.Equals(current, expected, StringComparison.Ordinal))
            {
                failure = AiSqlApplyFailure.EditorChanged;
                return false;
            }
            if (selectionLength < 0
                || selectionStart < 0
                || selectionStart > expected.Length
                || selectionLength > expected.Length
                || selectionStart > expected.Length - selectionLength)
            {
                failure = AiSqlApplyFailure.InvalidTarget;
                return false;
            }

            updatedEditorText = selectionLength > 0
                ? expected.Substring(0, selectionStart)
                    + suggestion
                    + expected.Substring(selectionStart + selectionLength)
                : suggestion;
            failure = AiSqlApplyFailure.None;
            return true;
        }

        private static string[] SplitLines(string sql)
        {
            string normalized = (sql ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            return normalized.Split(new[] { '\n' }, StringSplitOptions.None);
        }

        private static List<AiSqlDiffRow> BuildLineByLineDiff(string[] originalLines, string[] suggestedLines)
        {
            List<AiSqlDiffRow> rows = new List<AiSqlDiffRow>();
            int count = Math.Max(originalLines.Length, suggestedLines.Length);
            for (int index = 0; index < count; index++)
            {
                bool hasOriginal = index < originalLines.Length;
                bool hasSuggestion = index < suggestedLines.Length;
                AiSqlDiffKind kind = !hasOriginal
                    ? AiSqlDiffKind.Added
                    : !hasSuggestion
                        ? AiSqlDiffKind.Removed
                        : string.Equals(originalLines[index], suggestedLines[index], StringComparison.Ordinal)
                            ? AiSqlDiffKind.Same
                            : AiSqlDiffKind.Changed;
                rows.Add(new AiSqlDiffRow
                {
                    OriginalLineNumber = hasOriginal ? (int?)(index + 1) : null,
                    OriginalText = hasOriginal ? originalLines[index] : string.Empty,
                    SuggestedLineNumber = hasSuggestion ? (int?)(index + 1) : null,
                    SuggestedText = hasSuggestion ? suggestedLines[index] : string.Empty,
                    Kind = kind
                });
            }
            return rows;
        }

        private static List<AiSqlDiffRow> AlignChangedLines(List<LineChange> changes)
        {
            List<AiSqlDiffRow> rows = new List<AiSqlDiffRow>();
            int index = 0;
            while (index < changes.Count)
            {
                if (changes[index].Kind == AiSqlDiffKind.Same)
                {
                    rows.Add(ToDiffRow(changes[index]));
                    index++;
                    continue;
                }

                List<LineChange> removed = new List<LineChange>();
                List<LineChange> added = new List<LineChange>();
                while (index < changes.Count && changes[index].Kind != AiSqlDiffKind.Same)
                {
                    if (changes[index].Kind == AiSqlDiffKind.Removed) removed.Add(changes[index]);
                    if (changes[index].Kind == AiSqlDiffKind.Added) added.Add(changes[index]);
                    index++;
                }

                int paired = Math.Min(removed.Count, added.Count);
                for (int pair = 0; pair < paired; pair++)
                {
                    rows.Add(new AiSqlDiffRow
                    {
                        OriginalLineNumber = removed[pair].OriginalLineNumber,
                        OriginalText = removed[pair].OriginalText,
                        SuggestedLineNumber = added[pair].SuggestedLineNumber,
                        SuggestedText = added[pair].SuggestedText,
                        Kind = AiSqlDiffKind.Changed
                    });
                }
                for (int removedIndex = paired; removedIndex < removed.Count; removedIndex++)
                    rows.Add(ToDiffRow(removed[removedIndex]));
                for (int addedIndex = paired; addedIndex < added.Count; addedIndex++)
                    rows.Add(ToDiffRow(added[addedIndex]));
            }
            return rows;
        }

        private static List<AiSqlDiffRow> AssignChangeGroups(List<AiSqlDiffRow> rows)
        {
            int group = 0;
            bool inChange = false;
            foreach (AiSqlDiffRow row in rows)
            {
                if (row.Kind == AiSqlDiffKind.Same)
                {
                    row.ChangeGroup = 0;
                    inChange = false;
                    continue;
                }

                if (!inChange)
                {
                    group++;
                    inChange = true;
                }
                row.ChangeGroup = group;
            }
            return rows;
        }

        private static string DetectNewLine(string sql)
        {
            string text = sql ?? string.Empty;
            if (text.IndexOf("\r\n", StringComparison.Ordinal) >= 0) return "\r\n";
            if (text.IndexOf('\r') >= 0) return "\r";
            return "\n";
        }

        private static AiSqlDiffRow ToDiffRow(LineChange change)
        {
            return new AiSqlDiffRow
            {
                OriginalLineNumber = change.OriginalLineNumber,
                OriginalText = change.OriginalText ?? string.Empty,
                SuggestedLineNumber = change.SuggestedLineNumber,
                SuggestedText = change.SuggestedText ?? string.Empty,
                Kind = change.Kind
            };
        }
    }
}
