using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace mySQLPunk.lib.MssqlImport
{
    internal sealed class MssqlImportScriptInspector
    {
        private const string Header = "Source read mode:";
        private const string Hint = "Restore into an empty, compatible target selected by the caller; existing objects fail.";
        private const string Footer = "mysqlpunk-export-complete";
        private readonly string databaseName;
        private bool candidate, header, hint, footer, schema, data, lob, usePending;
        private string mode, createKind;
        private Guid? job, lobJob;
        private long? tables, rows;
        private MssqlImportBatch current;
        private int currentIndex;
        private bool complete;
        internal MssqlImportScriptInspector(string databaseName = null) { this.databaseName = databaseName; }

        internal void Observe(MssqlImportBatch batch)
        {
            if (complete) throw new InvalidOperationException("Inspector is complete.");
            current = batch ?? throw new ArgumentNullException(nameof(batch));
            string text = batch.Text;
            bool lineHasCode = false;
            int i = 0;
            while (i < text.Length)
            {
                currentIndex = i;
                char c = text[i];
                if (c == '\r' || c == '\n') { lineHasCode = false; i++; continue; }
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
                {
                    int start = i + 2, end = start;
                    while (end < text.Length && text[end] != '\r' && text[end] != '\n') end++;
                    // Only actual line comments can carry native markers. Capture at most
                    // 512 characters, never the whole comment or all batch metadata.
                    if (!lineHasCode) Comment(text, start, end);
                    i = end; continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int depth = 1; i += 2;
                    while (i < text.Length && depth != 0)
                    {
                        if (text[i] == '\r' || text[i] == '\n') lineHasCode = false;
                        if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*') { depth++; i += 2; }
                        else if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '/') { depth--; i += 2; }
                        else i++;
                    }
                    continue;
                }
                if ((!lineHasCode && (c == ':' || (c == '!' && i + 1 < text.Length && text[i + 1] == '!'))) || (c == '$' && i + 1 < text.Length && text[i + 1] == '(')) throw Error("SqlCmdUnsupported");
                if (footer) throw Error("NativeFormat"); // Footer is an EOF marker, not an in-band hint.
                bool firstCode = !lineHasCode;
                lineHasCode = true;
                if (c == '\'')
                {
                    if (usePending) throw Error("DatabaseChange");
                    SkipQuoted(text, ref i, '\'', null);
                    continue;
                }
                if (c == '[' || c == '"')
                {
                    var token = new StringBuilder();
                    SkipQuoted(text, ref i, c == '[' ? ']' : '"', token);
                    Token(token.ToString(), true);
                    continue;
                }
                if (IsWord(c))
                {
                    int start = i;
                    while (i < text.Length && IsWord(text[i])) i++;
                    int length = i - start;
                    if (firstCode && length == 2 && string.Compare(text, start, "GO", 0, 2, StringComparison.OrdinalIgnoreCase) == 0) throw Error("UnsupportedGo");
                    // Long identifiers are irrelevant unless they claim our reserved native
                    // namespace or are the pending USE target; do not allocate the token.
                    if (length > 512)
                    {
                        if (usePending) throw Error("DatabaseChange");
                        if (text[start] == '#' && length >= 4 && string.Compare(text, start, "#mp_", 0, 4, StringComparison.OrdinalIgnoreCase) == 0) throw Error("NativeFormat");
                        createKind = null;
                    }
                    else Token(text.Substring(start, length), false);
                    continue;
                }
                if (usePending) throw Error("DatabaseChange");
                createKind = null;
                i++;
            }
            if (usePending) throw Error("DatabaseChange");
            // Keep only immutable source coordinates, not the previous SQL payload.
            current = new MssqlImportBatch(batch.Number, batch.StartByte, batch.EndByte, batch.StartLine, batch.EndLine, string.Empty);
            currentIndex = 0;
        }

        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '@' || c == '$';
        private void SkipQuoted(string text, ref int i, char close, StringBuilder value)
        {
            i++;
            while (i < text.Length)
            {
                char c = text[i++];
                if (c == close)
                {
                    if (i < text.Length && text[i] == close) i++;
                    else return;
                }
                if (value != null)
                {
                    if (value.Length == 512)
                    {
                        if (usePending) throw Error("DatabaseChange");
                        value = null;
                    }
                    else value.Append(c);
                }
            }
        }
        private void Token(string token, bool quoted)
        {
            if (usePending)
            {
                usePending = false;
                if (databaseName == null || !string.Equals(token, databaseName, StringComparison.Ordinal)) throw Error("DatabaseChange");
                createKind = null;
                return;
            }
            if (!quoted && token.Equals("USE", StringComparison.OrdinalIgnoreCase)) { usePending = true; return; }
            if (!quoted && token.StartsWith("#mp_", StringComparison.OrdinalIgnoreCase))
            {
                candidate = true; lob = true;
                Guid parsed;
                if (token.Length != 38 || token[36] != '_' || (token[37] != 's' && token[37] != 'v' && token[37] != 'c') || !Guid.TryParseExact(token.Substring(4, 32), "N", out parsed) || parsed == Guid.Empty) throw Error("NativeFormat");
                if (lobJob.HasValue && lobJob != parsed) throw Error("NativeFormat");
                lobJob = parsed;
                createKind = null;
                return;
            }
            if (createKind == "object")
            {
                if (!token.StartsWith("#", StringComparison.Ordinal)) schema = true;
                createKind = null;
            }
            else if (createKind == "create" && !quoted && (token.Equals("TABLE", StringComparison.OrdinalIgnoreCase) || token.Equals("VIEW", StringComparison.OrdinalIgnoreCase) || token.Equals("PROCEDURE", StringComparison.OrdinalIgnoreCase) || token.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase))) createKind = "object";
            else createKind = !quoted && token.Equals("CREATE", StringComparison.OrdinalIgnoreCase) ? "create" : null;
            if (!quoted && (token.Equals("INSERT", StringComparison.OrdinalIgnoreCase) || token.Equals("UPDATE", StringComparison.OrdinalIgnoreCase) || token.Equals("DELETE", StringComparison.OrdinalIgnoreCase))) data = true;
        }
        private void Comment(string text, int start, int end)
        {
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            int count = Math.Min(end - start, 512);
            string comment = text.Substring(start, count).TrimEnd();
            bool isHeader = comment.StartsWith("Source read mode", StringComparison.OrdinalIgnoreCase);
            bool isHint = comment.StartsWith("Restore into an empty, compatible target", StringComparison.OrdinalIgnoreCase);
            bool isFooter = comment.StartsWith(Footer, StringComparison.OrdinalIgnoreCase);
            if (!isHeader && !isHint && !isFooter) return;
            candidate = true;
            if (end - start > 512 || footer) throw Error("NativeFormat");
            if (isHeader)
            {
                if (header || schema || data || lob || !comment.StartsWith(Header, StringComparison.Ordinal)) throw Error("NativeFormat");
                header = true; mode = comment.Substring(Header.Length).Trim();
                if (mode != "READ COMMITTED" && mode != "SNAPSHOT" && mode != "Schema only (no data transaction)") throw Error("NativeFormat");
            }
            else if (isHint)
            {
                if (hint || !header || schema || data || lob || comment != Hint) throw Error("NativeFormat");
                hint = true;
            }
            else
            {
                var match = Regex.Match(comment, @"\Amysqlpunk-export-complete job=([0-9a-fA-F]{32}) tables=([0-9]+) rows=([0-9]+)\z", RegexOptions.CultureInvariant);
                Guid parsed; long tableCount, rowCount;
                if (!match.Success || !Guid.TryParseExact(match.Groups[1].Value, "N", out parsed) || parsed == Guid.Empty || !long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out tableCount) || !long.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out rowCount)) throw Error("NativeFormat");
                footer = true; job = parsed; tables = tableCount; rows = rowCount;
            }
        }
        internal MssqlImportScriptInfo Complete()
        {
            if (complete) throw new InvalidOperationException("Inspector is complete.");
            complete = true;
            if (!candidate) return new MssqlImportScriptInfo(MssqlImportScriptKind.Generic, null, null, null, null, false);
            if (!header || !footer || (hint && !schema && (tables != 0 || rows != 0)) || (schema && !hint) || (lobJob.HasValue && lobJob != job) || (mode == "Schema only (no data transaction)" && (!hint || data || lob || rows != 0))) throw Error("NativeFormat");
            return new MssqlImportScriptInfo(hint ? MssqlImportScriptKind.NativeSchema : MssqlImportScriptKind.NativeData, job, tables, rows, mode, lob);
        }
        private MssqlImportInputException Error(string code)
        {
            if (current == null) return new MssqlImportInputException(code, 1, 1, 0);
            long errorLine = current.StartLine;
            for (int i = 0; i < currentIndex; i++) if (current.Text[i] == '\n' || (current.Text[i] == '\r' && (i + 1 == current.Text.Length || current.Text[i + 1] != '\n'))) errorLine++;
            // Reader supplies exact byte encoding for variable-width source diagnostics.
            string name = current.SourceEncodingName;
            long prefixBytes = 0;
            for (int i = 0; i < currentIndex; i++)
            {
                char c = current.Text[i];
                prefixBytes += name.StartsWith("UTF-16", StringComparison.Ordinal) ? 2 : char.IsHighSurrogate(c) ? 0 : char.IsLowSurrogate(c) ? 4 : c < 128 ? 1 : c < 2048 ? 2 : 3;
            }
            long bom = current.StartByte == 0 ? (name == "UTF-8 BOM" ? 3 : name.StartsWith("UTF-16", StringComparison.Ordinal) ? 2 : 0) : 0;
            long offset = current.StartByte + bom + prefixBytes;
            return new MssqlImportInputException(code, current.Number, errorLine, offset);
        }
    }
}
