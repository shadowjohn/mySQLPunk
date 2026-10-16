using System;
using System.IO;

namespace mySQLPunk.lib
{
    internal static class SqlExportPublication
    {
        // The candidate must be closed and verified before entering this non-cancellable commit.
        // Never emulate replacement with Delete+Move: both old and candidate survive failures.
        internal static void Publish(string stagingPath, string targetPath)
        {
            string source = Path.GetFullPath(stagingPath);
            string target = Path.GetFullPath(targetPath);
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The staging file must differ from the destination.");
            if (!string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Atomic publication requires staging beside the destination.");
            if (File.Exists(target)) File.Replace(source, target, null);
            else File.Move(source, target);
        }
    }
}
