// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO;
using System.Text;
using System.Threading.Channels;

namespace Lintel.Core.Services;

public interface IAppLog
{
    void Info(string message);
    void Warn(string message, Exception? error = null);
    void Error(string message, Exception? error = null);
}

/// <summary>Async append to &lt;dir&gt;\app.log; rolls to app.1.log at 1 MB. Never log tokens, e-mail, clipboard or log-line contents.</summary>
public sealed class FileLog : IAppLog, IDisposable
{
    public const long MaxBytes = 1024 * 1024;

    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly Channel<string> queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly string file;
    private readonly string rolled;
    private readonly Task writer;

    public FileLog(string directory)
    {
        Directory.CreateDirectory(directory);
        file = Path.Combine(directory, "app.log");
        rolled = Path.Combine(directory, "app.1.log");
        writer = Task.Run(WriteLoopAsync);
    }

    public void Info(string message) => Enqueue("INFO ", message, null);

    public void Warn(string message, Exception? error = null) => Enqueue("WARN ", message, error);

    public void Error(string message, Exception? error = null) => Enqueue("ERROR", message, error);

    public void Dispose()
    {
        queue.Writer.TryComplete();
        writer.Wait(TimeSpan.FromSeconds(2));
    }

    private void Enqueue(string level, string message, Exception? error)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}";
        if (error is not null)
            line += Environment.NewLine + "    " + error.ToString().ReplaceLineEndings(Environment.NewLine + "    ");
        queue.Writer.TryWrite(line);
    }

    private async Task WriteLoopAsync()
    {
        while (await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            try
            {
                using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                using (var output = new StreamWriter(stream, Utf8))
                {
                    while (queue.Reader.TryRead(out var line))
                        output.WriteLine(line);
                }
                if (new FileInfo(file).Length > MaxBytes)
                    File.Move(file, rolled, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down; the batch is dropped.
            }
        }
    }
}
