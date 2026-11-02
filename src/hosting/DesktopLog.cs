using System.Text;

namespace Athena.Desktop.Hosting;

public sealed class DesktopLog : IDisposable
{
    private readonly TextWriter previousOut = Console.Out;
    private readonly TextWriter previousError = Console.Error;
    private readonly string path;
    private readonly long maxBytes;
    private readonly object gate = new();
    private StreamWriter? file;

    public DesktopLog(string path, bool mirrorConsole = false, long maxBytes = 5 * 1024 * 1024)
    {
        this.path = Path.GetFullPath(path);
        this.maxBytes = maxBytes;
        Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
        file = Open();
        Console.SetOut(new LogWriter(this, "INFO", mirrorConsole ? previousOut : null));
        Console.SetError(new LogWriter(this, "ERROR", mirrorConsole ? previousError : null));
    }

    private StreamWriter Open() => new(new FileStream(path, FileMode.Append, FileAccess.Write,
        FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };

    private void Write(string level, string text, TextWriter? mirror)
    {
        lock (gate)
        {
            try
            {
                file ??= Open();
                if (file.BaseStream.Length >= maxBytes)
                {
                    file.Dispose();
                    file = null;
                    File.Delete(path + ".3");
                    for (var index = 2; index >= 1; index--)
                        if (File.Exists(path + "." + index)) File.Move(path + "." + index, path + "." + (index + 1), true);
                    File.Move(path, path + ".1", true);
                    file = Open();
                }
                file.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [{level}] {text}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // A full disk or locked archive must not stop the command listener.
                try { file?.Dispose(); }
                catch (IOException) { }
                file = null;
                try { previousError.WriteLine($"Athena log write failed: {exception.Message}"); }
                catch (IOException) { }
            }
            try { mirror?.WriteLine(text); }
            catch (IOException) { }
        }
    }

    public void Dispose()
    {
        Console.SetOut(previousOut);
        Console.SetError(previousError);
        file?.Dispose();
    }

    private sealed class LogWriter(DesktopLog log, string level, TextWriter? mirror) : TextWriter
    {
        private readonly StringBuilder partial = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value)
        {
            lock (log.gate)
            {
                if (value == '\n') { log.Write(level, partial.ToString().TrimEnd('\r'), mirror); partial.Clear(); }
                else partial.Append(value);
            }
        }
        public override void Write(string? value) { if (value is not null) foreach (var character in value) Write(character); }
        public override void WriteLine(string? value)
        {
            lock (log.gate) { Write(value); Write('\n'); }
        }
    }
}
