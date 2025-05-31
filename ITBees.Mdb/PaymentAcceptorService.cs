using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace ITBees.Mdb
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;

    public class PaymentAcceptorService : IPaymentAcceptorService
    {
        private readonly ISerialDevice _device;
        private readonly int[] _billValues = { 1000, 2000, 5000, 10000, 20000, 50000 };
        private readonly int[] _tubeValues = { 10, 20, 50, 100, 200, 500 };
        private readonly Dictionary<int, int> _coinMap = new()
        {
            { 16,  10 },
            { 17,  20 },
            { 18,  50 },
            { 19, 100 },
            { 20, 200 },
            { 21, 500 }
        };
        private CancellationTokenSource _cts;
        private TaskCompletionSource<bool> _escrowDecision;

        public event EventHandler<DeviceEventArgs>? DeviceEvent;

        public PaymentAcceptorService(ISerialDevice device)
        {
            _device = device;
        }

        public void Start(string portName)
        {
            try
            {
                _device.PrepareSerialPortDevice(portName, 115200, 1000);

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
        internal enum CoinRoute : byte
        {
            ToTube = 0,
            Dispensed = 1,
            ToCashbox = 2
        }

        private async Task PollLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    /* -------------  bills ------------- */
                    _device.Write("R,33");
                    var rawBill = _device.Read();
                    if (TryParseBill(rawBill) is int billAmt)
                        await HandleBillAsync(billAmt, token);

                    /* -------------  coins ------------- */
                    _device.Write("R,0B");
                    var rawCoins = _device.Read();
                    HandleCoins(rawCoins);                     // <-- changed
                }
                catch (Exception ex) { EmitError(ex.Message); }

                await Task.Delay(200, token);
            }
        }

        private async Task HandleBillAsync(int billAmt, CancellationToken token)
        {
            _escrowDecision = new TaskCompletionSource<bool>();
            DeviceEvent?.Invoke(this,
                new DeviceEventArgs(DeviceEventType.CashEscrowRequested, PaymentType.Cash, billAmt));

            var timeout = Task.Delay(TimeSpan.FromSeconds(5), token);
            var completed = await Task.WhenAny(_escrowDecision.Task, timeout);

            bool accepted = completed == _escrowDecision.Task && _escrowDecision.Task.Result;
            if (completed != _escrowDecision.Task)
                DeviceEvent?.Invoke(this,
                    new DeviceEventArgs(DeviceEventType.Error, PaymentType.Cash, billAmt, false,
                        "Escrow timeout, returning"));

            _escrowDecision = null;
            _device.Write($"R,35,{(accepted ? 1 : 0)}");
            _device.Read();
            DeviceEvent?.Invoke(this,
                new DeviceEventArgs(DeviceEventType.CashProcessed, PaymentType.Cash, billAmt, accepted));
        }
        private static readonly Regex _frame4 = new Regex(@"[0-9A-Fa-f]{4}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private void HandleCoins(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;

            // keep only 0-9 / A-F characters, drop everything else (p, CR, LF, commas…)
            string hex = new string(data.Where(ch =>
                    (ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'F') || (ch >= 'a' && ch <= 'f'))
                .ToArray());

            foreach (Match m in _frame4.Matches(hex))
                ProcessSingleCoinFrame(m.Value);
        }


        private void ProcessSingleCoinFrame(string hexFrame)
        {
            try
            {
                int raw = Convert.ToInt32(hexFrame, 16);
                int high = (raw >> 8) & 0xFF;

                int routingBits = (high & 0xC0) >> 6;   // 0,1,2 (3 is reserved)
                int type = high & 0x3F;         // coin type 0-63

                if (!_coinMap.TryGetValue(type, out int amount))
                {
                    // unknown type → log and still raise a generic event if you want
                    EmitError($"Unknown coin type {type} (route={routingBits})");
                    return;
                }

                switch (routingBits)
                {
                    case 0:           // routed to tube
                    case 1:           // routed to cash-box – still customer credit
                        DeviceEvent?.Invoke(this,
                            new DeviceEventArgs(DeviceEventType.CoinReceived,
                                PaymentType.Coin, amount));
                        DeviceEvent?.Invoke(this,
                            new DeviceEventArgs(DeviceEventType.CoinProcessed,
                                PaymentType.Coin, amount, true));
                        break;

                    case 2:           // payout
                        DeviceEvent?.Invoke(this,
                            new DeviceEventArgs(DeviceEventType.CoinDispensed,
                                PaymentType.Coin, amount));
                        break;
                }
            }
            catch (Exception ex) { EmitError("ProcessCoin: " + ex.Message); }
        }

        private void EmitError(string msg)
        {
            DeviceEvent?.Invoke(this, new DeviceEventArgs(DeviceEventType.Error, PaymentType.Cash, 0, null, msg));
        }

        private int? TryParseBill(string data)
        {
            if (string.IsNullOrEmpty(data) || data.StartsWith("p,ACK"))
                return null;

            string hex = data.Replace("p,", string.Empty);
            if (hex.Length != 2)
                return null;

            try
            {
                int v = Convert.ToInt32(hex, 16);
                int route = (v & 0xF0) >> 4;
                int type = v & 0x0F;
                if (route == 9 && type < _billValues.Length)
                    return _billValues[type];
            }
            catch (Exception ex) { EmitError("ParseBill: " + ex.Message); }
            return null;
        }

        private int? TryParseCoin(string data)
        {
            if (string.IsNullOrEmpty(data) || data.StartsWith("p,ACK"))
                return null;

            string hex = data.Replace("p,", string.Empty);
            if (hex.Length != 4) return null;        // not a 2-byte poll frame

            try
            {
                int raw = Convert.ToInt32(hex, 16);
                int high = (raw >> 8) & 0xFF;
                int routing = (high & 0xC0) >> 6;     // 0=tube,1=cashbox,2=dispense
                int type = high & 0x3F;           // 0-63

                // credit is granted for routing 0 or 1
                if ((routing == 0 || routing == 1) && _coinMap.TryGetValue(type, out int amount))
                    return amount;
            }
            catch (Exception ex) { EmitError("ParseCoin: " + ex.Message); }

            return null;
        }

        public Dictionary<int, int> GetCoinTubeStatus()
        {
            try
            {
                _device.Write("R,0A");          // MDB Tube Status (0x0A)
                var resp = _device.Read();      // np. "p,0000A1050302010000..."
                Console.WriteLine(resp);
                return ParseTubeStatus(resp);
            }
            catch (Exception ex)
            {
                EmitError("TubeStatus: " + ex.Message);
                return new Dictionary<int, int>();
            }
        }

        public void ShowTubeStatus()
        {
            var tubes = GetCoinTubeStatus();
            if (tubes.Count == 0)
            {
                Console.WriteLine("Brak danych o tubach");
                return;
            }

            Console.WriteLine("Stan zasobników:");
            foreach (var kv in tubes)
                Console.WriteLine($"  {kv.Key} gr: {kv.Value} szt.");
        }


        public bool DispenseCoin(int coinValue)
        {
            int idx = Array.IndexOf(_tubeValues, coinValue);   // <<<
            if (idx < 0 || idx > 0x0F) return false;

            byte param = (byte)((1 << 4) | idx);
            string cmd = $"R,0D,{param:X2}";
            _device.Write(cmd);
            var resp = _device.Read();
            return resp?.StartsWith("p,ACK", StringComparison.OrdinalIgnoreCase) == true;
        }


        private Dictionary<int, int> ParseTubeStatus(string data)
        {
            var result = new Dictionary<int, int>();
            if (string.IsNullOrEmpty(data) || data.StartsWith("p,ACK")) return result;

            string hex = data.Replace("p,", string.Empty);
            byte[] bytes = Enumerable.Range(0, hex.Length / 2)
                .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
                .ToArray();

            if (bytes.Length < 18) return result;

            for (int i = 0; i < _tubeValues.Length && (2 + i) < bytes.Length; i++)
                result[_tubeValues[i]] = bytes[2 + i];

            return result;
        }
    }
}
