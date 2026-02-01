using System.Net;
using System.Net.Sockets;
using ZScape.Models;
using ZScape.Services;

namespace ZScape.Protocol;

/// <summary>
/// Client for communicating with the Zandronum master server.
/// </summary>
public class MasterServerClient : IDisposable
{
    private readonly LoggingService _logger = LoggingService.Instance;
    private UdpClient? _udpClient;
    private bool _disposed;

    public string Host { get; set; } = ProtocolConstants.MasterServerHost;
    public int Port { get; set; } = ProtocolConstants.MasterServerPort;
    public int Timeout { get; set; } = ProtocolConstants.DefaultTimeout;

    public event EventHandler<ServerEndpointEventArgs>? ServerFound;
    public event EventHandler<MasterServerEventArgs>? RefreshCompleted;
    public event EventHandler<MasterServerEventArgs>? RefreshFailed;

    /// <summary>
    /// Queries the master server for a list of game servers.
    /// Retries up to the configured number of attempts on failure.
    /// </summary>
    public async Task<List<IPEndPoint>> GetServerListAsync(CancellationToken cancellationToken = default)
    {
        var settings = SettingsService.Instance.Settings;
        int maxRetries = Math.Max(1, settings.MasterServerRetryCount);
        Exception? lastException = null;
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var result = await QueryMasterServerOnceAsync(cancellationToken);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw; // Don't retry on cancellation
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                lastException = ex;
                _logger.Warning($"Master server query attempt {attempt}/{maxRetries} failed: {ex.Message}");
                
                // Wait before retrying (use query retry delay setting)
                int retryDelay = Math.Max(500, settings.QueryRetryDelayMs);
                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (Exception ex)
            {
                lastException = ex;
                _logger.Error($"Master server query failed after {maxRetries} attempts: {ex.Message}");
                RefreshFailed?.Invoke(this, new MasterServerEventArgs(0, ex.Message));
                throw;
            }
        }
        
        // Should not reach here, but just in case
        throw lastException ?? new Exception("Master server query failed");
    }
    
    private async Task<List<IPEndPoint>> QueryMasterServerOnceAsync(CancellationToken cancellationToken)
    {
        var servers = new List<IPEndPoint>();
        var packetsReceived = new HashSet<int>();
        int expectedPackets = 0;
        bool readLastPacket = false;

        try
        {
            _logger.Info($"Querying master server at {Host}:{Port}...");
            
            _udpClient?.Dispose();
            _udpClient = new UdpClient();
            _udpClient.Client.ReceiveTimeout = Timeout;
            _udpClient.Client.SendTimeout = Timeout;

            // Resolve the master server address
            var addresses = await Dns.GetHostAddressesAsync(Host, cancellationToken);
            if (addresses.Length == 0)
            {
                throw new Exception($"Could not resolve master server address: {Host}");
            }

            var masterEndpoint = new IPEndPoint(addresses[0], Port);
            _logger.Verbose($"Resolved master server to {masterEndpoint}");

            // Create and encode the challenge packet
            byte[] challenge = CreateMasterChallenge();
            _logger.Verbose($"Created challenge packet ({challenge.Length} bytes unencoded)");
            _logger.LogHexDump(challenge, "Master Challenge (unencoded)");

            byte[]? encodedChallenge = HuffmanCodec.Instance.Encode(challenge);
            if (encodedChallenge == null)
            {
                throw new Exception("Failed to encode master server challenge");
            }
            _logger.Verbose($"Encoded challenge packet ({encodedChallenge.Length} bytes)");
            _logger.LogHexDump(encodedChallenge, "Master Challenge (encoded)");

            // Send the challenge
            await _udpClient.SendAsync(encodedChallenge, masterEndpoint, cancellationToken);
            _logger.Verbose("Challenge sent, waiting for response...");

            // Receive responses (may come in multiple packets)
            using var timeoutCts = new CancellationTokenSource(Timeout * 3);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            while (!linkedCts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync(linkedCts.Token);
                    _logger.Verbose($"Received packet: {result.Buffer.Length} bytes from {result.RemoteEndPoint}");
                    _logger.LogHexDump(result.Buffer, "Master Response (encoded)");

                    // Decode the response
                    byte[]? decoded = HuffmanCodec.Instance.Decode(result.Buffer);
                    if (decoded == null || decoded.Length < 4)
                    {
                        _logger.Warning("Failed to decode master server response or response too short");
                        continue;
                    }
                    _logger.LogHexDump(decoded, "Master Response (decoded)");

                    // Parse the response
                    var parseResult = ParseMasterResponse(decoded, servers, packetsReceived, ref expectedPackets, ref readLastPacket);
                    
                    switch (parseResult)
                    {
                        case MasterResponseResult.Good:
                            if (readLastPacket && (expectedPackets == 0 || packetsReceived.Count >= expectedPackets))
                            {
                                _logger.Success($"Master server query complete. Found {servers.Count} servers.");
                                RefreshCompleted?.Invoke(this, new MasterServerEventArgs(servers.Count, string.Empty));
                                return servers;
                            }
                            break;
                        case MasterResponseResult.Banned:
                            throw new Exception("You are banned from the master server");
                        case MasterResponseResult.Bad:
                            _logger.Warning("Received bad response from master server, retrying...");
                            break;
                        case MasterResponseResult.WrongVersion:
                            throw new Exception("Protocol version mismatch with master server");
                        case MasterResponseResult.Pending:
                            // Continue waiting for more packets
                            break;
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    if (servers.Count > 0)
                    {
                        _logger.Warning($"Timeout waiting for more packets, but got {servers.Count} servers");
                        break;
                    }
                    throw new TimeoutException("Master server did not respond");
                }
            }

            _logger.Success($"Master server query complete. Found {servers.Count} servers.");
            RefreshCompleted?.Invoke(this, new MasterServerEventArgs(servers.Count, string.Empty));
            return servers;
        }
        catch (Exception)
        {
            // Let the caller (GetServerListAsync) handle retries and events
            throw;
        }
    }

    private byte[] CreateMasterChallenge()
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        // Write challenge (4 bytes, little-endian)
        writer.Write(ProtocolConstants.MasterChallenge);
        
        // Write protocol version (2 bytes, little-endian)
        writer.Write(ProtocolConstants.MasterProtocolVersion);
        
        return ms.ToArray();
    }

    private MasterResponseResult ParseMasterResponse(byte[] data, List<IPEndPoint> servers, 
        HashSet<int> packetsReceived, ref int expectedPackets, ref bool readLastPacket)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);

        // Read response code
        int response = reader.ReadInt32();
        _logger.Verbose($"Master response code: {response}");

        switch (response)
        {
            case ProtocolConstants.MasterResponseBanned:
                return MasterResponseResult.Banned;
            case ProtocolConstants.MasterResponseBad:
                return MasterResponseResult.Bad;
            case ProtocolConstants.MasterResponseWrongVersion:
                return MasterResponseResult.WrongVersion;
            case ProtocolConstants.MasterResponseBeginPart:
                // Multi-packet response
                break;
            default:
                _logger.Verbose($"Unexpected response code: {response}");
                return MasterResponseResult.Pending;
        }

        // Read packet number
        if (ms.Position >= ms.Length) return MasterResponseResult.Bad;
        int packetNum = reader.ReadByte();
        
        if (!packetsReceived.Add(packetNum))
        {
            _logger.Verbose($"Already received packet {packetNum}, ignoring");
            return MasterResponseResult.Pending;
        }

        if (packetNum + 1 > expectedPackets)
        {
            expectedPackets = packetNum + 1;
        }

        _logger.Verbose($"Processing packet {packetNum + 1} (expected: {expectedPackets})");

        // Read server blocks
        if (ms.Position >= ms.Length) return MasterResponseResult.Bad;
        byte firstByte = reader.ReadByte();

        while (firstByte != ProtocolConstants.MasterResponseEndPart && 
               firstByte != ProtocolConstants.MasterResponseEnd)
        {
            // firstByte is server count in this block (sharing same IP)
            byte numServersInBlock = reader.ReadByte();
            
            while (numServersInBlock > 0)
            {
                if (ms.Position + 6 > ms.Length) return MasterResponseResult.Bad;

                // Read IP address (4 bytes)
                byte[] ipBytes = reader.ReadBytes(4);
                var ip = new IPAddress(ipBytes);

                // Read ports for each server sharing this IP
                for (int i = 0; i < numServersInBlock; i++)
                {
                    ushort port = reader.ReadUInt16();
                    var endpoint = new IPEndPoint(ip, port);
                    servers.Add(endpoint);
                    ServerFound?.Invoke(this, new ServerEndpointEventArgs(endpoint));
                    _logger.Verbose($"Found server: {endpoint}");
                }

                if (ms.Position >= ms.Length) break;
                numServersInBlock = reader.ReadByte();
            }

            if (ms.Position >= ms.Length) break;
            firstByte = reader.ReadByte();
        }

        if (firstByte == ProtocolConstants.MasterResponseEnd)
        {
            readLastPacket = true;
            _logger.Verbose("Received end-of-list marker");
        }

        if (readLastPacket && packetsReceived.Count >= expectedPackets)
        {
            return MasterResponseResult.Good;
        }

        return MasterResponseResult.Pending;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _udpClient?.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private enum MasterResponseResult
    {
        Good,
        Banned,
        Bad,
        WrongVersion,
        Pending
    }
}

public class ServerEndpointEventArgs : EventArgs
{
    public IPEndPoint Endpoint { get; }

    public ServerEndpointEventArgs(IPEndPoint endpoint)
    {
        Endpoint = endpoint;
    }
}

public class MasterServerEventArgs : EventArgs
{
    public int ServerCount { get; }
    public string ErrorMessage { get; }

    public MasterServerEventArgs(int serverCount, string errorMessage)
    {
        ServerCount = serverCount;
        ErrorMessage = errorMessage;
    }
}
