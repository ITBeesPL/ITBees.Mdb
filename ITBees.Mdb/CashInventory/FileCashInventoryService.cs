using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ITBees.Mdb.CashInventory;

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
    
    public Task FlushAsync()
    {
        lock (_lock)
        {
            // no-op, just ensure previous writes are visible
        }
        return Task.CompletedTask;
    }
    
    public Task RegisterBanknoteAcceptedAsync(int nominalInGrosze)
    {
        if (nominalInGrosze <= 0) return Task.CompletedTask;

        lock (_lock)
        {
            var entry = _stateVm.Banknotes.FirstOrDefault(x => x.NominalInGrosze == nominalInGrosze);
            if (entry == null)
            {
                _stateVm.Banknotes.Add(new Banknote
                {
                    NominalInGrosze = nominalInGrosze,
                    Quantity = 1
                });
            }
            else
            {
                entry.Quantity++;
            }

            _stateVm.LastUpdatedUtc = DateTime.UtcNow;
            SaveToFileLocked();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Coin routed to tubes (available for change).
    /// </summary>
    public Task RegisterCoinAcceptedAsync(int nominalInGrosze)
    {
        if (nominalInGrosze <= 0) return Task.CompletedTask;

        lock (_lock)
        {
            var entry = _stateVm.Coins.FirstOrDefault(x => x.NominalInGrosze == nominalInGrosze);
            if (entry == null)
            {
                _stateVm.Coins.Add(new Coin
                {
                    NominalInGrosze = nominalInGrosze,
                    Quantity = 1
                });
            }
            else
            {
                entry.Quantity++;
            }

            _stateVm.LastUpdatedUtc = DateTime.UtcNow;
            SaveToFileLocked();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Coin routed directly to cashbox.
    /// </summary>
    public Task RegisterCoinToCashboxAcceptedAsync(int nominalInGrosze)
    {
        if (nominalInGrosze <= 0) return Task.CompletedTask;

        lock (_lock)
        {
            var entry = _stateVm.CoinsInCashbox.FirstOrDefault(x => x.NominalInGrosze == nominalInGrosze);
            if (entry == null)
            {
                _stateVm.CoinsInCashbox.Add(new Coin
                {
                    NominalInGrosze = nominalInGrosze,
                    Quantity = 1
                });
            }
            else
            {
                entry.Quantity++;
            }

            _stateVm.LastUpdatedUtc = DateTime.UtcNow;
            SaveToFileLocked();
        }

        return Task.CompletedTask;
    }

    public Task RegisterCoinDispensedAsync(int nominalInGrosze)
    {
        if (nominalInGrosze <= 0) return Task.CompletedTask;

        lock (_lock)
        {
            var entry = _stateVm.Coins.FirstOrDefault(x => x.NominalInGrosze == nominalInGrosze);
            if (entry != null && entry.Quantity > 0)
            {
                entry.Quantity--;
                if (entry.Quantity <= 0)
                {
                    _stateVm.Coins.Remove(entry);
                }
            }

            _stateVm.LastUpdatedUtc = DateTime.UtcNow;
            SaveToFileLocked();
        }

        return Task.CompletedTask;
    }

    public CashInventoryStateVm GetSnapshot()
    {
        lock (_lock)
        {
            return new CashInventoryStateVm
            {
                Banknotes = _stateVm.Banknotes
                    .Select(x => new Banknote
                    {
                        NominalInGrosze = x.NominalInGrosze,
                        Quantity = x.Quantity
                    })
                    .ToList(),

                Coins = _stateVm.Coins
                    .Select(x => new Coin
                    {
                        NominalInGrosze = x.NominalInGrosze,
                        Quantity = x.Quantity
                    })
                    .ToList(),

                CoinsInCashbox = _stateVm.CoinsInCashbox
                    .Select(x => new Coin
                    {
                        NominalInGrosze = x.NominalInGrosze,
                        Quantity = x.Quantity
                    })
                    .ToList(),

                LastUpdatedUtc = _stateVm.LastUpdatedUtc
            };
        }
    }

    private static string ResolveFilePath()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        );

        var parent = Directory.GetParent(baseDir);
        var targetDir = parent?.FullName ?? baseDir;

        return Path.Combine(targetDir, "cash_inventory.json");
    }

    private CashInventoryStateVm LoadFromFile()
    {
        try
        {
            var state = new CashInventoryStateVm { LastUpdatedUtc = DateTime.UtcNow };
            if (!File.Exists(_filePath))
            {
                NormalizeState(state);
                return state;
            }

            var json = File.ReadAllText(_filePath);
            state = JsonSerializer.Deserialize<CashInventoryStateVm>(json)
                        ?? new CashInventoryStateVm { LastUpdatedUtc = DateTime.UtcNow };

            NormalizeState(state);
            return state;
        }
        catch
        {
            _logger.LogError("Failed to load cash inventory from file '{FilePath}'. Starting from empty state.",
                _filePath);
            var state = new CashInventoryStateVm { LastUpdatedUtc = DateTime.UtcNow };
            NormalizeState(state);
            return state;
        }
    }

    private static void NormalizeState(CashInventoryStateVm state)
    {
        state.Banknotes ??= new List<Banknote>();
        state.Coins ??= new List<Coin>();
        state.CoinsInCashbox ??= new List<Coin>();
    }

    private void SaveToFileLocked()
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
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

    public Task ResetBanknotesAsync()
    {
        lock (_lock)
        {
            _stateVm.Banknotes.Clear();
            _stateVm.LastUpdatedUtc = DateTime.UtcNow;
            SaveToFileLocked();
        }

        return Task.CompletedTask;
    }

    public Task ResetCoinsAsync()
    {
        lock (_lock)
        {
            _stateVm.Coins.Clear();
            _stateVm.LastUpdatedUtc = DateTime.UtcNow;
            SaveToFileLocked();
        }

        return Task.CompletedTask;
    }

    public Task ResetCoinsInCashboxAsync()
    {
        lock (_lock)
        {
            _stateVm.CoinsInCashbox.Clear();
            _stateVm.LastUpdatedUtc = DateTime.UtcNow;
            SaveToFileLocked();
        }

        return Task.CompletedTask;
    }
}