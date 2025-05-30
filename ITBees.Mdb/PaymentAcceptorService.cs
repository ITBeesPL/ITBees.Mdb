using System.IO.Ports;

namespace ITBees.Mdb
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public enum DeviceEventType
    {
        CashEscrowRequested,
        CashProcessed,
        CoinReceived,
        CoinProcessed,
        Error,
        Initialized
    }

    public enum PaymentType { Cash, Coin }

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

    public interface ISerialDevice : IDisposable
    {
        void Open();
        void Close();
        bool IsOpen { get; }
        void Write(string data);
        string Read();
    }

    public class SerialPortDevice : ISerialDevice
    {
        private readonly SerialPort _port;
        public SerialPortDevice(string portName, int baudRate = 115200, int timeout = 1000)
        {
            _port = new SerialPort(portName, baudRate)
            {
                ReadTimeout = timeout,
                WriteTimeout = timeout
            };
        }
        public bool IsOpen => _port.IsOpen;
        public void Open() => _port.Open();
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

    public class PaymentAcceptorService
    {
        private readonly ISerialDevice _device;
        private readonly int[] _billValues = { 1000, 2000, 5000, 10000, 20000, 50000 };
        private readonly int[] _coinValues = { 10, 20, 50, 100, 200, 500 };
        private CancellationTokenSource _cts;
        private TaskCompletionSource<bool> _escrowDecision;

        public event EventHandler<DeviceEventArgs> DeviceEvent;

        public PaymentAcceptorService(ISerialDevice device)
        {
            _device = device;
        }

        public void Start()
        {
            try
            {
                _device.Open();
                InitDevices();
                _cts = new CancellationTokenSource();
                _ = PollLoop(_cts.Token);
            }
            catch (Exception ex)
            {
                EmitError(ex.Message);
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _device.Write("M,0"); _device.Read(); } catch { }
            _device.Close();
        }

        public void Accept()
        {
            _escrowDecision?.TrySetResult(true);
        }

        public void Return()
        {
            _escrowDecision?.TrySetResult(false);
        }

        private void InitDevices()
        {
            _device.Write("M,1"); _device.Read();
            // Bill validator init
            foreach (var cmd in new[] { "R,30", "R,31", "R,34,FFFFFFFF", "R,35,0" })
            {
                _device.Write(cmd);
                _device.Read();
            }
            // Coin acceptor init
            foreach (var cmd in new[] { "R,08", "R,09", "R,0C,FFFFFFFF" })
            {
                _device.Write(cmd);
                _device.Read();
            }
            DeviceEvent?.Invoke(this, new DeviceEventArgs(DeviceEventType.Initialized, PaymentType.Cash, 0, null, "Initialized"));
        }

        private async Task PollLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // Poll bills
                    _device.Write("R,33");
                    var rawBill = _device.Read();
                    if (TryParseBill(rawBill) is int billAmt)
                    {
                        _escrowDecision = new TaskCompletionSource<bool>();
                        DeviceEvent?.Invoke(this, new DeviceEventArgs(DeviceEventType.CashEscrowRequested, PaymentType.Cash, billAmt));

                        var timeout = Task.Delay(TimeSpan.FromSeconds(5), token);
                        var completed = await Task.WhenAny(_escrowDecision.Task, timeout);
                        bool accepted = completed == _escrowDecision.Task && _escrowDecision.Task.Result;
                        if (completed != _escrowDecision.Task)
                            DeviceEvent?.Invoke(this, new DeviceEventArgs(DeviceEventType.Error, PaymentType.Cash, billAmt, false, "Escrow timeout, returning"));

                        _escrowDecision = null;
                        _device.Write($"R,35,{(accepted ? 1 : 0)}");
                        _device.Read();
                        DeviceEvent?.Invoke(this, new DeviceEventArgs(DeviceEventType.CashProcessed, PaymentType.Cash, billAmt, accepted));
                    }

                    // Poll coins
                    _device.Write("R,0B");
                    var rawCoin = _device.Read();
                    if (TryParseCoin(rawCoin) is int coinAmt)
                    {
                        DeviceEvent?.Invoke(this, new DeviceEventArgs(DeviceEventType.CoinReceived, PaymentType.Coin, coinAmt));
                        DeviceEvent?.Invoke(this, new DeviceEventArgs(DeviceEventType.CoinProcessed, PaymentType.Coin, coinAmt, true));
                    }
                }
                catch (Exception ex)
                {
                    EmitError(ex.Message);
                }
                await Task.Delay(200, token);
            }
        }

        private void EmitError(string msg)
        {
            DeviceEvent?.Invoke(this, new DeviceEventArgs(DeviceEventType.Error, PaymentType.Cash, 0, null, msg));
        }

        private int? TryParseBill(string data)
        {
            if (string.IsNullOrEmpty(data) || data.StartsWith("p,ACK")) return null;
            try
            {
                int v = Convert.ToInt32(data.Replace("p,", string.Empty), 16);
                int r = (v & 0xF0) >> 4;
                int t = v & 0x0F;
                if (r == 9 && t < _billValues.Length) return _billValues[t];
            }
            catch (Exception ex) { EmitError("ParseBill: " + ex.Message); }
            return null;
        }

        private int? TryParseCoin(string data)
        {
            if (string.IsNullOrEmpty(data) || data.StartsWith("p,ACK")) return null;
            try
            {
                // MDB: p,HHLL (hex)
                Console.WriteLine(data);
                int raw = Convert.ToInt32(data.Replace("p,", string.Empty), 16);
                int high = (raw >> 8) & 0xFF;
                int routing = (high & 0xC0) >> 6;
                int type = high & 0x3F;
                if (routing == 0 && type < _coinValues.Length) return _coinValues[type];
            }
            catch (Exception ex) { EmitError("ParseCoin: " + ex.Message); }
            return null;
        }
    }
}
