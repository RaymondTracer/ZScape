namespace ZScape.Protocol;

using ZScape.Utilities;

/// <summary>
/// Protocol constants for Zandronum master server and game server communication.
/// Based on the Doomseeker reference implementation.
/// </summary>
public static class ProtocolConstants
{
    // Master server settings
    public const string MasterServerHost = "master.zandronum.com";
    public const int MasterServerPort = 15300;

    // Master server protocol
    public const int MasterChallenge = 5660028;
    public const short MasterProtocolVersion = 2;

    // Master response codes
    public const int MasterResponseGood = 0;
    public const int MasterResponseServer = 1;
    public const int MasterResponseEnd = 2;
    public const int MasterResponseBanned = 3;
    public const int MasterResponseBad = 4;
    public const int MasterResponseWrongVersion = 5;
    public const int MasterResponseBeginPart = 6;
    public const int MasterResponseEndPart = 7;
    public const int MasterResponseServerBlock = 8;

    // Server challenge
    public const int ServerChallenge = 199; // 0xC7

    // Server response codes
    public const int ServerGoodSingle = 5660023;
    public const int ServerWait = 5660024;
    public const int ServerBanned = 5660025;
    public const int ServerGoodSegmented = 5660032;

    // Query flags (SQF)
    [Flags]
    public enum QueryFlags : uint
    {
        None = 0,
        Name = 0x00000001,
        Url = 0x00000002,
        Email = 0x00000004,
        MapName = 0x00000008,
        MaxClients = 0x00000010,
        MaxPlayers = 0x00000020,
        PWads = 0x00000040,
        GameType = 0x00000080,
        GameName = 0x00000100,
        Iwad = 0x00000200,
        ForcePassword = 0x00000400,
        ForceJoinPassword = 0x00000800,
        GameSkill = 0x00001000,
        BotSkill = 0x00002000,
        DmFlags = 0x00004000, // Deprecated
        Limits = 0x00010000,
        TeamDamage = 0x00020000,
        TeamScores = 0x00040000, // Deprecated
        NumPlayers = 0x00080000,
        PlayerData = 0x00100000,
        TeamInfoNumber = 0x00200000,
        TeamInfoName = 0x00400000,
        TeamInfoColor = 0x00800000,
        TeamInfoScore = 0x01000000,
        TestingServer = 0x02000000,
        DataMd5Sum = 0x04000000, // Deprecated
        AllDmFlags = 0x08000000,
        SecuritySettings = 0x10000000,
        OptionalWads = 0x20000000,
        Deh = 0x40000000,
        ExtendedInfo = 0x80000000,

        StandardQuery = Name | Url | Email | MapName | MaxClients |
                        MaxPlayers | PWads | GameType | Iwad |
                        ForcePassword | ForceJoinPassword | Limits |
                        NumPlayers | PlayerData | TeamInfoNumber |
                        TeamInfoName | TeamInfoScore | GameSkill |
                        TestingServer | AllDmFlags | SecuritySettings |
                        OptionalWads | Deh | ExtendedInfo
    }

    // Extended query flags (SQF2)
    [Flags]
    public enum ExtendedQueryFlags : uint
    {
        None = 0,
        PwadHashes = 0x00000001,
        Country = 0x00000002,
        GameModeName = 0x00000004,
        GameModeShortName = 0x00000008,
        VoiceChat = 0x00000010,

        StandardQuery = PwadHashes | Country | GameModeName
    }

    // Network settings - delegate to centralized AppConstants
    public static int DefaultTimeout => AppConstants.Timeouts.DefaultTimeoutMs;
    public static int ServerQueryTimeout => AppConstants.Timeouts.ServerQueryTimeoutMs;
    public static int MaxPacketSize => AppConstants.BufferSizes.NetworkBuffer;
    public const int HuffmanBufferMultiplier = 3;
}
