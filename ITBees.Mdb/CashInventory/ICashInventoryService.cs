namespace ITBees.Mdb.CashInventory;

public interface ICashInventoryService
{
    Task RegisterBanknoteAcceptedAsync(int nominalInGrosze);   // e.g. 1000 = 10 PLN
    Task RegisterCoinAcceptedAsync(int nominalInGrosze);       // e.g. 200 = 2 PLN
    Task RegisterCoinDispensedAsync(int nominalInGrosze);      // coin used as change

    CashInventoryStateVm GetSnapshot();
    Task ResetBanknotesAsync();
    Task ResetCoinsAsync();
}