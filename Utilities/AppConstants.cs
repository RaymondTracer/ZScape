namespace ZScape.Utilities;

/// <summary>
/// Centralized application constants to ensure consistency across the codebase.
/// This is the single source of truth for common configuration values.
/// </summary>
public static class AppConstants
{
    /// <summary>
    /// Buffer sizes for file I/O operations.
    /// </summary>
    public static class BufferSizes
    {
        /// <summary>
        /// Default buffer size for file stream operations (8 KB).
        /// Used for reading/writing files efficiently.
        /// </summary>
        public const int FileStreamBuffer = 8192;
        
        /// <summary>
        /// Default buffer size for network operations (8 KB).
        /// </summary>
        public const int NetworkBuffer = 8192;
    }
    
    /// <summary>
    /// Common timeout values used across the application.
    /// </summary>
    public static class Timeouts
    {
        /// <summary>
        /// Default timeout for general operations (5 seconds).
        /// </summary>
        public const int DefaultTimeoutMs = 5000;
        
        /// <summary>
        /// Timeout for server queries (3 seconds).
        /// </summary>
        public const int ServerQueryTimeoutMs = 3000;
        
        /// <summary>
        /// Timeout for connection tests (5 seconds).
        /// </summary>
        public const int ConnectionTestTimeoutMs = 5000;
        
        /// <summary>
        /// Timeout for HTTP connections (30 seconds).
        /// </summary>
        public const int HttpConnectTimeoutSeconds = 30;
        
        /// <summary>
        /// Timeout for long-running HTTP operations (30 minutes).
        /// </summary>
        public const int HttpLongOperationTimeoutMinutes = 30;
        
        /// <summary>
        /// Timeout for web requests like search index downloads (30 seconds).
        /// </summary>
        public const int WebRequestTimeoutSeconds = 30;
        
        /// <summary>
        /// Timeout for page crawling operations (15 seconds).
        /// </summary>
        public const int PageCrawlTimeoutSeconds = 15;
    }
    
    /// <summary>
    /// HTTP connection pooling settings.
    /// </summary>
    public static class HttpPooling
    {
        /// <summary>
        /// Lifetime of pooled connections for download clients (10 minutes).
        /// </summary>
        public const int DownloadPooledConnectionLifetimeMinutes = 10;
        
        /// <summary>
        /// Idle timeout for pooled connections (5 minutes).
        /// </summary>
        public const int PooledConnectionIdleTimeoutMinutes = 5;
        
        /// <summary>
        /// Lifetime of pooled connections for web/search clients (5 minutes).
        /// </summary>
        public const int WebPooledConnectionLifetimeMinutes = 5;
    }
    
    /// <summary>
    /// Application identity information.
    /// </summary>
    public static class AppInfo
    {
        /// <summary>
        /// Application name and version for User-Agent strings.
        /// </summary>
        public const string UserAgent = "ZScape/1.0";
        
        /// <summary>
        /// Full User-Agent string for WAD downloader.
        /// </summary>
        public const string WadDownloaderUserAgent = "ZScape/1.0 (WadDownloader)";
    }
    
    /// <summary>
    /// UI throttle and update intervals.
    /// </summary>
    public static class UiIntervals
    {
        /// <summary>
        /// Throttle interval for UI updates to prevent excessive redraws (250ms).
        /// </summary>
        public const int UiUpdateThrottleMs = 250;
        
        /// <summary>
        /// Throttle interval for progress reporting during operations (50ms).
        /// </summary>
        public const int ProgressReportThrottleMs = 50;
    }
    
    /// <summary>
    /// Default column widths for the server list view.
    /// These are used when no saved column widths exist.
    /// </summary>
    public static class ServerListColumns
    {
        public const int FavoriteWidth = 24;
        public const int IconWidth = 24;
        public const int NameWidth = 300;
        public const int NameMinWidth = 100;
        public const int PlayersWidth = 70;
        public const int PlayersMinWidth = 50;
        public const int PingWidth = 50;
        public const int PingMinWidth = 40;
        public const int MapWidth = 150;
        public const int MapMinWidth = 80;
        public const int GameModeWidth = 100;
        public const int GameModeMinWidth = 60;
        public const int IwadWidth = 80;
        public const int IwadMinWidth = 50;
        public const int AddressWidth = 140;
        public const int AddressMinWidth = 100;
    }
    
    /// <summary>
    /// Default column widths for the player list view.
    /// </summary>
    public static class PlayerListColumns
    {
        public const int NameWidth = 100;  // Will be adjusted dynamically to fill space
        public const int ScoreWidth = 50;
        public const int PingWidth = 50;
        public const int TeamWidth = 50;
    }
    
    /// <summary>
    /// Common UI element sizes.
    /// </summary>
    public static class UiSizes
    {
        /// <summary>
        /// Standard small button width (80 pixels).
        /// </summary>
        public const int ButtonWidthSmall = 80;
        
        /// <summary>
        /// Standard medium button width (90 pixels).
        /// </summary>
        public const int ButtonWidthMedium = 90;
        
        /// <summary>
        /// Standard large button width (110 pixels).
        /// </summary>
        public const int ButtonWidthLarge = 110;
        
        /// <summary>
        /// Standard button height (28 pixels).
        /// </summary>
        public const int ButtonHeight = 28;
    }
    
    /// <summary>
    /// Standard dialog dimensions.
    /// </summary>
    public static class DialogSizes
    {
        public const int AboutDialogWidth = 400;
        public const int AboutDialogHeight = 250;
    }
}
