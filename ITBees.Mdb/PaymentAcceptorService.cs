using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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

        private readonly int[] _billValues = { 1000, 2000, 5000, 10000, 20000, 50000 };

        private readonly Dictionary<int, int> _coinTypeToValue = new();
        private readonly Dictionary<int, int> _coinValueToType = new();

        private readonly object _pendingDispenseLock = new();
        private readonly Dictionary<int, (int Count, DateTime ExpiresUtc)> _pendingProgrammaticDispenseByValue = new();

        // Key: coin value (gr), Value: queue of waiters for confirmation from PollCoins (0x9?)
        private readonly object _dispenseWaitersLock = new();
        private readonly Dictionary<int, Queue<TaskCompletionSource<bool>>> _dispenseWaitersByValue = new();

        // TTL must be longer than the "busy/poll jitter" window. 3s is often too short in real life.
        private static readonly TimeSpan _programmaticDispenseTtl = TimeSpan.FromSeconds(10);

        // How long we wait for poll confirmation after payout command.
        private static readonly TimeSpan _dispenseConfirmTimeout = TimeSpan.FromSeconds(2.5);

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
                lock (_ioLock)
                {
                    _device.Write("M,0");
                    ReadLineLogged();
                    _device.Close();
                }

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

            lock (_ioLock)
            {
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
                        LoadCoinConfiguration(line);
                }
            }

            DeviceEvent?.Invoke(this,
                new DeviceEventArgs(DeviceEventType.Initialized, PaymentType.Cash, 0, null, "Initialized"));
        }

        private async Task PollLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
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

                    if (!_cashlessBusy)
                        HandleBills(bills);

                    await HandleCoinsAsync(coins).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    EmitError(ex.Message);
                    await _liveLogger.LogErrorMessage("Error on PollLoop, message " + ex.Message);
                }

                await Task.Delay(200, _cts.Token);
            }
        }

        private async void HandleBills(string data)
        {
            if (TryParseBill(data) is int amt)
                await HandleBillAsync(amt, _cts.Token);
        }

        private async Task HandleBillAsync(int amt, CancellationToken token)
        {
            await _liveLogger.LogMessage("Handle bill, amount :" + amt);
            _escrowDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            DeviceEvent?.Invoke(this,
                new DeviceEventArgs(DeviceEventType.CashEscrowRequested, PaymentType.Cash, amt));

            var timeout = Task.Delay(TimeSpan.FromSeconds(5), token);
            var finished = await Task.WhenAny(_escrowDecision.Task, timeout);

            bool accept = finished == _escrowDecision.Task && _escrowDecision.Task.Result;
            if (finished != _escrowDecision.Task)
            {
                EmitError("Escrow timeout – returning note");
                await _liveLogger.LogErrorMessage("Escrow timeout – returning note");
            }

            lock (_ioLock)
            {
                _device.Write($"R,35,{(accept ? 1 : 0)}");
                ReadLineLogged();
            }

            await _liveLogger.LogMessage($"Bill {amt}" + (accept ? "accepted" : "returned"));

            if (accept)
            {
                await _cashInventoryService.RegisterBanknoteAcceptedAsync(amt);
                await _cashInventoryService.FlushAsync();
            }

            DeviceEvent?.Invoke(this,
                new DeviceEventArgs(DeviceEventType.CashProcessed, PaymentType.Cash, amt, accept));
        }

        // ===== FIX #1: Parse PollCoins byte-by-byte (2 hex chars = 1 byte) =====

        private async Task HandleCoinsAsync(string data)
        {
            if (string.IsNullOrEmpty(data) || !data.StartsWith("p,", StringComparison.OrdinalIgnoreCase))
                return;

            string hex = new string(data.Substring(2).Where(IsHex).ToArray());
            if (hex.Length < 2) return;
            if ((hex.Length & 1) == 1) hex = hex.Substring(0, hex.Length - 1);

            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

            int idx = 0;
            while (idx < bytes.Length)
            {
                // 1-byte statuses (adapter/firmware dependent)
                // Examples seen: 0x02 (payout busy), 0xAC (status)
                if (idx == bytes.Length - 1)
                {
                    LogCoinStatus(bytes[idx]);
                    break;
                }

                byte b1 = bytes[idx];
                byte b2 = bytes[idx + 1];

                int highNibble = (b1 >> 4) & 0x0F;

                // If b1 is clearly a 1-byte status, consume only it.
                // 0x00/0xFF/0x02/0xA* are common.
                if (b1 == 0x00 || b1 == 0xFF || b1 == 0x02 || highNibble == 0xA)
                {
                    LogCoinStatus(b1);
                    idx += 1;
                    continue;
                }

                await ProcessCoinEventPairAsync(b1, b2).ConfigureAwait(false);
                idx += 2;
            }
        }

        private void LogCoinStatus(byte b)
        {
            int highNibble = (b >> 4) & 0x0F;
            int lowNibble = b & 0x0F;

            _logger.LogInformation("[MDB COIN] status byte=0x{Byte:X2} (highNibble=0x{High:X}, lowNibble=0x{Low:X})",
                b, highNibble, lowNibble);
        }

        private async Task ProcessCoinEventPairAsync(byte b1, byte b2)
        {
            int highNibble = (b1 >> 4) & 0x0F;
            int coinType = b1 & 0x0F;

            CoinRoute route;
            switch (highNibble)
            {
                case 0x4: route = CoinRoute.ToCashbox; break;
                case 0x5: route = CoinRoute.ToTube; break;
                case 0x9: route = CoinRoute.Dispensed; break;
                default:
                    _logger.LogInformation("[MDB COIN] ignored event pair b1=0x{B1:X2}, b2=0x{B2:X2}", b1, b2);
                    return;
            }

            if (!_coinTypeToValue.TryGetValue(coinType, out var amountInCents))
            {
                _logger.LogWarning("[MDB COIN] unknown coin type={CoinType}, b1=0x{B1:X2}, b2=0x{B2:X2}", coinType, b1,
                    b2);
                return;
            }

            // b2 is NOT another coin event. It is extra info (tube/count/status depending on fw).
            // Keep it only for diagnostics if you want:
            if (_debugVerboseLogging)
                _logger.LogInformation("[MDB COIN] event route={Route}, value={Value}gr, extra=0x{Extra:X2}", route,
                    amountInCents, b2);

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

                case CoinRoute.Dispensed:
                {
                    bool programmatic = TryConsumeProgrammaticDispense(amountInCents);
                    if (programmatic)
                        CompleteDispenseWaiter(amountInCents);
                    else
                    {
                        await _cashInventoryService.RegisterCoinDispensedAsync(amountInCents).ConfigureAwait(false);
                        await _cashInventoryService.FlushAsync().ConfigureAwait(false);
                    }

                    DeviceEvent?.Invoke(this, new DeviceEventArgs(
                        DeviceEventType.CoinDispensed,
                        PaymentType.Coin,
                        amountInCents,
                        targetCashHolder: DeviceEventType.CoinDispensed));
                    break;
                }
            }
        }


        private async Task ProcessSingleCoinByteAsync(byte b)
        {
            int highNibble = (b >> 4) & 0x0F;
            int coinType = b & 0x0F;

            if (highNibble != 0x4 && highNibble != 0x5 && highNibble != 0x9)
            {
                _logger.LogInformation(
                    "[MDB COIN] ignored byte=0x{Byte:X2} (highNibble=0x{High:X}, coinType=0x{Type:X})",
                    b, highNibble, coinType);
                return;
            }

            if (!_coinTypeToValue.TryGetValue(coinType, out var amountInCents))
            {
                _logger.LogWarning("[MDB COIN] unknown coin type={CoinType}, byte=0x{Byte:X2}", coinType, b);
                return;
            }

            switch (highNibble)
            {
                case 0x5:
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

                case 0x4:
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

                case 0x9:
                {
                    if (!TryConsumeProgrammaticDispense(amountInCents))
                    {
                        await _cashInventoryService.RegisterCoinDispensedAsync(amountInCents).ConfigureAwait(false);
                        await _cashInventoryService.FlushAsync().ConfigureAwait(false);
                    }

                    DeviceEvent?.Invoke(this, new DeviceEventArgs(
                        DeviceEventType.CoinDispensed,
                        PaymentType.Coin,
                        amountInCents,
                        targetCashHolder: DeviceEventType.CoinDispensed));
                    break;
                }
            }
        }

        private async Task ProcessCoinByteAsync(byte b)
        {
            // In MDB, 0x00/0xFF etc can appear as "no event / filler / status" depending on adapter.
            // We only handle known routes (high nibble).
            int highNibble = (b >> 4) & 0x0F;
            int coinType = b & 0x0F;

            CoinRoute route;
            switch (highNibble)
            {
                case 0x4: route = CoinRoute.ToCashbox; break;
                case 0x5: route = CoinRoute.ToTube; break;
                case 0x9: route = CoinRoute.Dispensed; break;

                default:
                    if (_debugVerboseLogging)
                    {
                        _logger.LogInformation(
                            "[MDB COIN] ignored byte=0x{Byte:X2} (highNibble=0x{High:X}, coinType=0x{Type:X})",
                            b, highNibble, coinType);
                    }

                    return;
            }

            if (!_coinTypeToValue.TryGetValue(coinType, out var amountInCents))
            {
                _logger.LogWarning("[MDB COIN] unknown coin type={CoinType} for byte=0x{Byte:X2}", coinType, b);
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

                case CoinRoute.Dispensed:
                {
                    // If this was a programmatic payout, wake a waiter.
                    bool isProgrammatic = TryConsumeProgrammaticDispense(amountInCents);
                    if (isProgrammatic)
                    {
                        CompleteDispenseWaiter(amountInCents);
                    }
                    else
                    {
                        // Manual payout (buttons A-F) or external action.
                        // Keep inventory consistent, but DO NOT treat it as confirmation for an in-flight payout.
                        await _cashInventoryService.RegisterCoinDispensedAsync(amountInCents).ConfigureAwait(false);
                        await _cashInventoryService.FlushAsync().ConfigureAwait(false);
                    }

                    DeviceEvent?.Invoke(this, new DeviceEventArgs(
                        DeviceEventType.CoinDispensed,
                        PaymentType.Coin,
                        amountInCents,
                        targetCashHolder: DeviceEventType.CoinDispensed));
                    break;
                }
            }
        }

        // ===== FIX #2: Programmatic dispense must wait for PollCoins confirmation (0x9?) =====

        public Task<bool> DispenseCoinAsync(int value) => DispenseCoinInternalAsync(value, flushAfter: true);

        private async Task<bool> DispenseCoinInternalAsync(int value, bool flushAfter)
        {
            if (!_coinValueToType.TryGetValue(value, out var coinType))
            {
                _logger.LogWarning("DispenseCoin: unknown coin value {Value} gr", value);
                return false;
            }

            // Waiter zostawiamy (jeśli jednak 0x9? przyjdzie, to będzie szybciej)
            var waiter = EnqueueDispenseWaiter(value);

            // Mark pending BEFORE send
            MarkProgrammaticDispense(value);

            byte param = (byte)(0x10 | (coinType & 0x0F));

            string line;
            lock (_ioLock)
            {
                _liveLogger.LogMessage($"Sending payout for {value} gr (coinType={coinType}, param=0x{param:X2})")
                    .Wait();
                _device.Write($"R,0D,{param:X2}");
                line = ReadLineLogged("DispenseCoin");
            }

            if (!line.StartsWith("p,ACK", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("DispenseCoin failed for value={Value} gr, deviceResponse={Response}", value, line);
                FailDispenseWaiter(value, waiter);
                return false;
            }

            // 1) Najpierw spróbuj klasycznie: 0x9? event (jeśli przyjdzie)
            var eventTask = waiter.Task;
            var eventTimeout = Task.Delay(TimeSpan.FromMilliseconds(350));

            var first = await Task.WhenAny(eventTask, eventTimeout).ConfigureAwait(false);
            if (first == eventTask && eventTask.Result)
                goto CONFIRMED;

            // 2) Jeśli event nie przyszedł: potwierdź przez "Payout Busy" w poll
            //    Busy może nie wystąpić zawsze, więc logika jest:
            //    - czekamy aż zobaczymy busy (opcjonalnie),
            //    - potem czekamy aż busy zniknie.
            bool sawBusy = false;
            int notBusyStreak = 0;
            var deadline = DateTime.UtcNow.AddSeconds(3.0); // w praktyce możesz dać 4-5s

            while (DateTime.UtcNow < deadline)
            {
                string poll;
                lock (_ioLock)
                {
                    _device.Write("R,0B");
                    poll = ReadLineLogged("PollCoins(confirm)");
                }

                bool busy = IsPayoutBusyPoll(poll);
                if (busy)
                {
                    sawBusy = true;
                    notBusyStreak = 0;
                }
                else
                {
                    // Jeżeli widzieliśmy busy, to 2 kolejne "not busy" uznajemy za zakończenie cyklu.
                    if (sawBusy)
                    {
                        notBusyStreak++;
                        if (notBusyStreak >= 2)
                            goto CONFIRMED;
                    }
                }

                await Task.Delay(120).ConfigureAwait(false);
            }

            _logger.LogWarning(
                "DispenseCoin NOT confirmed (no 0x9? and payout busy did not complete) for value={Value} gr", value);
            return false;

            CONFIRMED:
            await _cashInventoryService.RegisterCoinDispensedAsync(value).ConfigureAwait(false);
            if (flushAfter)
                await _cashInventoryService.FlushAsync().ConfigureAwait(false);

            DeviceEvent?.Invoke(this, new DeviceEventArgs(DeviceEventType.CoinDispensed, PaymentType.Coin, value));
            return true;
        }


        private TaskCompletionSource<bool> EnqueueDispenseWaiter(int valueInCents)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_dispenseWaitersLock)
            {
                if (!_dispenseWaitersByValue.TryGetValue(valueInCents, out var q))
                {
                    q = new Queue<TaskCompletionSource<bool>>();
                    _dispenseWaitersByValue[valueInCents] = q;
                }

                q.Enqueue(tcs);
            }

            return tcs;
        }

        private void CompleteDispenseWaiter(int valueInCents)
        {
            TaskCompletionSource<bool>? tcs = null;
            lock (_dispenseWaitersLock)
            {
                if (_dispenseWaitersByValue.TryGetValue(valueInCents, out var q) && q.Count > 0)
                {
                    tcs = q.Dequeue();
                    if (q.Count == 0)
                        _dispenseWaitersByValue.Remove(valueInCents);
                }
            }

            tcs?.TrySetResult(true);
        }

        private void FailDispenseWaiter(int valueInCents, TaskCompletionSource<bool> waiter)
        {
            // Best effort: remove the exact waiter if still queued.
            lock (_dispenseWaitersLock)
            {
                if (_dispenseWaitersByValue.TryGetValue(valueInCents, out var q) && q.Count > 0)
                {
                    if (q.Contains(waiter))
                    {
                        var list = q.ToList();
                        list.Remove(waiter);
                        if (list.Count == 0) _dispenseWaitersByValue.Remove(valueInCents);
                        else _dispenseWaitersByValue[valueInCents] = new Queue<TaskCompletionSource<bool>>(list);
                    }
                }
            }

            waiter.TrySetResult(false);
        }

        private async Task<bool> WaitForDispenseConfirmationAsync(TaskCompletionSource<bool> waiter)
        {
            var timeoutTask = Task.Delay(_dispenseConfirmTimeout);
            var completed = await Task.WhenAny(waiter.Task, timeoutTask).ConfigureAwait(false);
            if (completed != waiter.Task)
                return false;

            return waiter.Task.Result;
        }

        private void MarkProgrammaticDispense(int valueInCents)
        {
            var now = DateTime.UtcNow;
            var exp = now.Add(_programmaticDispenseTtl);

            lock (_pendingDispenseLock)
            {
                CleanupExpiredProgrammaticDispenses_NoLock(now);

                if (_pendingProgrammaticDispenseByValue.TryGetValue(valueInCents, out var cur))
                    _pendingProgrammaticDispenseByValue[valueInCents] = (cur.Count + 1, exp);
                else
                    _pendingProgrammaticDispenseByValue[valueInCents] = (1, exp);
            }
        }

        private bool TryConsumeProgrammaticDispense(int valueInCents)
        {
            var now = DateTime.UtcNow;

            lock (_pendingDispenseLock)
            {
                CleanupExpiredProgrammaticDispenses_NoLock(now);

                if (!_pendingProgrammaticDispenseByValue.TryGetValue(valueInCents, out var cur))
                    return false;

                if (cur.Count <= 1)
                    _pendingProgrammaticDispenseByValue.Remove(valueInCents);
                else
                    _pendingProgrammaticDispenseByValue[valueInCents] = (cur.Count - 1, cur.ExpiresUtc);

                return true;
            }
        }

        private void CleanupExpiredProgrammaticDispenses_NoLock(DateTime nowUtc)
        {
            if (_pendingProgrammaticDispenseByValue.Count == 0)
                return;

            var toRemove = new List<int>();
            foreach (var kv in _pendingProgrammaticDispenseByValue)
            {
                if (kv.Value.ExpiresUtc <= nowUtc)
                    toRemove.Add(kv.Key);
            }

            foreach (var k in toRemove)
                _pendingProgrammaticDispenseByValue.Remove(k);
        }

        public void ResetDeviceCoinState()
        {
            try
            {
                lock (_ioLock)
                {
                    _device.Write("R,08");
                    ReadLineLogged();

                    _device.Write("R,09");
                    ReadLineLogged();

                    _device.Write("R,0C,FFFFFFFF");
                    ReadLineLogged();
                }

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
                if (remaining <= 0) break;

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
                    bool ok = await DispenseCoinInternalAsync(coinValue, flushAfter: false).ConfigureAwait(false);
                    await Task.Delay(150).ConfigureAwait(false);

                    if (!ok)
                    {
                        _liveLogger.LogErrorMessage(
                                $"Failed to dispense coin {coinValue} gr, aborting change dispense, initial amount: {amount} gr")
                            .Wait();
                        EmitError($"Błąd przy wypłacie monety {coinValue} gr");
                        return false;
                    }
                }
            }

            await _cashInventoryService.FlushAsync().ConfigureAwait(false);
            return true;
        }

        public bool DeviceRunning() => _deviceRunnig;
        public void EnableVerboseDebugLogging(bool enable) => _debugVerboseLogging = enable;

        // ---- rest of your class unchanged ----

        private void WriteDbg(string cmd)
        {
            Console.WriteLine($"TX » {cmd}");
            lock (_ioLock) _device.Write(cmd);
        }

        private string ReadLineLogged(string context = null)
        {
            string s;
            lock (_ioLock) s = _device.Read();

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
            DeviceEvent?.Invoke(this, new DeviceEventArgs(DeviceEventType.Error, PaymentType.Cash, 0, null, m));

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
                    map[valueInCents] = count;
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

                _coinScalingFactor = bytes[3] == 0 ? 1 : bytes[3];
                _coinDecimalPlaces = bytes[4];

                for (int coinType = 0; coinType < 16 && (7 + coinType) < bytes.Length; coinType++)
                {
                    byte credit = bytes[7 + coinType];
                    if (credit == 0 || credit == 0xFF)
                        continue;

                    int valueInCents = credit * _coinScalingFactor;

                    _coinTypeToValue[coinType] = valueInCents;
                    if (!_coinValueToType.ContainsKey(valueInCents))
                        _coinValueToType[valueInCents] = coinType;
                }

                _liveLogger.LogMessage(
                        $"Loaded COIN TYPE config. Scaling={_coinScalingFactor}, Decimals={_coinDecimalPlaces}, Types={_coinTypeToValue.Count}")
                    .Wait();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load COIN TYPE configuration");
            }
        }

        private bool IsPayoutBusyPoll(string pollLine)
        {
            if (string.IsNullOrEmpty(pollLine) || !pollLine.StartsWith("p,", StringComparison.OrdinalIgnoreCase))
                return false;

            string hex = new string(pollLine.Substring(2).Where(IsHex).ToArray());
            if (hex.Length < 2) return false;
            if ((hex.Length & 1) == 1) hex = hex.Substring(0, hex.Length - 1);

            for (int i = 0; i < hex.Length; i += 2)
            {
                byte b = Convert.ToByte(hex.Substring(i, 2), 16);
                if (b == 0x02) // Changer Payout Busy
                    return true;
            }

            return false;
        }


        internal enum CoinRoute : byte
        {
            ToTube,
            Dispensed,
            ToCashbox
        }

        // ===== Cashless part unchanged (omitted for brevity in this paste) =====
        // Keep your StartSigmaPaymentAsync etc. as-is.
    }
}