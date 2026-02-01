using System.Security.Cryptography;
using ZScape.Models;
using ZScape.Utilities;

namespace ZScape.Services;

/// <summary>
/// Manages WAD file discovery across configured search paths.
/// </summary>
public class WadManager
{
    private static WadManager? _instance;
    public static WadManager Instance => _instance ??= new WadManager();
    
    private readonly Dictionary<string, string> _wadCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _searchPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _executableFolders = new(StringComparer.OrdinalIgnoreCase);
    private string _downloadPath = string.Empty;
    private readonly LoggingService _logger = LoggingService.Instance;
    
    /// <summary>
    /// Forbidden WAD names that should never be downloaded (commercial IWADs, etc.).
    /// </summary>
    public static readonly HashSet<string> ForbiddenWads = new(StringComparer.OrdinalIgnoreCase)
    {
        "attack", "blacktwr", "bloodsea", "canyon", "catwalk", "combine",
        "doom", "doom1", "doom2", "doomu", "freedoom1", "freedoom2",
        "fistula", "garrison", "geryon", "heretic", "hexen", "hexdd",
        "manor", "mephisto", "minos", "nessus", "paradox", "plutonia",
        "subspace", "subterra", "teeth", "tnt", "ttrap", "sigil_shreds",
        "sigil_shreds_compat", "strife1", "vesperas", "virgil", "voices",
        "chex", "chex3", "hacx", "freedm", "nerve"
    };
    
    /// <summary>
    /// Gets or sets the list of paths to search for WAD files.
    /// </summary>
    public IReadOnlyCollection<string> SearchPaths => _searchPaths;
    
    /// <summary>
    /// Gets the list of Zandronum executable folders (highest priority for WAD search).
    /// </summary>
    public IReadOnlyCollection<string> ExecutableFolders => _executableFolders;
    
    /// <summary>
    /// Gets or sets the default path for downloading WAD files.
    /// </summary>
    public string DownloadPath
    {
        get => _downloadPath;
        set => _downloadPath = value;
    }
    
    private WadManager() { }
    
    /// <summary>
    /// Adds a search path for WAD files.
    /// </summary>
    public void AddSearchPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            _searchPaths.Add(path);
        }
    }
    
    /// <summary>
    /// Removes a search path.
    /// </summary>
    public void RemoveSearchPath(string path)
    {
        _searchPaths.Remove(path);
    }
    
    /// <summary>
    /// Clears all search paths.
    /// </summary>
    public void ClearSearchPaths()
    {
        _searchPaths.Clear();
    }
    
    /// <summary>
    /// Sets the search paths from a collection.
    /// </summary>
    public void SetSearchPaths(IEnumerable<string> paths)
    {
        _searchPaths.Clear();
        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p)))
        {
            _searchPaths.Add(path);
        }
    }
    
    /// <summary>
    /// Sets the Zandronum executable folders. WADs in these folders have highest priority.
    /// Call this when Zandronum path settings change.
    /// </summary>
    /// <param name="exePaths">Zandronum executable paths (not folders). The parent directories will be used.</param>
    public void SetExecutableFolders(IEnumerable<string> exePaths)
    {
        _executableFolders.Clear();
        var pathList = exePaths.ToList();
        foreach (var exePath in pathList)
        {
            if (string.IsNullOrWhiteSpace(exePath))
                continue;
            if (!File.Exists(exePath))
            {
                _logger.Warning($"Zandronum exe not found: {exePath}");
                continue;
            }
            var folder = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                _executableFolders.Add(folder);
                _logger.Verbose($"Added WAD search folder: {folder}");
            }
        }
    }
    
    /// <summary>
    /// Adds a Zandronum executable folder directly.
    /// </summary>
    public void AddExecutableFolder(string folder)
    {
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            _executableFolders.Add(folder);
        }
    }
    
    /// <summary>
    /// Rebuilds the WAD cache by scanning all search paths.
    /// Priority order: 1) Executable folders, 2) Download path, 3) Search paths
    /// </summary>
    public void RefreshCache()
    {
        _wadCache.Clear();
        
        // Log configured paths for debugging
        _logger.Info($"WAD search: {_executableFolders.Count} exe folders, download={!string.IsNullOrEmpty(_downloadPath)}, {_searchPaths.Count} search paths");
        if (_executableFolders.Count == 0)
        {
            _logger.Warning("No executable folders configured - WADs in Zandronum folder won't be found. Configure Zandronum path in Settings.");
        }
        _logger.Verbose($"  Executable folders: {string.Join(", ", _executableFolders)}");
        _logger.Verbose($"  Download path: {_downloadPath}");
        _logger.Verbose($"  Search paths: {string.Join(", ", _searchPaths)}");
        
        // Scan executable folders first (highest priority - where Zandronum.exe lives)
        foreach (var exeFolder in _executableFolders)
        {
            ScanDirectory(exeFolder);
        }
        
        // Scan download path second
        if (!string.IsNullOrEmpty(_downloadPath) && Directory.Exists(_downloadPath))
        {
            ScanDirectory(_downloadPath);
        }
        
        // Scan search paths last
        foreach (var searchPath in _searchPaths)
        {
            ScanDirectory(searchPath);
        }
        
        var pathCount = _executableFolders.Count + _searchPaths.Count + (string.IsNullOrEmpty(_downloadPath) ? 0 : 1);
        _logger.Verbose($"WAD cache refreshed: {_wadCache.Count} files found in {pathCount} paths");
    }
    
    private void ScanDirectory(string path)
    {
        try
        {
            int count = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file);
                if (WadExtensions.IsSupportedExtension(ext))
                {
                    var fileName = Path.GetFileName(file);
                    // Don't overwrite if already found (first path wins)
                    if (_wadCache.TryAdd(fileName, file))
                    {
                        count++;
                    }
                }
            }
            _logger.Verbose($"  Scanned {path}: found {count} WAD files");
        }
        catch (Exception ex)
        {
            _logger.Warning($"Error scanning directory {path}: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Finds a WAD file by name.
    /// </summary>
    /// <param name="wadName">The WAD filename (e.g., "brutal.pk3")</param>
    /// <returns>Full path to the WAD if found, null otherwise.</returns>
    public string? FindWad(string wadName)
    {
        if (string.IsNullOrWhiteSpace(wadName))
            return null;
        
        // Check cache first (case-insensitive key lookup)
        var cacheKey = wadName.ToLowerInvariant();
        if (_wadCache.TryGetValue(cacheKey, out var cachedPath))
        {
            if (File.Exists(cachedPath))
                return cachedPath;
            // File was deleted, remove from cache
            _wadCache.Remove(cacheKey);
        }
        
        // Build list of paths to search in priority order:
        // 1) Executable folders (where Zandronum.exe lives)
        // 2) Download path (recently downloaded files)
        // 3) Configured search paths
        var pathsToSearch = new List<string>();
        pathsToSearch.AddRange(_executableFolders.Where(Directory.Exists));
        if (!string.IsNullOrEmpty(_downloadPath) && Directory.Exists(_downloadPath))
            pathsToSearch.Add(_downloadPath);
        pathsToSearch.AddRange(_searchPaths);
        
        // Search all paths (case-insensitive file matching)
        foreach (var searchPath in pathsToSearch)
        {
            // First try direct path (case-insensitive on Windows, but we handle it explicitly)
            var match = FindFileIgnoreCase(searchPath, wadName);
            if (match != null)
            {
                _wadCache[cacheKey] = match;
                return match;
            }
            
            // Try subdirectories
            try
            {
                foreach (var file in Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(file).Equals(wadName, StringComparison.OrdinalIgnoreCase))
                    {
                        _wadCache[cacheKey] = file;
                        return file;
                    }
                }
            }
            catch { /* Ignore access errors */ }
        }
        
        return null;
    }

    /// <summary>
    /// Finds a file in a directory with case-insensitive name matching.
    /// </summary>
    private static string? FindFileIgnoreCase(string directory, string fileName)
    {
        if (!Directory.Exists(directory))
            return null;
        
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (Path.GetFileName(file).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                {
                    return file;
                }
            }
        }
        catch { /* Ignore access errors */ }
        
        return null;
    }
    
    /// <summary>
    /// Checks which WADs from a list are missing.
    /// </summary>
    public List<WadInfo> GetMissingWads(IEnumerable<string> wadNames, string? serverUrl = null)
    {
        var missing = new List<WadInfo>();
        
        foreach (var wadName in wadNames.Where(w => !string.IsNullOrWhiteSpace(w)))
        {
            var baseName = Path.GetFileNameWithoutExtension(wadName);
            
            // Skip forbidden WADs
            if (ForbiddenWads.Contains(baseName))
                continue;
            
            var path = FindWad(wadName);
            if (path == null)
            {
                missing.Add(new WadInfo(wadName) { ServerUrl = serverUrl });
            }
        }
        
        return missing;
    }
    
    /// <summary>
    /// Checks which WADs from a server are missing.
    /// </summary>
    public List<WadInfo> GetMissingWadsForServer(ServerInfo server)
    {
        var wadsToCheck = new List<string>();
        
        // Add IWAD
        if (!string.IsNullOrEmpty(server.IWAD))
            wadsToCheck.Add(server.IWAD);
        
        // Add PWADs
        wadsToCheck.AddRange(server.PWADs.Select(p => p.Name));
        
        return GetMissingWads(wadsToCheck, server.Website);
    }
    
    /// <summary>
    /// Checks if a WAD name is forbidden (commercial IWAD, etc.).
    /// </summary>
    public static bool IsForbiddenWad(string wadName)
    {
        var baseName = Path.GetFileNameWithoutExtension(wadName);
        return ForbiddenWads.Contains(baseName);
    }
    
    /// <summary>
    /// Gets all WADs in the cache.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetAllCachedWads() => _wadCache;
    
    /// <summary>
    /// Computes the MD5 hash of a file.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>MD5 hash as lowercase hex string, or null if file doesn't exist.</returns>
    public static string? ComputeFileHash(string filePath)
    {
        if (!File.Exists(filePath))
            return null;
        
        try
        {
            using var stream = File.OpenRead(filePath);
            using var md5 = MD5.Create();
            var hashBytes = md5.ComputeHash(stream);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
    
    /// <summary>
    /// Finds a WAD file by its hash, searching in all configured paths.
    /// This includes archived versions with hash suffixes like "modname_abc123.pk3".
    /// </summary>
    /// <param name="expectedHash">The expected MD5 hash.</param>
    /// <param name="baseName">The base name of the WAD (without extension).</param>
    /// <param name="extension">The file extension (e.g., ".pk3").</param>
    /// <returns>Path to file with matching hash, or null if not found.</returns>
    public string? FindWadByHash(string expectedHash, string baseName, string extension)
    {
        if (string.IsNullOrEmpty(expectedHash))
            return null;
        
        // Build search paths in priority order: exe folders, download path, search paths
        var searchPaths = new List<string>();
        searchPaths.AddRange(_executableFolders.Where(Directory.Exists));
        if (!string.IsNullOrEmpty(_downloadPath) && Directory.Exists(_downloadPath))
            searchPaths.Add(_downloadPath);
        searchPaths.AddRange(_searchPaths.Where(Directory.Exists));
        
        foreach (var searchPath in searchPaths)
        {
            try
            {
                // Search for files matching the base pattern (case-insensitive)
                // Matches baseName.pk3, baseName_abc123.pk3, BASENAME.PK3, etc.
                foreach (var file in Directory.EnumerateFiles(searchPath, "*", SearchOption.AllDirectories))
                {
                    var fileName = Path.GetFileName(file);
                    var fileExt = Path.GetExtension(file);
                    var fileBase = Path.GetFileNameWithoutExtension(file);
                    
                    // Check if extension matches (case-insensitive)
                    if (!fileExt.Equals(extension, StringComparison.OrdinalIgnoreCase))
                        continue;
                    
                    // Check if file starts with base name (case-insensitive)
                    if (!fileBase.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    
                    var hash = ComputeFileHash(file);
                    if (string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.Verbose($"Found matching hash for {baseName}: {file}");
                        return file;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Error searching for WAD by hash in {searchPath}: {ex.Message}");
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Gets all archived versions of a WAD (files with hash suffixes).
    /// </summary>
    /// <param name="baseName">The base name of the WAD.</param>
    /// <param name="extension">The file extension.</param>
    /// <returns>Dictionary of hash to file path.</returns>
    public Dictionary<string, string> GetArchivedVersions(string baseName, string extension)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        
        // Build search paths in priority order: exe folders, download path, search paths
        var searchPaths = new List<string>();
        searchPaths.AddRange(_executableFolders.Where(Directory.Exists));
        if (!string.IsNullOrEmpty(_downloadPath) && Directory.Exists(_downloadPath))
            searchPaths.Add(_downloadPath);
        searchPaths.AddRange(_searchPaths.Where(Directory.Exists));
        
        foreach (var searchPath in searchPaths)
        {
            try
            {
                var pattern = $"{baseName}_*{extension}";
                var files = Directory.GetFiles(searchPath, pattern, SearchOption.AllDirectories);
                
                foreach (var file in files)
                {
                    var hash = ComputeFileHash(file);
                    if (hash != null && !results.ContainsKey(hash))
                    {
                        results[hash] = file;
                    }
                }
            }
            catch { /* Ignore access errors */ }
        }
        
        return results;
    }
    
    /// <summary>
    /// Renames a WAD file to include its hash suffix for archival.
    /// </summary>
    /// <param name="filePath">Path to the WAD file.</param>
    /// <returns>New path with hash suffix, or null on failure.</returns>
    public string? ArchiveWadWithHash(string filePath)
    {
        if (!File.Exists(filePath))
            return null;
        
        var hash = ComputeFileHash(filePath);
        if (hash == null)
            return null;
        
        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);
        
        // Use first 12 characters of hash for filename
        var shortHash = hash.Substring(0, Math.Min(12, hash.Length));
        var newFileName = $"{baseName}_{shortHash}{extension}";
        var newPath = Path.Combine(directory, newFileName);
        
        // Check if archived version already exists
        if (File.Exists(newPath))
        {
            var existingHash = ComputeFileHash(newPath);
            if (string.Equals(existingHash, hash, StringComparison.OrdinalIgnoreCase))
            {
                // Same file already archived, just delete the current one
                File.Delete(filePath);
                return newPath;
            }
            // Different file with same short hash - use full hash
            newFileName = $"{baseName}_{hash}{extension}";
            newPath = Path.Combine(directory, newFileName);
        }
        
        try
        {
            File.Move(filePath, newPath);
            _logger.Info($"Archived WAD: {Path.GetFileName(filePath)} -> {newFileName}");
            return newPath;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to archive WAD {filePath}: {ex.Message}");
            return null;
        }
    }
    
    /// <summary>
    /// Activates an archived WAD version by renaming it to the standard name.
    /// </summary>
    /// <param name="archivedPath">Path to the archived WAD.</param>
    /// <param name="standardName">The standard filename (e.g., "mod.pk3").</param>
    /// <returns>New path with standard name, or null on failure.</returns>
    public string? ActivateArchivedWad(string archivedPath, string standardName)
    {
        if (!File.Exists(archivedPath))
            return null;
        
        var directory = Path.GetDirectoryName(archivedPath) ?? string.Empty;
        var newPath = Path.Combine(directory, standardName);
        
        try
        {
            // If a file with standard name exists, archive it first
            if (File.Exists(newPath))
            {
                var archived = ArchiveWadWithHash(newPath);
                if (archived == null)
                {
                    _logger.Error($"Failed to archive existing WAD: {standardName}");
                    return null;
                }
            }
            
            File.Move(archivedPath, newPath);
            _logger.Info($"Activated WAD: {Path.GetFileName(archivedPath)} -> {standardName}");
            
            // Update cache
            _wadCache[standardName] = newPath;
            
            return newPath;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to activate WAD {archivedPath}: {ex.Message}");
            return null;
        }
    }
}
