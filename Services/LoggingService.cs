namespace ZScape.Services;

/// <summary>
/// Provides logging functionality with support for verbose mode.
/// </summary>
public class LoggingService
{
    public static LoggingService Instance { get; } = new();

    private readonly object _fileLock = new();
    private readonly string _logFilePath = Path.Combine(AppContext.BaseDirectory, "runtime.log");

    public event EventHandler<LogEntry>? LogAdded;
    
    public bool VerboseMode { get; set; } = false;

    private const long MaxLogFileSize = 10 * 1024 * 1024; // 10 MB
    private const int MaxLogFiles = 3;

    private LoggingService()
    {
        try
        {
            RotateLogIfNeeded();
            File.AppendAllText(_logFilePath, $"=== ZScape runtime log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
        catch
        {
            // Best-effort logging only.
        }
    }

    public string LogFilePath => _logFilePath;

    public void Log(string message, LogLevel level = LogLevel.Info)
    {
        var entry = new LogEntry(message, level, DateTime.Now);
        WriteToFile(entry.ToString());
        LogAdded?.Invoke(this, entry);
    }

    public void Exception(string context, Exception ex, LogLevel level = LogLevel.Error)
    {
        var prefix = string.IsNullOrWhiteSpace(context) ? "Unhandled exception" : context;
        Log($"{prefix}{Environment.NewLine}{ex}", level);
    }

    public void Verbose(string message)
    {
        if (VerboseMode)
        {
            Log(message, LogLevel.Verbose);
        }
    }

    public void Info(string message) => Log(message, LogLevel.Info);
    public void Warning(string message) => Log(message, LogLevel.Warning);
    public void Error(string message) => Log(message, LogLevel.Error);
    public void Success(string message) => Log(message, LogLevel.Success);

    private void WriteToFile(string message)
    {
        try
        {
            lock (_fileLock)
            {
                RotateLogIfNeeded();
                File.AppendAllText(_logFilePath, message + Environment.NewLine);
            }
        }
        catch
        {
            // Best-effort logging only.
        }
    }

    private void RotateLogIfNeeded()
    {
        try
        {
            if (!File.Exists(_logFilePath)) return;
            var info = new FileInfo(_logFilePath);
            if (info.Length < MaxLogFileSize) return;

            var dir = Path.GetDirectoryName(_logFilePath) ?? ".";
            var baseName = Path.GetFileNameWithoutExtension(_logFilePath);
            var ext = Path.GetExtension(_logFilePath);

            // Shift existing backups: runtime.2.log -> runtime.3.log, runtime.1.log -> runtime.2.log
            for (int i = MaxLogFiles - 1; i >= 1; i--)
            {
                var oldPath = Path.Combine(dir, $"{baseName}.{i}{ext}");
                var newPath = Path.Combine(dir, $"{baseName}.{i + 1}{ext}");
                if (File.Exists(oldPath))
                {
                    if (File.Exists(newPath)) File.Delete(newPath);
                    File.Move(oldPath, newPath);
                }
            }

            // Rename current log to .1
            var backupPath = Path.Combine(dir, $"{baseName}.1{ext}");
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Move(_logFilePath, backupPath);
        }
        catch
        {
            // Best-effort rotation only.
        }
    }
}

public class LogEntry : EventArgs
{
    public string Message { get; }
    public LogLevel Level { get; }
    public DateTime Timestamp { get; }

    public LogEntry(string message, LogLevel level, DateTime timestamp)
    {
        Message = message;
        Level = level;
        Timestamp = timestamp;
    }

    public override string ToString()
    {
        return $"[{Timestamp:HH:mm:ss}] [{Level}] {Message}";
    }
}

public enum LogLevel
{
    Verbose,
    Info,
    Success,
    Warning,
    Error
}
