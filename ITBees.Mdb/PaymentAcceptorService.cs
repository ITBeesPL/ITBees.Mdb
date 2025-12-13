using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ITBees.Interfaces.Logs;
using ITBees.Mdb.CashInventory;
using Microsoft.Extensions.Logging;

namespace ITBees.Mdb
{
    public class PaymentAcceptorService : IPaymentAcceptorService
    {
        private readonly ISerialDevice _device;
        private readonly ILogger<PaymentAcceptorService> _logger;
        private readonly ILiveLogListener _liveLogger;
        private readonly ICashInventoryService _cashInventoryService;
        private readonly object _ioLock = new();
        private volatile bool _payoutBusy;

        private readonly object _dispenseWaitersLock = new();
        private readonly Dictionary<int, TaskCompletionSource<bool>> _dispenseWaiters = new();

        private readonly int[] _billValues = { 1000, 2000, 5000, 10000, 20000, 50000 };

        private readonly Dictionary<int, int> _coinTypeToValue = new();
        private readonly Dictionary<int, int> _coinValueToType = new();

        private int _coinScalingFactor = 1;
        private int _coinDecimalPlaces = 2;

        private CancellationTokenSource _cts;
        private TaskCompletionSource<bool> _escrowDecision;
        private volatile bool _cashlessBusy;
        private bool _deviceRunnig;
        private bool _debugVerboseLogging;

        public event EventHandler<DeviceEventArgs>? DeviceEvent;

        public PaymentAcceptorService(
            ISerialDevice device,
            ILogger<PaymentAcceptorService> logger,
            ILiveLogListener liveLogger,
            ICashInventoryService cashInventoryService)
        {
            _device = device;
            _logger = logger;
            _liveLogger = liveLogger;
            _cashInventoryService = cashInventoryService;
        }

        public void Start(string port)
        {
            Prepare(port);
            _ = PollLoop();
            _deviceRunnig = true;
        }

        public void Stop()
        {
            _cts?.Cancel();
            try
            {
                _liveLogger.LogMessage("Shutting down MDB device...").Wait();
                _device.Write("M,0");
                ReadLineLogged();
                _device.Close();
                _deviceRunnig = false;
            }
            catch
            {
                _liveLogger.LogMessage("Shutting down MDB device error").Wait();
            }
        }

        private void Prepare(string port)
        {
            _device.PrepareSerialPortDevice(port, 115200, 1000);
            InitDevices();
            _cts = new CancellationTokenSource();
        }

        private void InitDevices()
        {
            _liveLogger.LogMessage("Init MDB Device started...").Wait();
            _device.Write("M,1");
            ReadLineLogged();

            foreach (var cmd in new[] { "R,30", "R,31", "R,34,FFFFFFFF", "R,35,0" })
            {
                _device.Write(cmd);
                ReadLineLogged();
            }

            foreach (var cmd in new[] { "R,08", "R,09", "R,0C,FFFFFFFF" })
            {
                _device.Write(cmd);
                var line = ReadLineLogged();
                if (cmd == "R,09")
                {
                    LoadCoinConfiguration(line);
                }
            }

            DeviceEvent?.Invoke(this,
                new DeviceEventArgs(DeviceEventType.Initialized,
                    PaymentType.Cash, 0, null, "Initialized"));
        }

        private async Task PollLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (!_cashlessBusy && !_payoutBusy)
                    {
                        string bills;
                        string coins;

                        lock (_ioLock)
                        {
                            _device.Write("R,33");
                            bills = ReadLineLogged("PollBills");

                            _device.Write("R,0B");
                            coins = ReadLineLogged("PollCoins");
                        }

                        HandleBills(bills);
                        await HandleCoinsAsync(coins);
                    }
                }
                catch (Exception ex)
                {
                    EmitError(ex.Message);
                    await _liveLogger.LogErrorMessage(
                        "Error on PollLoop, message " + ex.Message);
                }

                await Task.Delay(200, _cts.Token);
            }
        }

        private async void HandleBills(string data)
        {
            if (TryParseBill(data) is int amt)
            {
                await HandleBillAsync(amt, _cts.Token);
            }
        }

        private async Task HandleBillAsync(int amt, CancellationToken token)
        {
            await _liveLogger.LogMessage("Handle bill, amount :" + amt);
            _escrowDecision = new TaskCompletionSource<bool>();
            DeviceEvent?.Invoke(this,
                new DeviceEventArgs(DeviceEventType.CashEscrowRequested,
                    PaymentType.Cash, amt));

            var timeout = Task.Delay(TimeSpan.FromSeconds(5), token);
            var finished = await Task.WhenAny(_escrowDecision.Task, timeout);

            bool accept = finished == _escrowDecision.Task && _escrowDecision.Task.Result;
            if (finished != _escrowDecision.Task)
            {
                EmitError("Escrow timeout – returning note");
                await _liveLogger.LogErrorMessage("Escrow timeout – returning note");
            }

            _device.Write($"R,35,{(accept ? 1 : 0)}");
            ReadLineLogged();
            await _liveLogger.LogMessage($"Bill {amt}" + (accept ? "accepted" : "returned"));

            if (accept)
            {
                await _cashInventoryService.RegisterBanknoteAcceptedAsync(amt);
                await _cashInventoryService.FlushAsync();
            }

            DeviceEvent?.Invoke(this,
                new DeviceEventArgs(
                    DeviceEventType.CashProcessed,
                    PaymentType.Cash,
                    amt,
                    accept));
        }

        private static readonly Regex _frame4 = new(@"[0-9A-Fa-f]{4}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private async Task HandleCoinsAsync(string data)
        {
            if (string.IsNullOrEmpty(data) || !data.StartsWith("p,")) return;

            string hex = new string(data.Substring(2)
                .Where(c => IsHex(c))
                .ToArray());

            foreach (Match m in _frame4.Matches(hex))
                await ProcessSingleCoinFrameAsync(m.Value).ConfigureAwait(false);
        }

        private async Task ProcessSingleCoinFrameAsync(string frame)
        {
            if (string.IsNullOrWhiteSpace(frame) || frame.Length != 4)
                return;

            byte b1 = Convert.ToByte(frame.Substring(0, 2), 16);

            int highNibble = (b1 >> 4) & 0x0F;
            int coinType = b1 & 0x0F;

            CoinRoute route;
            switch (highNibble)
            {
                case 0x4: route = CoinRoute.ToCashbox; break;
                case 0x5: route = CoinRoute.ToTube; break;
                case 0x9: route = CoinRoute.Dispensed; break;
                default:
                    _logger.LogInformation(
                        "[MDB COIN] unsupported/unknown highNibble={HighNibble:X}, coinType={CoinType}, frame={Frame}",
                        highNibble, coinType, frame);
                    return;
            }

            if (!_coinTypeToValue.TryGetValue(coinType, out var amountInCents))
            {
                _logger.LogWarning("[MDB COIN] unknown coin type={CoinType}, frame={Frame}", coinType, frame);
                return;
            }

            switch (route)
            {
                case CoinRoute.ToTube:
                {
                    await _cashInventoryService.RegisterCoinAcceptedAsync(amountInCents).ConfigureAwait(false);
                    await _cashInventoryService.FlushAsync().ConfigureAwait(false);

                    DeviceEvent?.Invoke(this, new DeviceEventArgs(
                        DeviceEventType.CoinReceived,
                        PaymentType.Coin,
                        amountInCents,
                        targetCashHolder: DeviceEventType.CoinReceived));

                    break;
                }

                case CoinRoute.Dispensed:
                {
                    await _cashInventoryService.RegisterCoinDispensedAsync(amountInCents).ConfigureAwait(false);
                    await _cashInventoryService.FlushAsync().ConfigureAwait(false);

                    DeviceEvent?.Invoke(this, new DeviceEventArgs(
                        DeviceEventType.CoinDispensed,
                        PaymentType.Coin,
                        amountInCents,
                        targetCashHolder: DeviceEventType.CoinDispensed));

                    TaskCompletionSource<bool>? tcs = null;
                    lock (_dispenseWaitersLock)
                    {
                        if (_dispenseWaiters.TryGetValue(amountInCents, out tcs))
                        {
                            _dispenseWaiters.Remove(amountInCents);
                        }
                    }
                    tcs?.TrySetResult(true);

                    break;
                }

                case CoinRoute.ToCashbox:
                {
                    await _cashInventoryService.RegisterCoinToCashboxAcceptedAsync(amountInCents).ConfigureAwait(false);
                    await _cashInventoryService.FlushAsync().ConfigureAwait(false);

                    DeviceEvent?.Invoke(this, new DeviceEventArgs(
                        DeviceEventType.CoinToCashbox,
                        PaymentType.Coin,
                        amountInCents,
                        targetCashHolder: DeviceEventType.CoinToCashbox));

                    break;
                }
            }
        }

        public void ResetDeviceCoinState()
        {
            try
            {
                _device.Write("R,08");
                ReadLineLogged();

                _device.Write("R,09");
                ReadLineLogged();

                _device.Write("R,0C,FFFFFFFF");
                ReadLineLogged();

                _logger.LogInformation("MDB coin device reset/reinitialized after emptying tubes.");
            }
            catch (Exception ex)
            {
                EmitError("Error while resetting MDB coin device: " + ex.Message);
            }
        }

        public void Accept() => _escrowDecision?.TrySetResult(true);
        public void Return() => _escrowDecision?.TrySetResult(false);

        public async Task<bool> DispenseChangeAsync(int amount)
        {
            _liveLogger.LogMessage("Dispensing change: " + amount + " gr").Wait();
            _payoutBusy = true;
            try
            {
                Dictionary<int, int> tubeMap;
                lock (_ioLock)
                {
                    _device.Write("R,0A");
                    string response = ReadLineLogged("TubeStatus");
                    tubeMap = ParseTubeStatus(response);
                }

                if (tubeMap == null || tubeMap.Count == 0)
                {
                    EmitError($"Nie udało się pobrać stanu tub przy próbie wydania reszty: {amount} gr");
                    _liveLogger.LogErrorMessage("Failed to get tube status when dispensing change").Wait();
                    return false;
                }

                int[] sortedValues = tubeMap.Keys.OrderByDescending(v => v).ToArray();

                var toDispense = new Dictionary<int, int>();
                int remaining = amount;

                foreach (int coinValue in sortedValues)
                {
                    _liveLogger.LogMessage("Considering coin value: " + coinValue + " gr").Wait();
                    if (remaining <= 0)
                        break;

                    tubeMap.TryGetValue(coinValue, out int availableCount);

                    if (availableCount <= 0)
                    {
                        toDispense[coinValue] = 0;
                        continue;
                    }

                    int needed = remaining / coinValue;
                    int use = Math.Min(needed, availableCount);

                    toDispense[coinValue] = use;
                    remaining -= use * coinValue;
                }

                if (remaining > 0)
                {
                    _liveLogger.LogErrorMessage(
                        $"Cannot make exact change: remaining={remaining} gr, requested={amount} gr").Wait();
                    return false;
                }

                foreach (var kv in toDispense)
                {
                    int coinValue = kv.Key;
                    int countToDispense = kv.Value;

                    for (int i = 0; i < countToDispense; i++)
                    {
                        _liveLogger.LogMessage($"Dispensing coin {coinValue} gr").Wait();

                        bool ok = await DispenseCoinAsync(coinValue);

                        await Task.Delay(200);

                        if (!ok)
                        {
                            _liveLogger.LogErrorMessage(
                                    $"Failed to dispense coin {coinValue} gr, aborting change dispense, remaining amount: {remaining} gr, initial amount: {amount} gr")
                                .Wait();
                            EmitError($"Błąd przy wypłacie monety {coinValue} gr");
                            return false;
                        }
                    }
                }

                return true;
            }
            finally
            {
                lock (_dispenseWaitersLock)
                {
                    foreach (var kv in _dispenseWaiters.Values)
                        kv.TrySetResult(false);
                    _dispenseWaiters.Clear();
                }
                _payoutBusy = false;
            }
        }

        public bool DeviceRunning()
        {
            return _deviceRunnig;
        }

        public void EnableVerboseDebugLogging(bool enable)
        {
            _debugVerboseLogging = enable;
        }

        public async Task<bool> DispenseCoinAsync(int value)
        {
            if (!_coinValueToType.TryGetValue(value, out var coinType))
            {
                _logger.LogWarning("DispenseCoin: unknown coin value {Value} gr", value);
                return false;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_dispenseWaitersLock)
            {
                _dispenseWaiters[value] = tcs;
            }

            byte param = (byte)(0x10 | (coinType & 0x0F));

            string line;
            lock (_ioLock)
            {
                _liveLogger.LogMessage(
                    $"Sending payout for {value} gr (coinType={coinType}, param=0x{param:X2})").Wait();

                _device.Write($"R,0D,{param:X2}");
                line = ReadLineLogged("DispenseCoin");
            }

            if (!line.StartsWith("p,ACK", StringComparison.OrdinalIgnoreCase))
            {
                lock (_dispenseWaitersLock) { _dispenseWaiters.Remove(value); }
                _logger.LogWarning("DispenseCoin failed for {Value} gr", value);
                return false;
            }

            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                if (tcs.Task.IsCompleted)
                    break;

                string coins;
                lock (_ioLock)
                {
                    _device.Write("R,0B");
                    coins = ReadLineLogged("PollCoinsAfterPayout");
                }

                await HandleCoinsAsync(coins).ConfigureAwait(false);

                if (tcs.Task.IsCompleted)
                    break;

                await Task.Delay(80).ConfigureAwait(false);
            }

            bool ok = false;
            if (tcs.Task.IsCompleted)
                ok = await tcs.Task.ConfigureAwait(false);

            if (!ok)
            {
                lock (_dispenseWaitersLock) { _dispenseWaiters.Remove(value); }
            }

            return ok;
        }

        public void ShowTubeStatus()
        {
            _liveLogger.LogMessage("Requesting tube status...").Wait();
            _device.Write("R,0A");
            var map = ParseTubeStatus(ReadLineLogged());
            _liveLogger.LogMessage("Tube status:").Wait();
            foreach (var kv in map)
            {
                _liveLogger.LogMessage($"  {kv.Key} gr: {kv.Value}").Wait();
            }
        }

        public async Task<bool> StartSigmaPaymentAsync(int amountCents,
            CancellationToken ct = default)
        {
            if (_cashlessBusy) return false;
            _cashlessBusy = true;
            try
            {
                WriteDbg("C,64,02");
                if (!ReadAck()) return Fail("ENABLE no ACK");
                await Task.Delay(300, ct);

                WriteDbg("C,60");

                var sw = Stopwatch.StartNew();
                const int timeoutMs = 5000;
                bool seenReset = false;
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    await Task.Delay(100, ct);
                    WriteDbg("C,62");
                    string rsp = ReadLineLogged();
                    if (rsp.StartsWith("d,STATUS,RESET",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        seenReset = true;
                        break;
                    }
                }

                if (!seenReset)
                    return Fail("RESET no ACK (timeout waiting for STATUS,RESET)");

                WriteDbg("C,61");
                string setup = ReadNextNonAck(_device);
                byte decimals = 2;
                if (setup.StartsWith("p,", StringComparison.OrdinalIgnoreCase))
                {
                    var b = AsHexBytes(setup.Substring(2));
                    if (b.Length >= 7) decimals = b[6];
                }

                SendDisplayText($"Product {amountCents / 100.0:0.00} PLN");

                uint scaled = (uint)(amountCents / Math.Pow(10, decimals));
                byte hi = (byte)(scaled >> 8), lo = (byte)scaled;
                WriteDbg($"C,63,{hi:X2},{lo:X2}");
                if (!ReadAck())
                    return Fail("VEND REQUEST no ACK");

                DeviceEvent?.Invoke(this,
                    new DeviceEventArgs(DeviceEventType.CashlessSessionStarted,
                        PaymentType.Card, amountCents));

                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    WriteDbg("C,62");
                    string poll = ReadLineLogged();
                    if (TryParseCashlessPoll(poll, out bool approved, out bool finished))
                    {
                        if (approved)
                        {
                            DeviceEvent?.Invoke(this,
                                new DeviceEventArgs(DeviceEventType.CashlessVendApproved,
                                    PaymentType.Card, amountCents, true));
                            return true;
                        }

                        if (finished)
                        {
                            DeviceEvent?.Invoke(this,
                                new DeviceEventArgs(DeviceEventType.CashlessVendDenied,
                                    PaymentType.Card, amountCents, false));
                            return false;
                        }
                    }

                    await Task.Delay(200, ct);
                }

                EmitError("Cashless: approval timeout");
                return false;
            }
            catch (Exception ex)
            {
                EmitError("StartSigmaPayment: " + ex.Message);
                return false;
            }
            finally
            {
                _cashlessBusy = false;
            }
        }

        private void WriteDbg(string cmd)
        {
            Console.WriteLine($"TX » {cmd}");
            _device.Write(cmd);
        }

        private string ReadLineLogged(string context = null)
        {
            string s = _device.Read();
            if (_debugVerboseLogging)
            {
                if (!string.IsNullOrEmpty(context))
                    _logger.LogInformation("[MDB RX:{Context}] {Line}", context, s);
                else
                    _logger.LogInformation("[MDB RX] {Line}", s);
            }

            return s;
        }

        private bool ReadAck(int retries = 5)
        {
            for (int i = 0; i < retries; i++)
            {
                string line = ReadLineLogged();
                if (line.StartsWith("p,ACK", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static string ReadNextNonAck(ISerialDevice dev)
        {
            string s;
            do
            {
                s = dev.Read();
            } while (s.StartsWith("p,ACK", StringComparison.OrdinalIgnoreCase) ||
                     string.IsNullOrWhiteSpace(s));

            return s;
        }

        private static byte[] AsHexBytes(string hex)
        {
            hex = new string(hex.Where(IsHex).ToArray());
            if ((hex.Length & 1) == 1)
                hex = hex.Substring(0, hex.Length - 1);

            return Enumerable.Range(0, hex.Length / 2)
                .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
                .ToArray();
        }

        private static bool IsHex(char c) =>
            (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

        private static bool TryParseCashlessPoll(string data, out bool approved, out bool finished)
        {
            approved = finished = false;
            if (string.IsNullOrEmpty(data) || !data.StartsWith("p,")) return false;
            byte code = Convert.ToByte(data.Substring(2, 2), 16);
            if (code == 0x01)
            {
                approved = finished = true;
                return true;
            }

            if (code == 0x02)
            {
                finished = true;
                return true;
            }

            return false;
        }

        private void SendDisplayText(string text)
        {
            if (text.Length > 32) text = text.Substring(0, 32);
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            byte[] frame = new byte[utf8.Length + 3];
            frame[0] = 0x65;
            frame[1] = (byte)(utf8.Length + 1);
            frame[2] = 0x06;
            Buffer.BlockCopy(utf8, 0, frame, 3, utf8.Length);

            string cmd = "R," + string.Join(',', frame.Select(b => b.ToString("X2")));
            WriteDbg(cmd);
            ReadAck();
        }

        private void EmitError(string m) =>
            DeviceEvent?.Invoke(this,
                new DeviceEventArgs(DeviceEventType.Error, PaymentType.Cash, 0, null, m));

        private bool Fail(string msg)
        {
            EmitError(msg);
            return false;
        }

        private int? TryParseBill(string line)
        {
            if (string.IsNullOrEmpty(line) || line.StartsWith("p,ACK")) return null;
            string hex = line.Replace("p,", string.Empty);
            if (hex.Length != 2) return null;
            try
            {
                int v = Convert.ToInt32(hex, 16);
                int route = (v & 0xF0) >> 4;
                int type = v & 0x0F;
                if (route == 9 && type < _billValues.Length)
                    return _billValues[type];
            }
            catch (Exception ex)
            {
                EmitError("ParseBill: " + ex.Message);
            }

            return null;
        }

        private Dictionary<int, int> ParseTubeStatus(string data)
        {
            var map = new Dictionary<int, int>();
            if (string.IsNullOrEmpty(data)) return map;

            string hex = data.StartsWith("p,") ? data.Substring(2) : data;
            hex = new string(hex.Where(IsHex).ToArray());
            byte[] bytes = AsHexBytes(hex);

            if (bytes.Length < 3)
                return map;

            int countBytes = Math.Min(16, bytes.Length - 2);

            for (int coinType = 0; coinType < countBytes; coinType++)
            {
                byte count = bytes[2 + coinType];
                if (count == 0) continue;

                if (_coinTypeToValue.TryGetValue(coinType, out var valueInCents))
                {
                    map[valueInCents] = count;
                }
            }

            return map;
        }

        private void LoadCoinConfiguration(string line)
        {
            try
            {
                string hex = line.StartsWith("p,") ? line.Substring(2) : line;
                hex = new string(hex.Where(IsHex).ToArray());
                var bytes = AsHexBytes(hex);

                if (bytes.Length < 8)
                {
                    _logger.LogWarning("COIN TYPE response too short: {Len}", bytes.Length);
                    _liveLogger.LogErrorMessage("COIN TYPE response too short").Wait();
                    return;
                }

                _coinTypeToValue.Clear();
                _coinValueToType.Clear();

                _coinScalingFactor = bytes.Length > 3 && bytes[3] != 0 ? bytes[3] : 1;
                _coinDecimalPlaces = bytes.Length > 4 ? bytes[4] : 2;

                byte[] credits;
                if (bytes.Length >= 16)
                    credits = bytes.Skip(bytes.Length - 16).Take(16).ToArray();
                else
                    credits = Array.Empty<byte>();

                for (int coinType = 0; coinType < credits.Length; coinType++)
                {
                    byte credit = credits[coinType];
                    if (credit == 0 || credit == 0xFF)
                        continue;

                    int valueInCents = credit * _coinScalingFactor;

                    _coinTypeToValue[coinType] = valueInCents;
                    if (!_coinValueToType.ContainsKey(valueInCents))
                        _coinValueToType[valueInCents] = coinType;
                }

                _liveLogger.LogMessage(
                    $"Loaded COIN TYPE config. Scaling={_coinScalingFactor}, Decimals={_coinDecimalPlaces}, Types={_coinTypeToValue.Count}").Wait();

                foreach (var kv in _coinTypeToValue.OrderBy(x => x.Key))
                    _logger.LogInformation("[MDB COIN] coinType={CoinType} => {Value} gr", kv.Key, kv.Value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load COIN TYPE configuration");
            }
        }

        internal enum CoinRoute : byte
        {
            ToTube,
            Dispensed,
            ToCashbox
        }
    }
}
