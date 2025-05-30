using ITBees.Mdb;

namespace TestMdbConsoleApp
{
    internal class Program
    {
        static void Main(string[] args)
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
                        Console.WriteLine($"Escrow: {e.Amount} Hit A =accept, R=return.");
                        break;
                    case DeviceEventType.CashProcessed:
                        Console.WriteLine(
                            $"Banknote {e.Amount} {(e.Accepted.Value ? "accpeted" : "returned")}.");
                        break;
                    case DeviceEventType.CoinReceived:
                        Console.WriteLine($"Received Coin: {e.Amount} .");
                        break;
                    case DeviceEventType.CoinProcessed:
                        Console.WriteLine($"Coin {e.Amount} accepted.");
                        break;
                    case DeviceEventType.Error:
                        Console.WriteLine($"Error: {e.Message}");
                        break;
                }
            };

            service.Start("COM3");

            Console.WriteLine("Hit enter to quit");

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    if (key == ConsoleKey.A)
                        service.Accept();
                    else if (key == ConsoleKey.R)
                        service.Return();
                    else if (key == ConsoleKey.Escape)
                        break;
                }

                Thread.Sleep(50);
            }

            service.Stop();
        }
    }
}
