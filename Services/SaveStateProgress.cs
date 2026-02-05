namespace ZScape.Services;

/// <summary>
/// Progress data for state save operation.
/// </summary>
public class SaveStateProgress
{
    public int Current { get; init; }
    public int Total { get; init; }
    public string Status { get; init; } = string.Empty;
}
