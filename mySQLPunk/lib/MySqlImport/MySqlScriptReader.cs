using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace mySQLPunk.lib.MySqlImport
{
    // A shared reader for preflight and execution. Delimiters/comments are syntax
    // only outside quoted text; original newline and literal characters survive.
    public static class MySqlScriptReader
    {
        public const int MaxStatementBytes = 32 * 1024 * 1024;
        public sealed class Statement
        {
            public string Sql { get; internal set; }
            public int Line { get; internal set; }
        }

        public sealed class Token
        {
            public string Value { get; internal set; }
            public int Start { get; internal set; }
            public int Length { get; internal set; }
            public bool Identifier { get; internal set; }
            public bool Quoted { get; internal set; }
        }

        public static IEnumerable<Statement> Read(TextReader reader, CancellationToken cancellation, Action<string> commentLine = null)
        {
            var batch = new StringBuilder();
            string delimiter = ";";
            char quote = '\0';
            bool block = false, escaped = false;
            int lineNumber = 0, startLine = 1;
            string line;
            while ((line = ReadLineWithEnding(reader)) != null)
            {
                cancellation.ThrowIfCancellationRequested();
                lineNumber++;
                string trimmed = line.Trim();
                if (quote == '\0' && !block)
                {
                    if (trimmed.StartsWith("--", StringComparison.Ordinal) && commentLine != null) commentLine(trimmed);
                    var command = Regex.Match(trimmed, @"^DELIMITER\s+(\S+)\s*$", RegexOptions.IgnoreCase);
                    if (command.Success && Tokens(batch.ToString()).Count == 0)
                    {
                        delimiter = command.Groups[1].Value;
                        if (delimiter.Length > 16 || delimiter.IndexOfAny(new[] { '\'', '"', '`', '\\' }) >= 0)
                            throw new InvalidDataException("Unsupported DELIMITER at line " + lineNumber);
                        batch.Clear();
                        startLine = lineNumber + 1;
                        continue;
                    }
                }
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    if (quote != '\0')
                    {
                        batch.Append(c);
                        if (escaped) { escaped = false; continue; }
                        if (c == '\\' && quote != '`') { escaped = true; continue; }
                        if (c == quote)
                        {
                            if (i + 1 < line.Length && line[i + 1] == quote) { batch.Append(line[++i]); continue; }
                            quote = '\0';
                        }
                        continue;
                    }
                    if (block)
                    {
                        batch.Append(c);
                        if (c == '*' && i + 1 < line.Length && line[i + 1] == '/') { batch.Append(line[++i]); block = false; }
                        continue;
                    }
                    if (c == '#' || (c == '-' && i + 2 < line.Length && line[i + 1] == '-' && char.IsWhiteSpace(line[i + 2])))
                    {
                        batch.Append(line, i, line.Length - i);
                        break;
                    }
                    if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                    {
                        if (i + 2 < line.Length && (line[i + 2] == '!' || (line[i + 2] == 'M' && i + 3 < line.Length && line[i + 3] == '!')))
                            throw new InvalidDataException("Executable version comments require a native MySQL client; line " + lineNumber);
                        block = true;
                        batch.Append(c).Append(line[++i]);
                        continue;
                    }
                    if (c == '\'' || c == '"' || c == '`') { quote = c; batch.Append(c); continue; }
                    if (i + delimiter.Length <= line.Length && string.CompareOrdinal(line, i, delimiter, 0, delimiter.Length) == 0)
                    {
                        string sql = batch.ToString().Trim();
                        CheckSize(sql);
                        if (Tokens(sql).Count > 0) yield return new Statement { Sql = sql, Line = startLine };
                        batch.Clear();
                        startLine = lineNumber;
                        i += delimiter.Length - 1;
                    }
                    else batch.Append(c);
                    if (batch.Length > MaxStatementBytes) throw new InvalidDataException("Single SQL statement exceeds 32 MiB safe limit.");
                }
                if (batch.Length > MaxStatementBytes) throw new InvalidDataException("Single SQL statement exceeds 32 MiB safe limit.");
            }
            cancellation.ThrowIfCancellationRequested();
            if (quote != '\0' || block) throw new InvalidDataException("Unterminated SQL literal, identifier, or block comment.");
            string remaining = batch.ToString().Trim();
            CheckSize(remaining);
            if (Tokens(remaining).Count > 0) yield return new Statement { Sql = remaining, Line = startLine };
        }

        private static string ReadLineWithEnding(TextReader reader)
        {
            var line = new StringBuilder();
            int c;
            while ((c = reader.Read()) >= 0)
            {
                line.Append((char)c);
                if (line.Length > MaxStatementBytes) throw new InvalidDataException("Single SQL line exceeds 32 MiB safe limit.");
                if (c == '\n') break;
                if (c == '\r') { if (reader.Peek() == '\n') line.Append((char)reader.Read()); break; }
            }
            return line.Length == 0 ? null : line.ToString();
        }

        private static void CheckSize(string sql)
        {
            if (Encoding.UTF8.GetByteCount(sql) > MaxStatementBytes) throw new InvalidDataException("Single SQL statement exceeds 32 MiB UTF-8 safe limit.");
            if (sql.IndexOf('\0') >= 0) throw new InvalidDataException("SQL contains an embedded NUL; use an escaped literal.");
        }

        public static List<Token> Tokens(string sql)
        {
            var tokens = new List<Token>();
            for (int i = 0; i < sql.Length;)
            {
                char c = sql[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '#' || (c == '-' && i + 2 < sql.Length && sql[i + 1] == '-' && char.IsWhiteSpace(sql[i + 2])))
                { while (i < sql.Length && sql[i] != '\r' && sql[i] != '\n') i++; continue; }
                if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                { int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? sql.Length : end + 2; continue; }
                int start = i++;
                if (c == '\'' || c == '"' || c == '`')
                {
                    while (i < sql.Length)
                    {
                        if (sql[i] == '\\' && c != '`') { i = Math.Min(sql.Length, i + 2); continue; }
                        if (sql[i++] == c) { if (i < sql.Length && sql[i] == c) { i++; continue; } break; }
                    }
                    if (c == '`') tokens.Add(new Token { Start = start, Length = i - start, Value = MySqlImportRunner.UnquoteIdentifier(sql.Substring(start, i - start)), Identifier = true, Quoted = true });
                    else tokens.Add(new Token { Start = start, Length = i - start, Value = "<literal>" });
                }
                else
                {
                    bool identifier = char.IsLetterOrDigit(c) || c == '_' || c == '$';
                    if (identifier) while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$')) i++;
                    tokens.Add(new Token { Start = start, Length = i - start, Value = sql.Substring(start, i - start), Identifier = identifier });
                }
                if (tokens.Count > 250000) throw new InvalidDataException("SQL statement token count exceeds safe limit.");
            }
            return tokens;
        }

        public static string CodeOnly(string sql)
        {
            var code = new string(' ', sql.Length).ToCharArray();
            foreach (var token in Tokens(sql))
                if (token.Value != "<literal>") sql.CopyTo(token.Start, code, token.Start, token.Length);
            return new string(code);
        }

        public static string ReplaceToken(string sql, Token token, string replacement)
        { return sql.Substring(0, token.Start) + replacement + sql.Substring(token.Start + token.Length); }
    }
}
