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
                    case DeviceEventType.CoinDispensed:
                        Console.WriteLine($"Dispensed Coin: {e.Amount}.");
                        break;
                    case DeviceEventType.CoinToCashbox:
                        Console.WriteLine($"Coin to cashbox: {e.Amount}.");
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
            Console.WriteLine("T - Enable mdb service");

            while (true)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true).Key;
                    Console.WriteLine($"Pressed key : {key}");
                    if (key == ConsoleKey.A)
                        service.Accept();
                    if (key == ConsoleKey.Q)
                        service.Stop();
                    if (key == ConsoleKey.E)
                        service.Start(portName);
                    if (key == ConsoleKey.S)
                        service.ShowTubeStatus();
                    if (key == ConsoleKey.D)
                    {
                        string coinValue = Console.ReadLine();
                        if(coinValue.Trim() == String.Empty)
                            return;
                        service.DispenseCoin(Convert.ToInt32(coinValue.Trim()));
                    }
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
