using ZScape.Services;

namespace ZScape.Models;

public enum WadHashLookupMode
{
    FileNameOnly,
    CachedOnly,
    Verify
}

public enum WadResolutionStatus
{
    Found,
    Matched,
    Missing,
    Mismatch,
    Unavailable
}

/// <summary>A server requirement resolved to one particular local file.</summary>
public sealed record WadResolutionResult(
    string FileName,
    string? ExpectedHash,
    WadResolutionStatus Status,
    string? LocalPath = null,
    WadHashResult? HashResult = null,
    string? ErrorMessage = null,
    IReadOnlyList<string>? MismatchedPaths = null)
{
    public WadHashLookupMode LookupMode { get; init; } = WadHashLookupMode.FileNameOnly;
    public bool IsArchived { get; init; }
    public bool IsUsable => Status is WadResolutionStatus.Found or WadResolutionStatus.Matched;
    public string? LocalHash => HashResult?.Hash;
    public bool FromCache => HashResult?.FromCache == true;
}

public sealed record WadCandidateHashProgress(
    string FileName,
    string CandidatePath,
    int CandidateIndex,
    int CandidateCount,
    long BytesProcessed,
    long TotalBytes);
