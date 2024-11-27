using Serilog;

namespace AngCore.Terminal
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var logFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AngCore",
            "logs",
            "log-.txt");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day)
                .CreateLogger();

            var controller = new USBController("COM13");
            controller.OnDataReceived += Controller_OnDataReceived;
            Console.ReadKey();

        }

        private static void Controller_OnDataReceived(object sender, Enums.PortDataReceivedType type, object message)
        {
            if (type == Enums.PortDataReceivedType.String)
            {
                Console.WriteLine((string)message);
            }
        }
    }
}
