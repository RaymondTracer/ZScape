internal static class RegressionAssert
{
    public static void That(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}

/// <summary>Isolated fixtures stay on the repository drive, never in portable app settings.</summary>
internal sealed class TestDirectory : IDisposable
{
    public string Path { get; }

    public TestDirectory()
    {
        Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            "artifacts", "regression-fixtures", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path);
    }

    public string GetPath(params string[] parts) =>
        System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
