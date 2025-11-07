using System.Diagnostics;
using System.IO.Ports;
using Microsoft.Extensions.Logging;

namespace ITBees.Mdb
{
    public sealed class SerialPortDevice : ISerialDevice, IDisposable
    {
        private readonly ILogger<SerialPortDevice> _logger;

        public SerialPortDevice(ILogger<SerialPortDevice> logger)
        {
            _logger = logger;
        }

        private SerialPort? _port;

        /* ------------------------------------------------------------
         *  ISerialDevice members
         * ----------------------------------------------------------*/

        /// <summary>Open and configure the port in one call.</summary>
        public void PrepareSerialPortDevice(string port,
            int baud = 115200,
            int timeout = 1000)
        {
            try
            {
                _port = new SerialPort(port, baud)
                {
                    NewLine = "\r", // read until CR (no LF)
                    ReadTimeout = timeout,
                    WriteTimeout = timeout
                };
                _port.Open();
            }
            catch (Exception e)
            {
                _logger.LogError(e,"Prepare serial port device  error : " + e.Message);
            }
        }

        /// <summary>True when the underlying SerialPort is open.</summary>
        public bool IsOpen => _port?.IsOpen == true;

        /// <summary>Open an already configured SerialPort.</summary>
        public void Open(string portName)
        {
            try
            {
                if (_port == null)
                    PrepareSerialPortDevice(portName);
                else
                {
                    _port.PortName = portName;
                    if (!_port.IsOpen) _port.Open();
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, e.Message);
            }
        }

        /// <summary>Close the port if it is open.</summary>
        public void Close()
        {
            try
            {
                _port?.Close();
            }
            catch (Exception e)
            {
                _logger.LogError(e, e.Message);
            }
        }

        /// <summary>Write ASCII command + single CR (0x0D).</summary>
        public void Write(string ascii)
        {
            try
            {
                if (!IsOpen)
                {
                    Debug.WriteLine("SerialPort closed");
                    return;
                }

                _port!.Write(ascii + "\r"); // <CR> terminator
                Thread.Sleep(20); // small gap for some bridges
            }
            catch (Exception e)
            {
                _logger.LogError(e, e.Message);
            }
        }

        /// <summary>Read one line (up to CR). Returns empty on timeout.</summary>
        public string Read()
        {
            if (!IsOpen) return string.Empty;

            try
            {
                return _port!.ReadLine().Trim();
            }
            catch (TimeoutException)
            {
                return string.Empty;
            }
        }

        /* ------------------------------------------------------------
         *  IDisposable
         * ----------------------------------------------------------*/
        public void Dispose() => Close();
    }
}