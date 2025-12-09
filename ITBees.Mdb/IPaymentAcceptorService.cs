namespace ITBees.Mdb;

public interface IPaymentAcceptorService
{
    event EventHandler<DeviceEventArgs> DeviceEvent;
    void Start(string portName);
    void Stop();
    void Accept();
    void Return();
    bool DispenseChange(int amount);
    bool DeviceRunning();
    void EnableVerboseDebugLogging(bool enable);
}