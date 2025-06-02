using ITBees.Mdb;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace TestMdbConsoleApp
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            using var device = new SerialPortDevice();

            var service = new PaymentAcceptorService(device);

            service.DeviceEvent += (s, e) =>
            {
                switch (e.EventType)
                {
                    case DeviceEventType.Initialized:
                        Console.WriteLine("Device is ready.");
                        break;
                    case DeviceEventType.CashEscrowRequested:
                        Console.WriteLine($"Escrow: {e.Amount} Hit A=accept, R=return.");
                        break;
                    case DeviceEventType.CashProcessed:
                        Console.WriteLine(
                            $"Banknote {e.Amount} {(e.Accepted.Value ? "accepted" : "returned")}.");
                        break;
                    case DeviceEventType.CoinReceived:
                        Console.WriteLine($"Received Coin: {e.Amount}.");
                        break;
                    case DeviceEventType.CoinProcessed:
                        Console.WriteLine($"Coin {e.Amount} accepted.");
                        break;
                    case DeviceEventType.Error:
                        Console.WriteLine($"Error: {e.Message}");
                        break;
                    case DeviceEventType.CoinDispensed:
                        Console.WriteLine($"Dispensed Coin: {e.Amount}.");
                        break;
                    case DeviceEventType.CoinToCashbox:
                        Console.WriteLine($"Coin to cashbox: {e.Amount}.");
                        break;
                    case DeviceEventType.CashlessSessionStarted:
                        Console.WriteLine("Waiting for card …");
                        break;
                    case DeviceEventType.CashlessVendApproved:
                        Console.WriteLine("Payment approved!");
                        break;
                    case DeviceEventType.CashlessVendDenied:
                        Console.WriteLine("Payment denied.");
                        break;
                }
            };

            var portName = "COM3";
            service.Start(portName);

            Console.WriteLine("Commands:");
            Console.WriteLine("A - Accept banknote");
            Console.WriteLine("S - Show tube status");
            Console.WriteLine("D - Dispense coin");
            Console.WriteLine("Q - Stop mdb service");
            Console.WriteLine("E - Enable mdb service");
            Console.WriteLine("T - Start myposSigma payment");
            Console.WriteLine("M - Manual: wpisz dowolną komendę do portu szeregowego");
            Console.WriteLine("R - Return banknote");
            Console.WriteLine("Esc - Exit");

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    Console.WriteLine($"Pressed key: {key}");
                    switch (key)
                    {
                        case ConsoleKey.A:
                            service.Accept();
                            break;

                        case ConsoleKey.S:
                            service.ShowTubeStatus();
                            break;

                        case ConsoleKey.D:
                            Console.Write("Podaj nominał monety do wydania (np. 10, 20, 50, 100 …): ");
                            string coinValue = Console.ReadLine() ?? "";
                            if (int.TryParse(coinValue.Trim(), out int v))
                            {
                                bool ok = service.DispenseCoin(v);
                                Console.WriteLine(ok
                                    ? $"Wydałem monetę {v} gr"
                                    : $"Nie udało się wydać monety {v} gr");
                            }
                            else
                            {
                                Console.WriteLine("Błędna wartość monety.");
                            }
                            break;

                        case ConsoleKey.Q:
                            service.Stop();
                            break;

                        case ConsoleKey.E:
                            service.Start(portName);
                            break;

                        case ConsoleKey.T:
                            // przykład płatności 5 zł (500 gr)
                            await service.StartSigmaPaymentAsync(500);
                            break;

                        case ConsoleKey.R:
                            service.Return();
                            break;

                        case ConsoleKey.M:
                            // tryb manualnego wpisywania dowolnej komendy ASCII do portu szeregowego
                            Console.WriteLine("=== Tryb manualny: wpisz komendę (np. M,1 lub R,33 itp.), Enter aby wysłać ===");
                            string manualCmd = Console.ReadLine() ?? "";
                            if (!string.IsNullOrWhiteSpace(manualCmd))
                            {
                                // Wysyłamy dokładnie to, co wpisał użytkownik
                                device.Write(manualCmd);
                                // Natychmiast odczytajemy i wypisujemy pierwszą odpowiedź
                                string resp = device.Read();
                                Console.WriteLine($"RX « {resp}");
                                // Jeśli chcesz odczytywać wszystkie linie aż do pustej:
                                /*
                                while (true)
                                {
                                    string line = device.Read();
                                    if (string.IsNullOrEmpty(line)) break;
                                    Console.WriteLine($"RX « {line}");
                                }
                                */
                            }
                            else
                            {
                                Console.WriteLine("Żadne polecenie nie zostało wpisane.");
                            }
                            break;

                        case ConsoleKey.Escape:
                            service.Stop();
                            return;
                    }
                }

                Thread.Sleep(50);
            }
        }
    }
}
