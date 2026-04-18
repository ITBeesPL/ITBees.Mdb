using System.Diagnostics;
using System.IO.Ports;
using System.Text;
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
        private FileStream? _linuxStream;
        private string? _linuxPortName;
        private int _timeout = 1000;

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
                _timeout = timeout;
                Close();

                if (OperatingSystem.IsLinux())
                {
                    ConfigureLinuxPort(port, baud, timeout);
                    _linuxStream = new FileStream(port, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1,
                        FileOptions.None);
                    _linuxPortName = port;
                    return;
                }

                _port = new SerialPort(port, baud)
                {
                    NewLine = "\r",
                    ReadTimeout = timeout,
                    WriteTimeout = timeout
                };
                _port.Open();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Prepare serial port device  error : " + e.Message);
            }
        }

        /// <summary>True when the underlying SerialPort is open.</summary>
        public bool IsOpen => OperatingSystem.IsLinux() ? _linuxStream != null : _port?.IsOpen == true;

        /// <summary>Open an already configured SerialPort.</summary>
        public void Open(string portName)
        {
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    if (_linuxStream == null || !string.Equals(_linuxPortName, portName, StringComparison.Ordinal))
                        PrepareSerialPortDevice(portName, timeout: _timeout);

                    return;
                }

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
                if (_port != null)
                {
                    if (_port.IsOpen)
                        _port.Close();

                    _port.Dispose();
                    _port = null;
                }

                if (_linuxStream != null)
                {
                    _linuxStream.Dispose();
                    _linuxStream = null;
                    _linuxPortName = null;
                }
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

                if (OperatingSystem.IsLinux())
                {
                    var data = Encoding.ASCII.GetBytes(ascii + "\r");
                    _linuxStream!.Write(data, 0, data.Length);
                    _linuxStream.Flush();
                    Thread.Sleep(20);
                    return;
                }

                _port!.Write(ascii + "\r");
                Thread.Sleep(20);
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
                if (OperatingSystem.IsLinux())
                    return ReadLinuxLine();

                return _port!.ReadLine().Trim();
            }
            catch (TimeoutException)
            {
                _logger.LogError("SerialPort read timeout");
                return string.Empty;
            }
            catch (Exception e)
            {
                _logger.LogError("SerialPort read error: " + e.Message);
                return string.Empty;
            }
        }

        private void ConfigureLinuxPort(string port, int baud, int timeout)
        {
            var deciseconds = Math.Clamp((int)Math.Ceiling(timeout / 100.0), 1, 255);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "stty",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add("-F");
            process.StartInfo.ArgumentList.Add(port);
            process.StartInfo.ArgumentList.Add(baud.ToString());
            process.StartInfo.ArgumentList.Add("raw");
            process.StartInfo.ArgumentList.Add("-echo");
            process.StartInfo.ArgumentList.Add("min");
            process.StartInfo.ArgumentList.Add("0");
            process.StartInfo.ArgumentList.Add("time");
            process.StartInfo.ArgumentList.Add(deciseconds.ToString());

            if (!process.Start())
                throw new InvalidOperationException($"Unable to start stty for {port}");

            if (!process.WaitForExit(2000))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Best effort only.
                }

                throw new TimeoutException($"stty timed out for {port}");
            }

            var error = process.StandardError.ReadToEnd().Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"stty failed for {port}: {error}");
        }

        private string ReadLinuxLine()
        {
            if (_linuxStream == null)
                return string.Empty;

            var buffer = new List<byte>(64);
            var oneByte = new byte[1];
            var started = Stopwatch.StartNew();

            while (started.ElapsedMilliseconds <= Math.Max(_timeout, 100))
            {
                var read = _linuxStream.Read(oneByte, 0, 1);
                if (read == 0)
                    continue;

                var current = oneByte[0];
                if (current == '\r')
                    return Encoding.ASCII.GetString(buffer.ToArray()).Trim();

                if (current == '\n')
                {
                    if (buffer.Count == 0)
                        continue;

                    return Encoding.ASCII.GetString(buffer.ToArray()).Trim();
                }

                buffer.Add(current);
            }

            if (buffer.Count == 0)
                throw new TimeoutException();

            return Encoding.ASCII.GetString(buffer.ToArray()).Trim();
        }

        /* ------------------------------------------------------------
         *  IDisposable
         * ----------------------------------------------------------*/
        public void Dispose() => Close();
    }
}
