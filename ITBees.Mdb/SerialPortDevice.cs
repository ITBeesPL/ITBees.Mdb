using System.IO.Ports;

namespace ITBees.Mdb;

public class SerialPortDevice : ISerialDevice
{
    private SerialPort _port;
        
    public void PrepareSerialPortDevice(string portName , int baudRate = 115200, int timeout = 1000)
    {
        _port = new SerialPort(portName, baudRate)
        {
            ReadTimeout = timeout,
            WriteTimeout = timeout
        };
        _port.Open();
    }
    public bool IsOpen => _port.IsOpen;
    public void Open(string portName)
    {
        _port.PortName = portName;
        _port.Open();
    }

    public void Close() => _port.Close();
    public void Write(string data)
    {
        if (!_port.IsOpen)
            throw new InvalidOperationException("Port is not open");
        _port.WriteLine(data);
        Thread.Sleep(50);
    }
    public string Read()
    {
        try { return _port.ReadLine().Trim(); }
        catch (TimeoutException) { return string.Empty; }
    }
    public void Dispose() => Close();
}