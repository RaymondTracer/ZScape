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
    public bool ShowHexDumps { get; set; } = false;

    private LoggingService()
    {
        try
        {
            File.WriteAllText(_logFilePath, $"=== ZScape runtime log started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
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

    public void LogHexDump(byte[] data, string label)
    {
        if (!VerboseMode || !ShowHexDumps || data == null || data.Length == 0)
            return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"[HEX] {label} ({data.Length} bytes):");
        
        const int bytesPerLine = 16;
        for (int offset = 0; offset < data.Length; offset += bytesPerLine)
        {
            int lineSize = Math.Min(bytesPerLine, data.Length - offset);
            
            // Hex part
            sb.Append($"  {offset:X8}: ");
            for (int i = 0; i < lineSize; i++)
            {
                sb.Append($"{data[offset + i]:X2} ");
            }
            for (int i = lineSize; i < bytesPerLine; i++)
            {
                sb.Append("   ");
            }
            
            // ASCII part
            sb.Append("| ");
            for (int i = 0; i < lineSize; i++)
            {
                char c = (char)data[offset + i];
                sb.Append(c >= 0x20 && c <= 0x7E ? c : '.');
            }
            sb.AppendLine();
        }
        
        Log(sb.ToString(), LogLevel.Verbose);
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
                File.AppendAllText(_logFilePath, message + Environment.NewLine);
            }
        }
        catch
        {
            // Best-effort logging only.
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
