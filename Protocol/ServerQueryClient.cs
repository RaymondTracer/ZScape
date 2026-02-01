using System.Net;
using System.Net.Sockets;
using System.Text;
using ZScape.Models;
using ZScape.Services;

namespace ZScape.Protocol;

/// <summary>
/// Client for querying individual Zandronum game servers.
/// </summary>
public class ServerQueryClient : IDisposable
{
    private readonly LoggingService _logger = LoggingService.Instance;
    private bool _disposed;

    public int Timeout { get; set; } = ProtocolConstants.ServerQueryTimeout;

    public event EventHandler<ServerQueryEventArgs>? ServerQueried;

    /// <summary>
    /// Queries a single server for details.
    /// </summary>
    public async Task<ServerInfo?> QueryServerAsync(IPEndPoint endpoint, CancellationToken cancellationToken = default)
    {
        var server = new ServerInfo { EndPoint = endpoint };

        try
        {
            _logger.Verbose($"Querying server {endpoint}...");
            server.QuerySentTime = DateTime.Now;

            using var udpClient = new UdpClient();
            udpClient.Client.ReceiveTimeout = Timeout;
            udpClient.Client.SendTimeout = Timeout;

            // Create and encode the challenge
            byte[] challenge = CreateServerChallenge();
            _logger.LogHexDump(challenge, $"Server Challenge to {endpoint} (unencoded)");

            byte[]? encodedChallenge = HuffmanCodec.Instance.Encode(challenge);
            if (encodedChallenge == null)
            {
                _logger.Warning($"Failed to encode server challenge for {endpoint}");
                server.ErrorMessage = "Failed to encode challenge";
                server.IsOnline = false;
                return server;
            }
            _logger.LogHexDump(encodedChallenge, $"Server Challenge to {endpoint} (encoded)");

            // Send challenge
            await udpClient.SendAsync(encodedChallenge, endpoint, cancellationToken);

            // Wait for response with timeout
            using var timeoutCts = new CancellationTokenSource(Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            // May need to collect segmented response
            var segments = new Dictionary<int, SegmentData>();
            int totalSegments = 1;
            int totalSize = 0;

            while (!linkedCts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await udpClient.ReceiveAsync(linkedCts.Token);
                    server.LastQueryTime = DateTime.Now;
                    server.Ping = (int)(server.LastQueryTime - server.QuerySentTime).TotalMilliseconds;

                    _logger.Verbose($"Server {endpoint} responded: {result.Buffer.Length} bytes, ping: {server.Ping}ms");
                    _logger.LogHexDump(result.Buffer, $"Server Response from {endpoint} (encoded)");

                    // Decode the response
                    byte[]? decoded = HuffmanCodec.Instance.Decode(result.Buffer);
                    if (decoded == null || decoded.Length < 4)
                    {
                        _logger.Warning($"Failed to decode response from {endpoint}");
                        continue;
                    }
                    _logger.LogHexDump(decoded, $"Server Response from {endpoint} (decoded)");

                    // Check response type
                    using var ms = new MemoryStream(decoded);
                    using var reader = new BinaryReader(ms);

                    int responseCode = reader.ReadInt32();
                    _logger.Verbose($"Server response code: {responseCode}");

                    switch (responseCode)
                    {
                        case ProtocolConstants.ServerBanned:
                            server.ErrorMessage = "Banned from server";
                            server.IsOnline = false;
                            return server;

                        case ProtocolConstants.ServerWait:
                            server.ErrorMessage = "Server busy, try again later";
                            server.IsOnline = true;
                            return server;

                        case ProtocolConstants.ServerGoodSingle:
                            // Single packet response - parse directly
                            byte[] remaining = new byte[ms.Length - ms.Position];
                            ms.Read(remaining, 0, remaining.Length);
                            ParseServerData(server, remaining);
                            server.IsQueried = true;
                            server.IsOnline = true;
                            ServerQueried?.Invoke(this, new ServerQueryEventArgs(server, true));
                            return server;

                        case ProtocolConstants.ServerGoodSegmented:
                            // Segmented response
                            var segmentInfo = ParseSegmentHeader(reader);
                            if (segmentInfo == null)
                            {
                                _logger.Warning($"Invalid segment header from {endpoint}");
                                continue;
                            }

                            totalSegments = segmentInfo.Value.TotalSegments;
                            totalSize = segmentInfo.Value.TotalSize;

                            byte[] segmentData = reader.ReadBytes(segmentInfo.Value.SegmentSize);
                            segments[segmentInfo.Value.SegmentNo] = new SegmentData
                            {
                                Offset = segmentInfo.Value.Offset,
                                Data = segmentData
                            };

                            _logger.Verbose($"Received segment {segmentInfo.Value.SegmentNo + 1}/{totalSegments} " +
                                          $"(offset: {segmentInfo.Value.Offset}, size: {segmentInfo.Value.SegmentSize})");

                            // Check if we have all segments
                            if (segments.Count >= totalSegments)
                            {
                                byte[] fullData = ReassembleSegments(segments, totalSize);
                                ParseServerData(server, fullData);
                                server.IsQueried = true;
                                server.IsOnline = true;
                                ServerQueried?.Invoke(this, new ServerQueryEventArgs(server, true));
                                return server;
                            }
                            break;

                        default:
                            _logger.Verbose($"Unknown response code {responseCode} from {endpoint}");
                            break;
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    break;
                }
            }

            // If we got some segments but not all, try to use what we have
            if (segments.Count > 0 && segments.Count < totalSegments)
            {
                _logger.Warning($"Only received {segments.Count}/{totalSegments} segments from {endpoint}");
            }

            server.ErrorMessage = "Server did not respond";
            server.IsOnline = false;
            ServerQueried?.Invoke(this, new ServerQueryEventArgs(server, false));
            return server;
        }
        catch (Exception ex)
        {
            _logger.Verbose($"Error querying {endpoint}: {ex.Message}");
            server.ErrorMessage = ex.Message;
            server.IsOnline = false;
            ServerQueried?.Invoke(this, new ServerQueryEventArgs(server, false));
            return server;
        }
    }

    /// <summary>
    /// Queries multiple servers in parallel.
    /// </summary>
    public async Task<List<ServerInfo>> QueryServersAsync(IEnumerable<IPEndPoint> endpoints, 
        int maxConcurrent = 50, CancellationToken cancellationToken = default)
    {
        var results = new List<ServerInfo>();
        var semaphore = new SemaphoreSlim(maxConcurrent);
        var tasks = new List<Task<ServerInfo?>>();

        foreach (var endpoint in endpoints)
        {
            await semaphore.WaitAsync(cancellationToken);
            
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    return await QueryServerAsync(endpoint, cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        var completed = await Task.WhenAll(tasks);
        results.AddRange(completed.Where(s => s != null)!);

        return results;
    }

    private byte[] CreateServerChallenge()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Challenge code (4 bytes) - LAUNCHER_SERVER_CHALLENGE = 199 as little-endian int32
        writer.Write((int)ProtocolConstants.ServerChallenge);

        // Query flags (4 bytes)
        writer.Write((uint)ProtocolConstants.QueryFlags.StandardQuery);

        // Timestamp (4 bytes) - used for ping calculation
        uint timestamp = (uint)(DateTime.Now.TimeOfDay.TotalMilliseconds);
        writer.Write(timestamp);

        // Extended query flags (4 bytes) - only if SQF_ExtendedInfo is set
        writer.Write((uint)ProtocolConstants.ExtendedQueryFlags.StandardQuery);

        // Request segmented response preference (1 byte):
        // 0 = don't care, 1 = prefer single, 2 = prefer segmented
        writer.Write((byte)0x00);

        return ms.ToArray();
    }

    private (int SegmentNo, int TotalSegments, int Offset, int SegmentSize, int TotalSize)? ParseSegmentHeader(BinaryReader reader)
    {
        try
        {
            byte segmentNo = (byte)(reader.ReadByte() & 0x7F);
            byte totalSegments = reader.ReadByte();
            ushort offset = reader.ReadUInt16();
            ushort segmentSize = reader.ReadUInt16();
            ushort totalSize = reader.ReadUInt16();

            return (segmentNo, totalSegments, offset, segmentSize, totalSize);
        }
        catch
        {
            return null;
        }
    }

    private byte[] ReassembleSegments(Dictionary<int, SegmentData> segments, int totalSize)
    {
        byte[] result = new byte[totalSize];
        foreach (var segment in segments.Values)
        {
            Array.Copy(segment.Data, 0, result, segment.Offset, segment.Data.Length);
        }
        return result;
    }

    private void ParseServerData(ServerInfo server, byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        try
        {
            // Skip timestamp (4 bytes)
            if (ms.Length < 4) return;
            reader.ReadInt32();

            // Read game version
            server.GameVersion = ReadNullTerminatedString(reader);

            // Read query flags
            if (ms.Position + 4 > ms.Length) return;
            uint flags = reader.ReadUInt32();

            bool hasTeams = (flags & (uint)ProtocolConstants.QueryFlags.TeamInfoNumber) != 0;

            // Parse based on flags
            if ((flags & (uint)ProtocolConstants.QueryFlags.Name) != 0)
            {
                server.Name = ReadNullTerminatedString(reader);
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.Url) != 0)
            {
                server.Website = ReadNullTerminatedString(reader);
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.Email) != 0)
            {
                server.Email = ReadNullTerminatedString(reader);
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.MapName) != 0)
            {
                server.Map = ReadNullTerminatedString(reader);
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.MaxClients) != 0)
            {
                server.MaxClients = reader.ReadByte();
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.MaxPlayers) != 0)
            {
                server.MaxPlayers = reader.ReadByte();
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.PWads) != 0)
            {
                int numPwads = reader.ReadByte();
                for (int i = 0; i < numPwads; i++)
                {
                    string wadName = ReadNullTerminatedString(reader);
                    server.PWADs.Add(new PWadInfo { Name = wadName });
                }
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.GameType) != 0)
            {
                int gameMode = reader.ReadByte();
                server.GameMode = GameMode.FromCode(gameMode);
                server.Instagib = reader.ReadByte() != 0;
                server.Buckshot = reader.ReadByte() != 0;
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.GameName) != 0)
            {
                // Skip game name (usually just "DOOM" or similar)
                ReadNullTerminatedString(reader);
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.Iwad) != 0)
            {
                server.IWAD = ReadNullTerminatedString(reader);
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.ForcePassword) != 0)
            {
                server.IsPassworded = reader.ReadByte() != 0;
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.ForceJoinPassword) != 0)
            {
                server.RequiresJoinPassword = reader.ReadByte() != 0;
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.GameSkill) != 0)
            {
                server.Skill = reader.ReadByte();
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.BotSkill) != 0)
            {
                server.BotSkill = reader.ReadByte();
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.Limits) != 0)
            {
                server.FragLimit = reader.ReadUInt16();
                server.TimeLimit = reader.ReadUInt16();
                if (server.TimeLimit != 0)
                {
                    server.TimeLeft = reader.ReadUInt16();
                }
                server.DuelLimit = reader.ReadUInt16();
                server.PointLimit = reader.ReadUInt16();
                server.WinLimit = reader.ReadUInt16();
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.TeamDamage) != 0)
            {
                server.TeamDamage = reader.ReadSingle();
            }

            // Skip deprecated team scores
            if ((flags & (uint)ProtocolConstants.QueryFlags.TeamScores) != 0)
            {
                reader.ReadInt16();
                reader.ReadInt16();
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.NumPlayers) != 0)
            {
                server.CurrentPlayers = reader.ReadByte();
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.PlayerData) != 0)
            {
                for (int i = 0; i < server.CurrentPlayers; i++)
                {
                    var player = new PlayerInfo
                    {
                        Name = ReadNullTerminatedString(reader),
                        Score = reader.ReadInt16(),
                        Ping = reader.ReadUInt16(),
                        IsSpectator = reader.ReadByte() != 0,
                        IsBot = reader.ReadByte() != 0
                    };

                    if (hasTeams)
                    {
                        player.Team = reader.ReadByte();
                    }

                    // Skip time on server
                    reader.ReadByte();

                    server.Players.Add(player);
                }
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.TeamInfoNumber) != 0)
            {
                server.NumTeams = reader.ReadByte();
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.TeamInfoName) != 0)
            {
                for (int i = 0; i < server.NumTeams && i < server.Teams.Length; i++)
                {
                    server.Teams[i].Name = ReadNullTerminatedString(reader);
                }
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.TeamInfoColor) != 0)
            {
                for (int i = 0; i < server.NumTeams && i < server.Teams.Length; i++)
                {
                    uint colorRgb = reader.ReadUInt32();
                    server.Teams[i].Color = Color.FromArgb((int)colorRgb);
                }
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.TeamInfoScore) != 0)
            {
                for (int i = 0; i < server.NumTeams && i < server.Teams.Length; i++)
                {
                    server.Teams[i].Score = reader.ReadInt16();
                }
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.TestingServer) != 0)
            {
                server.IsTestingServer = reader.ReadByte() != 0;
                server.TestingArchive = ReadNullTerminatedString(reader);
            }

            // Skip DMFlags parsing for now
            if ((flags & (uint)ProtocolConstants.QueryFlags.AllDmFlags) != 0)
            {
                // Number of flag sets
                if (ms.Position < ms.Length)
                {
                    int numFlagSets = reader.ReadByte();
                    for (int i = 0; i < numFlagSets; i++)
                    {
                        if (ms.Position + 4 <= ms.Length)
                            reader.ReadUInt32(); // Skip flag value
                    }
                }
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.SecuritySettings) != 0)
            {
                server.IsSecure = reader.ReadByte() != 0;
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.OptionalWads) != 0)
            {
                int numOptional = reader.ReadByte();
                for (int i = 0; i < numOptional; i++)
                {
                    int index = reader.ReadByte();
                    if (index < server.PWADs.Count)
                    {
                        server.PWADs[index].IsOptional = true;
                    }
                }
            }

            if ((flags & (uint)ProtocolConstants.QueryFlags.Deh) != 0)
            {
                int numDehs = reader.ReadByte();
                for (int i = 0; i < numDehs; i++)
                {
                    string deh = ReadNullTerminatedString(reader);
                    server.PWADs.Add(new PWadInfo { Name = deh });
                }
            }

            // Extended info (SQF2)
            if ((flags & (uint)ProtocolConstants.QueryFlags.ExtendedInfo) != 0)
            {
                ParseExtendedInfo(server, reader);
            }

            _logger.Verbose($"Parsed server: {server.Name} - {server.Map} [{server.CurrentPlayers}/{server.MaxPlayers}]");
        }
        catch (Exception ex)
        {
            _logger.Verbose($"Error parsing server data: {ex.Message}");
        }
    }

    private void ParseExtendedInfo(ServerInfo server, BinaryReader reader)
    {
        try
        {
            if (reader.BaseStream.Position + 4 > reader.BaseStream.Length)
                return;

            uint flags2 = reader.ReadUInt32();

            if ((flags2 & (uint)ProtocolConstants.ExtendedQueryFlags.PwadHashes) != 0)
            {
                int numHashes = reader.ReadByte();
                for (int i = 0; i < numHashes && i < server.PWADs.Count; i++)
                {
                    server.PWADs[i].Hash = ReadNullTerminatedString(reader);
                }
            }

            if ((flags2 & (uint)ProtocolConstants.ExtendedQueryFlags.Country) != 0)
            {
                byte[] countryBytes = reader.ReadBytes(3);
                server.Country = Encoding.ASCII.GetString(countryBytes).TrimEnd('\0').ToUpperInvariant();
            }

            if ((flags2 & (uint)ProtocolConstants.ExtendedQueryFlags.GameModeName) != 0)
            {
                string gameModeName = ReadNullTerminatedString(reader);
                // Could update GameMode name here if needed
            }
        }
        catch
        {
            // Extended info is optional, don't fail on errors
        }
    }

    private string ReadNullTerminatedString(BinaryReader reader)
    {
        var bytes = new List<byte>();
        byte b;
        while (reader.BaseStream.Position < reader.BaseStream.Length && (b = reader.ReadByte()) != 0)
        {
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private class SegmentData
    {
        public int Offset { get; set; }
        public byte[] Data { get; set; } = [];
    }
}

public class ServerQueryEventArgs : EventArgs
{
    public ServerInfo Server { get; }
    public bool Success { get; }

    public ServerQueryEventArgs(ServerInfo server, bool success)
    {
        Server = server;
        Success = success;
    }
}
