namespace ITBees.Mdb;

public class DeviceEventArgs : EventArgs
{
    public DeviceEventType EventType { get; }
    public PaymentType PaymentType { get; }
    public int Amount { get; }
    public bool? Accepted { get; }
    public string Message { get; }

    public DeviceEventArgs(DeviceEventType evt, PaymentType type, int amount = 0, bool? accepted = null, string message = null)
    {
        EventType = evt;
        PaymentType = type;
        Amount = amount;
        Accepted = accepted;
        Message = message;
    }
}