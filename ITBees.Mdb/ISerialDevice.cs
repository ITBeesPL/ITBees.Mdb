namespace ITBees.Mdb;

public interface ISerialDevice : IDisposable
{
    void Open(string portName);
    void Close();
    bool IsOpen { get; }
    void Write(string data);
    string Read();
    void PrepareSerialPortDevice(string portName, int baudRate = 115200, int timeout = 1000);
}