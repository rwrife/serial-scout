using System.Text;

namespace SerialScout.Core.Privacy;

internal static class AtomicFileWriter
{
    public static async Task WriteNewTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            throw new IOException("The destination already exists. Choose a new file name; Serial Scout never overwrites exports or backups.");
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: false);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // The completed destination remains valid; an orphaned staging file is harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup after a failed write/rename.
            }
        }
    }
}
