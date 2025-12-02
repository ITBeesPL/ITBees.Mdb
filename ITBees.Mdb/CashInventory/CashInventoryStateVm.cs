namespace ITBees.Mdb.CashInventory;

public class CashInventoryStateVm
{
    // Key = nominal in grosze, Value = quantity
    public List<Banknote> Banknotes { get; set; } = new();
    public List<Coin> Coins { get; set; } = new();
    public DateTime LastUpdatedUtc { get; set; }
}

public class Banknote
{
    public int NominalInGrosze { get; set; }
    public int Quantity { get; set; }
}

public class Coin
{
    public int NominalInGrosze { get; set; }
    public int Quantity { get; set; }
}