using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ITBees.Mdb.CashInventory;

/// <summary>
/// Stores cash inventory (banknotes & coins) in a JSON file
/// located one folder above the application folder.
/// </summary>
public sealed class FileCashInventoryService : ICashInventoryService
{
    private readonly ILogger<FileCashInventoryService> _logger;
    private readonly string _filePath;
    private readonly object _lock = new object();
    private CashInventoryStateVm _stateVm;

    public FileCashInventoryService(ILogger<FileCashInventoryService> logger)
    {
        _logger = logger;
        _filePath = ResolveFilePath();
        _stateVm = LoadFromFile();
    }

    public async Task RegisterBanknoteAcceptedAsync(int nominalInGrosze)
    {
        if (nominalInGrosze <= 0) return;

        lock (_lock)
        {
            if (!_stateVm.Banknotes.ContainsKey(nominalInGrosze))
                _stateVm.Banknotes[nominalInGrosze] = 0;

            _stateVm.Banknotes[nominalInGrosze]++;
            _stateVm.LastUpdatedUtc = DateTime.UtcNow;
            SaveToFileLocked();
        }

        await Task.CompletedTask;
    }

    public async Task RegisterCoinAcceptedAsync(int nominalInGrosze)
    {
        if (nominalInGrosze <= 0) return;

        lock (_lock)
        {
            if (!_stateVm.Coins.ContainsKey(nominalInGrosze))
                _stateVm.Coins[nominalInGrosze] = 0;

            _stateVm.Coins[nominalInGrosze]++;
            _stateVm.LastUpdatedUtc = DateTime.UtcNow;
            SaveToFileLocked();
        }

        await Task.CompletedTask;
    }

    public async Task RegisterCoinDispensedAsync(int nominalInGrosze)
    {
        if (nominalInGrosze <= 0) return;

        lock (_lock)
        {
            if (_stateVm.Coins.TryGetValue(nominalInGrosze, out var qty) && qty > 0)
            {
                _stateVm.Coins[nominalInGrosze] = qty - 1;
            }
            // If there is no coin recorded for that nominal, we do nothing.
            _stateVm.LastUpdatedUtc = DateTime.UtcNow;
            SaveToFileLocked();
        }

        await Task.CompletedTask;
    }

    public CashInventoryStateVm GetSnapshot()
    {
        lock (_lock)
        {
            // Return a shallow copy to avoid external modifications of internal state
            return new CashInventoryStateVm
            {
                Banknotes = new Dictionary<int, int>(_stateVm.Banknotes),
                Coins = new Dictionary<int, int>(_stateVm.Coins),
                LastUpdatedUtc = _stateVm.LastUpdatedUtc
            };
        }
    }

    private static string ResolveFilePath()
    {
        // Application base directory, e.g. /home/pi/ColumnApp/
        var baseDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        );

        var parent = Directory.GetParent(baseDir);
        var targetDir = parent?.FullName ?? baseDir;   // If no parent, fallback to baseDir

        // Result: one folder above application directory
        return Path.Combine(targetDir, "cash_inventory.json");
    }

    private CashInventoryStateVm LoadFromFile()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new CashInventoryStateVm { LastUpdatedUtc = DateTime.UtcNow };

            var json = File.ReadAllText(_filePath);
            var state = JsonSerializer.Deserialize<CashInventoryStateVm>(json);
            return state ?? new CashInventoryStateVm { LastUpdatedUtc = DateTime.UtcNow };
        }
        catch
        {
            // In case of any error (corrupted file, etc.), start from clean state
            _logger.LogError("Failed to load cash inventory from file '{FilePath}'. Starting from empty state.", _filePath);
            return new CashInventoryStateVm { LastUpdatedUtc = DateTime.UtcNow };
        }
    }

    /// <summary>
    /// Must be called under _lock.
    /// Writes to a temp file and then atomically replaces the target file.
    /// </summary>
    private void SaveToFileLocked()
    {
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(_stateVm, options);
            var tempPath = _filePath + ".tmp";

            File.WriteAllText(tempPath, json);
#if NET6_0_OR_GREATER
            File.Move(tempPath, _filePath, true);
#else
                if (File.Exists(_filePath))
                    File.Delete(_filePath);
                File.Move(tempPath, _filePath);
#endif
        }
        catch
        {
            _logger.LogError("Failed to save cash inventory to file '{FilePath}'.", _filePath);
        }
    }
}