using System.Text;
using System.Text.RegularExpressions;

namespace ITBees.Mdb
{
    public class PaymentAcceptorService : IPaymentAcceptorService
    {
        private readonly ISerialDevice _device;

        /* ---------- static tables ---------------------------------- */
        private readonly int[] _billValues = { 1000, 2000, 5000, 10000, 20000, 50000 };
        private readonly int[] _tubeValues = { 10, 20, 50, 100, 200, 500 };

        private readonly Dictionary<int, int> _coinMap = new()
        {
            { 16, 10 }, { 17, 20 }, { 18, 50 },
            { 19, 100 }, { 20, 200 }, { 21, 500 }
        };

        /* ---------- runtime fields --------------------------------- */
        private CancellationTokenSource _cts;
        private TaskCompletionSource<bool> _escrowDecision;
        private volatile bool _cashlessBusy;

        public event EventHandler<DeviceEventArgs>? DeviceEvent;

        public PaymentAcceptorService(ISerialDevice device) => _device = device;

        /* ============================================================ */
        /*  LIFECYCLE                                                   */
        /* ============================================================ */
        public void Start(string port)
        {
            Prepare(port);
            _ = PollLoop();
        }

        public void Stop()
        {
            _cts?.Cancel();
            try
            {
                _device.Write("M,0");
                ReadLineLogged();
            }
            catch
            {
            }

            _device.Close();
        }

        private void Prepare(string port)
        {
            _device.PrepareSerialPortDevice(port, 115200, 1000);
            InitDevices();
            _cts = new CancellationTokenSource();
        }

        /* ============================================================ */
        /*  DEVICE INITIALISATION                                       */
        /* ============================================================ */
        private void InitDevices()
        {
            // Master reset (M,1)
            _device.Write("M,1");
            ReadLineLogged();

            // Inicjalizacja valid <banknotów>
            foreach (var cmd in new[] { "R,30", "R,31", "R,34,FFFFFFFF", "R,35,0" })
            {
                _device.Write(cmd);
                ReadLineLogged();
            }

            // Inicjalizacja <monet>
            foreach (var cmd in new[] { "R,08", "R,09", "R,0C,FFFFFFFF" })
            {
                _device.Write(cmd);
                ReadLineLogged();
            }

            DeviceEvent?.Invoke(this,
                new DeviceEventArgs(DeviceEventType.Initialized,
                    PaymentType.Cash, 0, null, "Initialized"));
        }

        /* ============================================================ */
        /*  MAIN POLL LOOP                                              */
        /* ============================================================ */
        private async Task PollLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (!_cashlessBusy)
                    {
                        // Polluj banknoty (R,33)
                        _device.Write("R,33");
                        HandleBills(ReadLineLogged());

                        // Polluj monety (R,0B)
                        _device.Write("R,0B");
                        HandleCoins(ReadLineLogged());
                    }
                }
                catch (Exception ex)
                {
                    EmitError(ex.Message);
                }

                await Task.Delay(200, _cts.Token);
            }
        }

        /* ---------- bill helper (wrapper previously missing) -------- */
        private async void HandleBills(string data)
        {
            if (TryParseBill(data) is int amt)
            {
                await HandleBillAsync(amt, _cts.Token);
            }
        }

        /* ============================================================ */
        /*  BILL ESCROW HANDLING                                        */
        /* ============================================================ */
        private async Task HandleBillAsync(int amt, CancellationToken token)
        {
            _escrowDecision = new TaskCompletionSource<bool>();
            DeviceEvent?.Invoke(this,
                new DeviceEventArgs(DeviceEventType.CashEscrowRequested,
                    PaymentType.Cash, amt));

            var timeout = Task.Delay(TimeSpan.FromSeconds(5), token);
            var finished = await Task.WhenAny(_escrowDecision.Task, timeout);

            bool accept = finished == _escrowDecision.Task && _escrowDecision.Task.Result;
            if (finished != _escrowDecision.Task)
                EmitError("Escrow timeout – returning note");

            // zaakceptuj lub zwróć banknot
            _device.Write($"R,35,{(accept ? 1 : 0)}");
            ReadLineLogged();
            DeviceEvent?.Invoke(this,
                new DeviceEventArgs(DeviceEventType.CashProcessed,
                    PaymentType.Cash, amt, accept));
        }

        /* ============================================================ */
        /*  COIN PARSING                                                */
        /* ============================================================ */
        private static readonly Regex _frame4 = new(@"[0-9A-Fa-f]{4}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private void HandleCoins(string data)
        {
            // zwracamy tylko ramki zaczynające się od "p,"
            if (string.IsNullOrEmpty(data) || !data.StartsWith("p,")) return;

            // usuń przedrostek "p," i pozostaw same znaki heksadecymalne
            string hex = new string(data.Substring(2)
                .Where(c => IsHex(c))
                .ToArray());

            foreach (Match m in _frame4.Matches(hex))
                ProcessSingleCoinFrame(m.Value);
        }

        private void ProcessSingleCoinFrame(string hex)
        {
            try
            {
                int raw = Convert.ToInt32(hex, 16);
                int high = (raw >> 8) & 0xFF;
                int route = (high & 0xC0) >> 6; // 0=tube, 1=cashbox, 2=payout
                int type = high & 0x3F;

                if (!_coinMap.TryGetValue(type, out int amount))
                {
                    // nieznany typ monety → pomiń
                    return;
                }

                switch (route)
                {
                    case 0: // przyjęto do tuby
                    case 1: // przyjęto do cashboxa
                        DeviceEvent?.Invoke(this,
                            new DeviceEventArgs(DeviceEventType.CoinReceived,
                                PaymentType.Coin, amount));
                        DeviceEvent?.Invoke(this,
                            new DeviceEventArgs(DeviceEventType.CoinProcessed,
                                PaymentType.Coin, amount, true));
                        break;
                    case 2: // wypłacono monetę
                        DeviceEvent?.Invoke(this,
                            new DeviceEventArgs(DeviceEventType.CoinDispensed,
                                PaymentType.Coin, amount));
                        break;
                }
            }
            catch
            {
                // błędy parsowania ignorujemy
            }
        }

        /* ============================================================ */
        /*  PUBLIC HELPERS                                              */
        /* ============================================================ */
        public void Accept() => _escrowDecision?.TrySetResult(true);
        public void Return() => _escrowDecision?.TrySetResult(false);

        public bool DispenseChange(int amount)
        {
            // 1. Pobierz aktualny stan tub (komenda R,0A)
            _device.Write("R,0A");
            string response = ReadLineLogged();
            var tubeMap = ParseTubeStatus(response); // słownik: nominał -> liczba sztuk

            // 2. Jeśli nie udało się sparsować stanu tub, od razu zwracamy false
            if (tubeMap == null || tubeMap.Count == 0)
            {
                // Brak danych o stanie tuby – nie wypłacamy
                EmitError($"Nie udało się pobrać stanu tub przy próbie wydania reszty: {amount} gr");
                return false;
            }

            // 3. Przygotuj rozkład na nominały (greedy od największego do najmniejszego)
            //    _tubeValues = { 10, 20, 50, 100, 200, 500 }
            //    Posortuj malejąco:
            int[] sortedValues = _tubeValues.OrderByDescending(v => v).ToArray();

            // Tymczasowa struktura na liczbę monet do wypłacenia: nominał -> ile sztuk
            var toDispense = new Dictionary<int, int>();
            int remaining = amount;

            foreach (int coinValue in sortedValues)
            {
                // Jeżeli kwota do wydania jest już zerowa, przerywamy
                if (remaining <= 0)
                    break;

                // Sprawdź, ile mamy min. tego nominału w tubie (jeśli nie ma klucza, traktujemy jako zero)
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

            // 4. Jeżeli po próbie rozkładu zostało coś do wydania, to znaczy, że brakuje monet
            if (remaining > 0)
            {
                // Nie ma wystarczająco monet, nie próbujemy wypłacać czegokolwiek
                return false;
            }

            // 5. Wypłać monety po jednym (metoda DispenseCoin generuje zdarzenia CoinDispensed)
            foreach (var kv in toDispense)
            {
                int coinValue = kv.Key;
                int countToDispense = kv.Value;

                for (int i = 0; i < countToDispense; i++)
                {
                    bool ok = DispenseCoin(coinValue);
                    if (!ok)
                    {
                        // Jeżeli nie udało się wypłacić pojedynczej monety (np. problem z komunikacją),
                        // możemy przerwać dalszą wypłatę i zwrócić false.
                        EmitError($"Błąd przy wypłacie monety {coinValue} gr");
                        return false;
                    }
                    // Jeżeli wypłata powiodła się, to w handle’u ProcessSingleCoinFrame
                    // zostanie wygenerowane DeviceEvent typu CoinDispensed.
                }
            }

            return true;
        }


        public bool DispenseCoin(int value)
        {
            int idx = Array.IndexOf(_tubeValues, value);
            if (idx < 0) return false;

            _device.Write($"R,0D,{(0x10 | idx):X2}");
            return ReadLineLogged().StartsWith("p,ACK", StringComparison.OrdinalIgnoreCase);
        }

        public void ShowTubeStatus()
        {
            _device.Write("R,0A");
            var map = ParseTubeStatus(ReadLineLogged());
            Console.WriteLine("Tube status:");
            foreach (var kv in map)
                Console.WriteLine($"  {kv.Key} gr: {kv.Value}");
        }

        public async Task<bool> StartSigmaPaymentAsync(int amountCents,
            CancellationToken ct = default)
        {
            if (_cashlessBusy) return false;
            _cashlessBusy = true;
            try
            {
                // 0. Enable cashless #2 (bit1 = 02) – C,64,02
                WriteDbg("C,64,02");
                if (!ReadAck()) return Fail("ENABLE no ACK");
                await Task.Delay(300, ct);

                // Teraz Reset == 0x60
                WriteDbg("C,60");

                // Czekamy maksymalnie 5 sek na pierwsze "d,STATUS,RESET"
                var sw = System.Diagnostics.Stopwatch.StartNew();
                const int timeoutMs = 5000;
                bool seenReset = false;
                while (sw.ElapsedMilliseconds < timeoutMs)
                {
                    await Task.Delay(100, ct);
                    WriteDbg("C,62");
                    string rsp = ReadLineLogged();
                    // ignorujemy c,ERR i p,NACK, dopóki nie pojawi się RESET
                    if (rsp.StartsWith("d,STATUS,RESET",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        seenReset = true;
                        break;
                    }
                    // pomińmy inne linie aż do timeoutu
                }

                if (!seenReset)
                    return Fail("RESET no ACK (timeout waiting for STATUS,RESET)");

                // 2. Setup – C,61
                WriteDbg("C,61");
                string setup = ReadNextNonAck(_device);
                byte decimals = 2;
                if (setup.StartsWith("p,", StringComparison.OrdinalIgnoreCase))
                {
                    var b = AsHexBytes(setup.Substring(2));
                    if (b.Length >= 7) decimals = b[6];
                }

                // 3. Display text (opcjonalne)
                SendDisplayText($"Product {amountCents / 100.0:0.00} PLN");

                // 4. Vend Request – C,63,<hi>,<lo>
                uint scaled = (uint)(amountCents / Math.Pow(10, decimals));
                byte hi = (byte)(scaled >> 8), lo = (byte)scaled;
                WriteDbg($"C,63,{hi:X2},{lo:X2}");
                if (!ReadAck())
                    return Fail("VEND REQUEST no ACK");

                DeviceEvent?.Invoke(this,
                    new DeviceEventArgs(DeviceEventType.CashlessSessionStarted,
                        PaymentType.Card, amountCents));

                // 5. Poll dla zatwierdzenia – C,62
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


        // -------------------------
        // Poniżej pozostałe potrzebne metody i pomocnicze fragmenty
        // -------------------------

        private void WriteDbg(string cmd)
        {
            Console.WriteLine($"TX » {cmd}");
            _device.Write(cmd);
        }

        private string ReadLineLogged()
        {
            string s = _device.Read();
            //Console.WriteLine($"RX « {s}");
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
            frame[0] = 0x65; // bajt komendy
            frame[1] = (byte)(utf8.Length + 1); // długość (bajt typu + tekst)
            frame[2] = 0x06; // typ = nazwa produktu
            Buffer.BlockCopy(utf8, 0, frame, 3, utf8.Length);

            string cmd = "R," + string.Join(',', frame.Select(b => b.ToString("X2")));
            WriteDbg(cmd);
            ReadAck(); // ignorujemy, jeśli urządzenie nie wspiera wyświetlania
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
                int route = (v & 0xF0) >> 4; // 9 = escrow
                int type = v & 0x0F; // 0-5
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
            if (string.IsNullOrEmpty(data) || data.StartsWith("p,ACK")) return map;
            string hex = data.Replace("p,", string.Empty);
            byte[] bytes = Enumerable.Range(0, hex.Length / 2)
                .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
                .ToArray();
            if (bytes.Length < 18) return map; // 2 bajty FULL + 16 liczników
            for (int i = 0; i < _tubeValues.Length && i < 16; i++)
                map[_tubeValues[i]] = bytes[2 + i];
            return map;
        }

        internal enum CoinRoute : byte
        {
            ToTube,
            Dispensed,
            ToCashbox
        }
    }
}