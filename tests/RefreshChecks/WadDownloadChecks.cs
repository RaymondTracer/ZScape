using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ZScape.Models;
using ZScape.Services;
using ZScape.Utilities;

internal static class WadDownloadChecks
{
    public static void Run() => Task.Run(RunAsync).GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        using var fixture = new TestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await CheckContentRecognitionAsync(fixture, timeout.Token);
        await CheckDownloadAsync(fixture, timeout.Token);
        await CheckCancelledDownloadAsync(fixture, timeout.Token);
        await CheckArchiveDownloadAsync(fixture, timeout.Token);
    }

    private static async Task CheckContentRecognitionAsync(TestDirectory fixture, CancellationToken ct)
    {
        var formats = new (string Name, byte[] Bytes)[]
        {
            ("PWAD", CreateWad("PWAD")),
            ("IWAD", CreateWad("IWAD")),
            ("ZIP", CreateZip("MAPINFO", Encoding.ASCII.GetBytes("map MAP01 \"Test\" {}"))),
            ("7z signature", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04])
        };
        foreach (var (name, bytes) in formats)
        {
            foreach (var extension in WadExtensions.WadFileExtensions)
            {
                var path = fixture.GetPath(name.Replace(' ', '-') + extension);
                await File.WriteAllBytesAsync(path, bytes, ct);
                RegressionAssert.That(await WadDownloader.HasExpectedFileSignatureAsync(path, ct),
                    $"Downloader recognizes {name} content named {extension}, independent of the filename format");
            }
        }

        var invalidPayloads = new byte[][]
        {
            [], Encoding.ASCII.GetBytes("<!DOCTYPE html><html>Not a download</html>"),
            [0, 1, 2, 3, 4, 5, 6, 7], Encoding.ASCII.GetBytes("PWA"),
            [0x50, 0x4B, 0x03], [0x37, 0x7A, 0xBC, 0xAF, 0x27]
        };
        foreach (var extension in WadExtensions.WadFileExtensions)
        {
            var path = fixture.GetPath("invalid" + extension);
            foreach (var bytes in invalidPayloads)
            {
                await File.WriteAllBytesAsync(path, bytes, ct);
                RegressionAssert.That(!await WadDownloader.HasExpectedFileSignatureAsync(path, ct),
                    $"Downloader rejects HTML, unknown content and incomplete signatures named {extension} ({bytes.Length} bytes)");
            }
        }

        var patch = fixture.GetPath("patch.deh");
        await File.WriteAllTextAsync(patch, "Patch File for DeHackEd v3.0", ct);
        RegressionAssert.That(await WadDownloader.HasExpectedFileSignatureAsync(patch, ct),
            "Direct DeHackEd patches retain their existing non-container validation behavior");
    }

    private static async Task CheckDownloadAsync(TestDirectory fixture, CancellationToken ct)
    {
        var wad = CreateWad("PWAD");
        var previous = CreateWad("IWAD");
        var destination = fixture.GetPath("downloads");
        Directory.CreateDirectory(destination);
        foreach (var name in new[] { "wad-named-package.pk3", "wrong-hash.pk3", "error-page.pk3" })
            await File.WriteAllBytesAsync(Path.Combine(destination, name), previous, ct);
        var otherFolder = fixture.GetPath("other-copy");
        Directory.CreateDirectory(otherFolder);
        var otherCopy = Path.Combine(otherFolder, "wad-named-package.pk3");
        await File.WriteAllBytesAsync(otherCopy, previous, ct);
        var originalsRetainedDuringDownload = true;
        await using var server = new DownloadServer(new Dictionary<string, byte[]>
        {
            ["wad-named-package.pk3"] = wad,
            ["wrong-hash.pk3"] = wad,
            ["error-page.pk3"] = Encoding.ASCII.GetBytes("<html>Download unavailable</html>")
        }, (name, _) =>
        {
            var originalPath = Path.Combine(destination, name);
            originalsRetainedDuringDownload &= File.Exists(originalPath)
                && File.ReadAllBytes(originalPath).SequenceEqual(previous);
            return Task.CompletedTask;
        });
        using var downloader = new WadDownloader([server.Site])
        {
            IdgamesEnabled = false,
            WebSearchEnabled = false
        };
        var matchingHash = Convert.ToHexString(MD5.HashData(wad)).ToLowerInvariant();
        var valid = new WadDownloadTask { Wad = new WadInfo("wad-named-package.pk3", matchingHash) };
        var mismatch = new WadDownloadTask { Wad = new WadInfo("wrong-hash.pk3", new string('0', 32)) };
        var html = new WadDownloadTask { Wad = new WadInfo("error-page.pk3") };
        var terminalNotifications = new System.Collections.Concurrent.ConcurrentDictionary<WadDownloadTask, bool>();
        downloader.DownloadCompleted += (_, completedTask) =>
            terminalNotifications[completedTask] = completedTask.IsFinished;
        await downloader.DownloadWadsAsync([valid, mismatch, html], destination, ct);
        ct.ThrowIfCancellationRequested();
        RegressionAssert.That(terminalNotifications.Count == 3 && terminalNotifications.Values.All(finished => finished)
            && new[] { valid, mismatch, html }.All(task => task.IsFinished),
            "The real downloader marks successful and exhausted failed tasks finished before terminal notifications");

        var savedPath = Path.Combine(destination, valid.Wad.FileName);
        RegressionAssert.That(valid.Status == WadDownloadStatus.Completed && File.Exists(savedPath)
            && (await File.ReadAllBytesAsync(savedPath, ct)).SequenceEqual(wad),
            "Public download accepts WAD content named .pk3, preserves its advertised filename and verifies its full MD5");
        if (WadHashCacheService.Instance.IsEnabled)
            RegressionAssert.That(WadHashCacheService.Instance.TryGetCachedHash(savedPath) == matchingHash,
                "The installed destination receives the verified full MD5 cache entry after its staging file is renamed");
        RegressionAssert.That(originalsRetainedDuringDownload,
            "Downloading leaves each existing destination untouched until its replacement passes validation");
        var archivedName = $"wad-named-package_{Convert.ToHexString(MD5.HashData(previous))[..12].ToLowerInvariant()}.pk3";
        RegressionAssert.That(File.ReadAllBytes(Path.Combine(destination, archivedName)).SequenceEqual(previous)
            && File.ReadAllBytes(otherCopy).SequenceEqual(previous)
            && Directory.EnumerateFiles(otherFolder).Count() == 1,
            "Successful replacement archives only the actual destination and preserves copies in other folders");
        RegressionAssert.That(mismatch.Status == WadDownloadStatus.Failed
            && mismatch.ErrorMessage?.Contains("hash", StringComparison.OrdinalIgnoreCase) == true
            && File.ReadAllBytes(Path.Combine(destination, mismatch.Wad.FileName)).SequenceEqual(previous),
            "A download with the wrong advertised MD5 retains the existing destination unchanged");
        RegressionAssert.That(html.Status == WadDownloadStatus.Failed
            && html.ErrorMessage?.Contains("not a valid", StringComparison.OrdinalIgnoreCase) == true
            && File.ReadAllBytes(Path.Combine(destination, html.Wad.FileName)).SequenceEqual(previous),
            "Rejected HTML served as octet-stream retains the existing destination unchanged");
        RegressionAssert.That(Directory.EnumerateFiles(destination).Count() == 4,
            "Failed validation produces no archives or staging files alongside the preserved destinations");
    }

    private static async Task CheckCancelledDownloadAsync(TestDirectory fixture, CancellationToken ct)
    {
        var destination = fixture.GetPath("cancelled-download");
        Directory.CreateDirectory(destination);
        var previous = CreateWad("IWAD");
        var originalPath = Path.Combine(destination, "cancel.pk3");
        await File.WriteAllBytesAsync(originalPath, previous, ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new DownloadServer(new Dictionary<string, byte[]>
        {
            ["cancel.pk3"] = CreateWad("PWAD")
        }, async (_, serverToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(serverToken);
        });
        using var downloader = new WadDownloader([server.Site]) { IdgamesEnabled = false, WebSearchEnabled = false };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = new WadDownloadTask { Wad = new WadInfo("cancel.pk3") };
        var finishedAtCancellationNotification = false;
        downloader.DownloadCompleted += (_, completedTask) =>
            finishedAtCancellationNotification = completedTask.IsFinished;
        var downloading = downloader.DownloadWadsAsync([task], destination, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(ct);
            RegressionAssert.That(File.ReadAllBytes(originalPath).SequenceEqual(previous),
                "An in-progress replacement retains the existing destination while waiting for network bytes");
            cancellation.Cancel();
            await downloading.WaitAsync(ct);
            RegressionAssert.That(task.IsFinished && finishedAtCancellationNotification,
                "The real downloader marks cancelled tasks finished before their terminal notification");
            RegressionAssert.That(task.Status == WadDownloadStatus.Cancelled
                && File.ReadAllBytes(originalPath).SequenceEqual(previous)
                && Directory.EnumerateFiles(destination).Count() == 1,
                "Cancelling a staged download preserves the original and cleans up its temporary output");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private static async Task CheckArchiveDownloadAsync(TestDirectory fixture, CancellationToken ct)
    {
        var previous = CreateWad("IWAD");
        var replacement = CreateWad("PWAD");
        var cases = new (string Name, byte[] Contents, string? Hash, bool Succeeds)[]
        {
            ("valid", replacement, Convert.ToHexString(MD5.HashData(replacement)).ToLowerInvariant(), true),
            ("hash-mismatch", replacement, new string('0', 32), false),
            ("invalid", Encoding.ASCII.GetBytes("<html>Invalid archive contents</html>"), null, false)
        };
        foreach (var test in cases)
        {
            var destination = fixture.GetPath("archive-download-" + test.Name);
            Directory.CreateDirectory(destination);
            var originalPath = Path.Combine(destination, "archive.pk3");
            await File.WriteAllBytesAsync(originalPath, previous, ct);
            var archiveCopy = Path.Combine(destination, "archive.zip");
            await File.WriteAllTextAsync(archiveCopy, "Existing source archive must be retained", ct);
            await using var server = new DownloadServer(new Dictionary<string, byte[]>
            {
                ["archive.zip"] = CreateZip("archive.pk3", test.Contents)
            });
            using var downloader = new WadDownloader([server.Site]) { IdgamesEnabled = false, WebSearchEnabled = false };
            var task = new WadDownloadTask { Wad = new WadInfo("archive.pk3", test.Hash) };
            await downloader.DownloadWadsAsync([task], destination, ct);
            ct.ThrowIfCancellationRequested();
            RegressionAssert.That(task.Status == (test.Succeeds ? WadDownloadStatus.Completed : WadDownloadStatus.Failed)
                && File.ReadAllBytes(originalPath).SequenceEqual(test.Succeeds ? replacement : previous)
                && File.ReadAllText(archiveCopy) == "Existing source archive must be retained"
                && Directory.EnumerateFiles(destination).Count() == (test.Succeeds ? 3 : 2),
                $"{test.Name} archive download validates its staged extraction before replacing or archiving only the destination");
            if (test.Succeeds && WadHashCacheService.Instance.IsEnabled)
                RegressionAssert.That(WadHashCacheService.Instance.TryGetCachedHash(originalPath) == test.Hash,
                    "An extracted replacement records its verified snapshot at the installed inner-file path");
        }
    }

    private static byte[] CreateZip(string fileName, byte[] contents)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry(fileName).Open())
            entry.Write(contents);
        return stream.ToArray();
    }

    private static byte[] CreateWad(string magic)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes(magic));
        writer.Write(1); // One lump.
        writer.Write(16); // Directory follows its four-byte contents.
        writer.Write(new byte[] { 1, 2, 3, 4 });
        writer.Write(12);
        writer.Write(4);
        writer.Write(new byte[] { (byte)'T', (byte)'E', (byte)'S', (byte)'T', 0, 0, 0, 0 });
        return stream.ToArray();
    }

    private sealed class DownloadServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new(TimeSpan.FromSeconds(35));
        private readonly IReadOnlyDictionary<string, byte[]> _files;
        private readonly Func<string, CancellationToken, Task>? _beforeBody;
        private readonly Task _serving;
        public string Site { get; }

        public DownloadServer(IReadOnlyDictionary<string, byte[]> files,
            Func<string, CancellationToken, Task>? beforeBody = null)
        {
            _files = files;
            _beforeBody = beforeBody;
            _listener.Start();
            Site = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/%WadName%";
            _serving = ServeAsync(_shutdown.Token);
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync(ct);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    try
                    {
                        var request = (await reader.ReadLineAsync(ct))?.Split(' ');
                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }
                        var fileName = request is { Length: >= 2 }
                            ? Uri.UnescapeDataString(request[1].TrimStart('/')) : string.Empty;
                        var found = _files.TryGetValue(fileName, out var payload);
                        var body = payload ?? [];
                        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\n"
                            + $"Content-Length: {body.Length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(header, ct);
                        if (found && request![0] != "HEAD")
                        {
                            if (_beforeBody != null) await _beforeBody(fileName, ct);
                            await stream.WriteAsync(body, ct);
                        }
                    }
                    catch (IOException)
                    {
                        // Discovery/download cancellation may close its peer connection.
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            try { await _serving; }
            finally
            {
                _listener.Stop();
                _shutdown.Dispose();
            }
        }
    }
}
