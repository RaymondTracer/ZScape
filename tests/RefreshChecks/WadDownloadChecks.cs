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
    }

    private static async Task CheckContentRecognitionAsync(TestDirectory fixture, CancellationToken ct)
    {
        using var zipStream = new MemoryStream();
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("MAPINFO").Open())
            entry.Write(Encoding.ASCII.GetBytes("map MAP01 \"Test\" {}"));

        var formats = new (string Name, byte[] Bytes)[]
        {
            ("PWAD", CreateWad("PWAD")),
            ("IWAD", CreateWad("IWAD")),
            ("ZIP", zipStream.ToArray()),
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
        await using var server = new DownloadServer(new Dictionary<string, byte[]>
        {
            ["wad-named-package.pk3"] = wad,
            ["wrong-hash.pk3"] = wad,
            ["error-page.pk3"] = Encoding.ASCII.GetBytes("<html>Download unavailable</html>")
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
        var destination = fixture.GetPath("downloads");
        await downloader.DownloadWadsAsync([valid, mismatch, html], destination, ct);
        ct.ThrowIfCancellationRequested();

        var savedPath = Path.Combine(destination, valid.Wad.FileName);
        RegressionAssert.That(valid.Status == WadDownloadStatus.Completed && File.Exists(savedPath)
            && (await File.ReadAllBytesAsync(savedPath, ct)).SequenceEqual(wad),
            "Public download accepts WAD content named .pk3, preserves its advertised filename and verifies its full MD5");
        RegressionAssert.That(mismatch.Status == WadDownloadStatus.Failed
            && mismatch.ErrorMessage?.Contains("hash", StringComparison.OrdinalIgnoreCase) == true
            && !File.Exists(Path.Combine(destination, mismatch.Wad.FileName)),
            "Recognized cross-extension content still fails when the advertised MD5 differs");
        RegressionAssert.That(html.Status == WadDownloadStatus.Failed
            && html.ErrorMessage?.Contains("not a valid", StringComparison.OrdinalIgnoreCase) == true
            && !File.Exists(Path.Combine(destination, html.Wad.FileName)),
            "Public download rejects and removes an HTML response even when served as octet-stream");
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
        private readonly Task _serving;
        public string Site { get; }

        public DownloadServer(IReadOnlyDictionary<string, byte[]> files)
        {
            _files = files;
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
                    var request = (await reader.ReadLineAsync(ct))?.Split(' ');
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }
                    byte[]? payload = null;
                    var found = request is { Length: >= 2 }
                        && _files.TryGetValue(Uri.UnescapeDataString(request[1].TrimStart('/')), out payload);
                    var body = payload ?? [];
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\n"
                        + $"Content-Length: {body.Length}\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, ct);
                    if (found && request![0] != "HEAD")
                        await stream.WriteAsync(body, ct);
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
