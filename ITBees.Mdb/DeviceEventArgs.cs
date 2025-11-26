namespace ITBees.Mdb;

public class DeviceEventArgs : EventArgs
{
    public DeviceEventType EventType { get; }
    public DeviceEventType? TargetCashHolder { get; }
    public PaymentType PaymentType { get; }
    public int Amount { get; set; }
    public bool? Accepted { get; set; }
    public string Message { get; set; }

    public DeviceEventArgs(DeviceEventType evt, PaymentType type, int amount = 0, bool? accepted = null, string message = null, DeviceEventType? targetCashHolder = null)
    {
        EventType = evt;
        PaymentType = type;
        Amount = amount;
        Accepted = accepted;
        Message = message;
        TargetCashHolder = targetCashHolder;
    }
}