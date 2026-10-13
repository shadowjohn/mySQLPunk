using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace mySQLPunk.lib.MssqlImport
{
    // A single bounded SQL buffer. The line candidate is a finite-state machine:
    // a GO comment (including an arbitrarily long one) is never buffered.
    internal sealed class MssqlGoBatchReader : IDisposable
    {
        internal const int MaxBatchCodeUnits = 16777216;
        private enum Candidate { Leading, G, Go, Space, Dash, Comment, Sql }
        private enum Context { Normal, Single, Double, Bracket, LineComment, BlockComment }
        private readonly Stream stream;
        private readonly int bufferSize;
        private readonly Action<long> progress;
        private readonly Action<MssqlImportBatch> observe;
        private Context context;
        private int blockDepth;
        private bool skipPair, hasSql, reading;
        private long number = 1, line = 1, byteOffset;
        internal string EncodingName { get; private set; }
        internal string Sha256 { get; private set; }
        internal MssqlGoBatchReader(Stream stream, int bufferSize = 8192, Action<long> progress = null, Action<MssqlImportBatch> observe = null)
        {
            this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (bufferSize < 1 || bufferSize > 1048576) throw new ArgumentOutOfRangeException(nameof(bufferSize));
            this.bufferSize = bufferSize; this.progress = progress; this.observe = observe;
        }

        internal IEnumerable<MssqlImportBatch> ReadBatches(CancellationToken cancellation)
        {
            if (reading) throw new InvalidOperationException("Reader is single use.");
            reading = true;
            var sql = new StringBuilder();
            long startByte = 0, startLine = 1, endLine = 1;
            int lineStart = 0;
            bool overflow = false;
            Candidate candidate = Candidate.Leading;
            using (var chars = Decode(cancellation).GetEnumerator())
            {
                bool more = chars.MoveNext();
                Unit current = more ? chars.Current : default(Unit);
                while (more)
                {
                    cancellation.ThrowIfCancellationRequested();
                    more = chars.MoveNext();
                    Unit next = more ? chars.Current : default(Unit);
                    char c = current.Character;
                    byteOffset = current.Start;
                    if (c == '\0') throw Error("EmbeddedNul");
                    if (c == '\r' || c == '\n')
                    {
                        string newline = c.ToString();
                        long boundary = current.End;
                        if (c == '\r' && more && next.Character == '\n')
                        {
                            newline = "\r\n"; boundary = next.End;
                            more = chars.MoveNext(); next = more ? chars.Current : default(Unit);
                        }
                        if (IsGo(candidate))
                        {
                            sql.Length = lineStart;
                            var batch = new MssqlImportBatch(number, startByte, boundary, startLine, Math.Max(startLine, line - 1), sql.ToString(), EncodingName);
                            if (sql.Length != 0) observe?.Invoke(batch);
                            if (hasSql) { number++; yield return batch; }
                            sql.Clear(); hasSql = false; startByte = boundary; startLine = line + 1;
                        }
                        else
                        {
                            if (candidate == Candidate.Dash) throw Error("UnsupportedGo");
                            if (overflow) throw Error("BatchTooLarge");
                            Append(sql, newline);
                            if (context == Context.LineComment) context = Context.Normal;
                            skipPair = false;
                            endLine = line;
                        }
                        line++; lineStart = sql.Length; overflow = false;
                        candidate = context == Context.Normal ? Candidate.Leading : Candidate.Sql;
                        current = next;
                        continue;
                    }

                    if (candidate != Candidate.Sql)
                    {
                        Candidate before = candidate;
                        candidate = Advance(candidate, c);
                        if (candidate == Candidate.Sql)
                        {
                            if (overflow) throw Error("BatchTooLarge");
                            // The deferred prefix contains only whitespace/G/O, so has no
                            // lexical side effects. G/O become SQL if this isn't a GO line.
                            if (before != Candidate.Leading) hasSql = true;
                            Append(sql, c); Lex(c, more ? next.Character : '\0');
                        }
                        else if (candidate != Candidate.Comment)
                        {
                            if (sql.Length < MaxBatchCodeUnits) sql.Append(c); else overflow = true;
                        }
                    }
                    else { Append(sql, c); Lex(c, more ? next.Character : '\0'); }
                    endLine = line;
                    current = next;
                }
            }
            if (IsGo(candidate))
            {
                sql.Length = lineStart;
                var batch = new MssqlImportBatch(number, startByte, stream.Position, startLine, Math.Max(startLine, line - 1), sql.ToString(), EncodingName);
                if (sql.Length != 0) observe?.Invoke(batch);
                if (hasSql) yield return batch;
            }
            else
            {
                if (candidate == Candidate.Dash) throw Error("UnsupportedGo");
                if (overflow) throw Error("BatchTooLarge");
                if (context != Context.Normal && context != Context.LineComment) throw Error("UnclosedContext");
                var batch = new MssqlImportBatch(number, startByte, stream.Position, startLine, endLine, sql.ToString(), EncodingName);
                if (sql.Length != 0) observe?.Invoke(batch);
                if (hasSql) yield return batch;
            }
        }

        private Candidate Advance(Candidate state, char c)
        {
            if (state == Candidate.Comment) return state;
            if (state == Candidate.Leading) return char.IsWhiteSpace(c) ? state : c == 'g' || c == 'G' ? Candidate.G : Candidate.Sql;
            if (state == Candidate.G) return c == 'o' || c == 'O' ? Candidate.Go : Candidate.Sql;
            if (state == Candidate.Go && (char.IsLetterOrDigit(c) || c == '_' || c == '#' || c == '@' || c == '$')) return Candidate.Sql;
            if (state == Candidate.Dash)
            {
                if (c == '-') return Candidate.Comment;
                throw Error("UnsupportedGo");
            }
            if (char.IsWhiteSpace(c)) return Candidate.Space;
            if (c == '-') return Candidate.Dash;
            throw Error("UnsupportedGo");
        }
        private static bool IsGo(Candidate candidate) => candidate == Candidate.Go || candidate == Candidate.Space || candidate == Candidate.Comment;
        private void Append(StringBuilder sql, char c) { if (sql.Length == MaxBatchCodeUnits) throw Error("BatchTooLarge"); sql.Append(c); }
        private void Append(StringBuilder sql, string text) { if ((long)sql.Length + text.Length > MaxBatchCodeUnits) throw Error("BatchTooLarge"); sql.Append(text); }
        private MssqlImportInputException Error(string code, Exception inner = null) => new MssqlImportInputException(code, number, line, byteOffset, inner);
        private void Lex(char c, char next)
        {
            if (skipPair) { skipPair = false; return; }
            if (context == Context.LineComment) return;
            if (context == Context.BlockComment)
            {
                if (c == '/' && next == '*') { blockDepth++; skipPair = true; }
                else if (c == '*' && next == '/') { if (--blockDepth == 0) context = Context.Normal; skipPair = true; }
                return;
            }
            if (context == Context.Single || context == Context.Double || context == Context.Bracket)
            {
                char close = context == Context.Single ? '\'' : context == Context.Double ? '"' : ']';
                if (c == close) { if (next == close) skipPair = true; else context = Context.Normal; }
                return;
            }
            if (c == '-' && next == '-') { context = Context.LineComment; skipPair = true; }
            else if (c == '/' && next == '*') { context = Context.BlockComment; blockDepth = 1; skipPair = true; }
            else if (c == '\'') { context = Context.Single; hasSql = true; }
            else if (c == '"') { context = Context.Double; hasSql = true; }
            else if (c == '[') { context = Context.Bracket; hasSql = true; }
            else if (!char.IsWhiteSpace(c)) hasSql = true;
        }

        private struct Unit
        {
            internal char Character;
            internal long Start, End;
            internal Unit(char character, long start, long end) { Character = character; Start = start; End = end; }
        }
        private IEnumerable<Unit> Decode(CancellationToken cancellation)
        {
            var prefix = new byte[4];
            int prefixCount = 0;
            while (prefixCount < prefix.Length)
            {
                cancellation.ThrowIfCancellationRequested();
                int n = stream.Read(prefix, prefixCount, Math.Min(bufferSize, prefix.Length - prefixCount));
                if (n == 0) break;
                prefixCount += n;
            }
            int bom = 0;
            Encoding encoding = new UTF8Encoding(false, true);
            if (prefixCount >= 4 && ((prefix[0] == 0xff && prefix[1] == 0xfe && prefix[2] == 0 && prefix[3] == 0) || (prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0xfe && prefix[3] == 0xff))) throw Error("InvalidEncoding");
            if (prefixCount >= 3 && prefix[0] == 0xef && prefix[1] == 0xbb && prefix[2] == 0xbf) bom = 3;
            else if (prefixCount >= 2 && prefix[0] == 0xff && prefix[1] == 0xfe) { encoding = new UnicodeEncoding(false, true, true); bom = 2; }
            else if (prefixCount >= 2 && prefix[0] == 0xfe && prefix[1] == 0xff) { encoding = new UnicodeEncoding(true, true, true); bom = 2; }
            EncodingName = encoding.CodePage == 65001 ? (bom == 0 ? "UTF-8" : "UTF-8 BOM") : encoding.CodePage == 1200 ? "UTF-16 LE BOM" : "UTF-16 BE BOM";
            Decoder decoder = encoding.GetDecoder();
            var buffer = new byte[bufferSize];
            var decoded = new char[Math.Max(4, bufferSize + 2)];
            long decodedByte = bom, chunkStart = bom;
            using (var hash = SHA256.Create())
            {
                hash.TransformBlock(prefix, 0, prefixCount, null, 0);
                byte[] source = prefix; int offset = bom, count = prefixCount - bom;
                bool eof = false;
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int usedBytes, usedChars; bool complete;
                    try { decoder.Convert(source, offset, count, decoded, 0, decoded.Length, eof, out usedBytes, out usedChars, out complete); }
                    catch (DecoderFallbackException ex) { byteOffset = Math.Max(0, chunkStart + ex.Index); throw Error("InvalidEncoding", ex); }
                    for (int i = 0; i < usedChars; i++)
                    {
                        char c = decoded[i];
                        int bytes = encoding.CodePage != 65001 ? 2 : char.IsHighSurrogate(c) ? 0 : char.IsLowSurrogate(c) ? 4 : c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
                        yield return new Unit(c, decodedByte, decodedByte + bytes); decodedByte += bytes;
                    }
                    offset += usedBytes; count -= usedBytes; chunkStart += usedBytes;
                    if (count != 0) continue;
                    if (eof)
                    {
                        hash.TransformFinalBlock(new byte[0], 0, 0);
                        Sha256 = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                        progress?.Invoke(stream.Position);
                        yield break;
                    }
                    int read = stream.Read(buffer, 0, buffer.Length);
                    chunkStart = stream.Position - read;
                    if (read > 0) { hash.TransformBlock(buffer, 0, read, null, 0); progress?.Invoke(stream.Position); }
                    source = buffer; offset = 0; count = read; eof = read == 0;
                }
            }
        }
        // The caller owns the same file handle across scan and execution.
        public void Dispose() { }
    }
}
