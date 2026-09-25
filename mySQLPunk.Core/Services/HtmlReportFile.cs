using System.Text;

namespace MySqlPunk.Core.Services;

/// <summary>Writes an HTML report next to its target through a same-directory temp file and an atomic replace.</summary>
public static class HtmlReportFile
{
    public static async Task<(long Bytes, string Path)> WriteAsync(
        string html,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("請選擇匯出檔案。", nameof(path));
        }

        var targetPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("匯出目錄不存在。");
        }

        if (Directory.Exists(targetPath))
        {
            throw new InvalidOperationException("匯出路徑指向目錄，不是檔案。");
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await stream.WriteAsync(new UTF8Encoding(false).GetBytes(html), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, targetPath, overwrite: true);
            return (new FileInfo(targetPath).Length, targetPath);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }
    }
}
