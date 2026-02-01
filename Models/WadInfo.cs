using ZScape.Utilities;

namespace ZScape.Models;

/// <summary>
/// Represents information about a WAD file.
/// </summary>
public class WadInfo
{
    public string Name { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? LocalPath { get; set; }
    public bool IsFound => !string.IsNullOrEmpty(LocalPath);
    public long FileSize { get; set; }
    public bool IsOptional { get; set; }
    
    /// <summary>
    /// Expected MD5 hash of the file (from server).
    /// </summary>
    public string? ExpectedHash { get; set; }
    
    /// <summary>
    /// Server URL where this WAD might be available (from server's website field).
    /// </summary>
    public string? ServerUrl { get; set; }
    
    /// <summary>
    /// Direct download URL if found.
    /// </summary>
    public string? DownloadUrl { get; set; }
    
    public WadInfo() { }
    
    public WadInfo(string fileName)
    {
        FileName = fileName;
        Name = Path.GetFileNameWithoutExtension(fileName);
    }
    
    public WadInfo(string fileName, string? expectedHash) : this(fileName)
    {
        ExpectedHash = expectedHash;
    }
    
    public override string ToString() => FileName;
}

/// <summary>
/// Status of a WAD download operation.
/// </summary>
public enum WadDownloadStatus
{
    Pending,
    Searching,
    Queued,
    Downloading,
    Completed,
    Failed,
    Cancelled,
    AlreadyExists
}

/// <summary>
/// Represents a WAD download task with progress tracking.
/// </summary>
public class WadDownloadTask
{
    public WadInfo Wad { get; set; } = new();
    public WadDownloadStatus Status { get; set; } = WadDownloadStatus.Pending;
    public string StatusMessage { get; set; } = "Pending";
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public double BytesPerSecond { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SourceUrl { get; set; }
    public int ThreadCount { get; set; } = 1;
    
    /// <summary>
    /// The actual filename downloaded (may differ from Wad.FileName if found as different extension/archive).
    /// </summary>
    public string? DownloadedFileName { get; set; }
    
    /// <summary>
    /// List of alternate URLs to try if current source fails.
    /// </summary>
    public List<(string Url, long Size)> AlternateUrls { get; set; } = [];
    
    /// <summary>
    /// Number of retry attempts made.
    /// </summary>
    public int RetryCount { get; set; }
    
    /// <summary>
    /// Maximum retries per source before trying next alternate.
    /// </summary>
    public const int MaxRetriesPerSource = 2;
    
    /// <summary>
    /// Number of sites searched so far.
    /// </summary>
    private int _sitesSearched;
    public int SitesSearched
    {
        get => _sitesSearched;
        set => _sitesSearched = value;
    }
    
    /// <summary>
    /// Thread-safe increment of SitesSearched counter.
    /// </summary>
    public int IncrementSitesSearched() => Interlocked.Increment(ref _sitesSearched);
    
    /// <summary>
    /// Total number of sites to search.
    /// </summary>
    public int TotalSitesToSearch { get; set; }
    
    public double ProgressPercent => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100 : 0;
    
    public string ProgressText
    {
        get
        {
            if (TotalBytes <= 0) return StatusMessage;
            return $"{FormatBytes(BytesDownloaded)} / {FormatBytes(TotalBytes)} ({ProgressPercent:F1}%)";
        }
    }
    
    public string SpeedText => BytesPerSecond > 0 ? $"{FormatUtils.FormatBytes((long)BytesPerSecond)}/s" : "";
    
    private static string FormatBytes(long bytes) => FormatUtils.FormatBytes(bytes);
}
