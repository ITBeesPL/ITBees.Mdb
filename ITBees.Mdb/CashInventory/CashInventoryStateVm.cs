namespace ITBees.Mdb.CashInventory;

public class CashInventoryStateVm
{
    // Key = nominal in grosze, Value = quantity
    public Dictionary<int, int> Banknotes { get; set; } = new Dictionary<int, int>();
    public Dictionary<int, int> Coins { get; set; } = new Dictionary<int, int>();
    public DateTime LastUpdatedUtc { get; set; }
}