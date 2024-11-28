using Serilog;

namespace AngCore.Terminal
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            #region logger
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
            #endregion
            var cts = new CancellationTokenSource();
            var controller = new USBController("COM3");
            //controller.OnDataReceived += Controller_OnDataReceived;
            if (controller.TryOpen()) Log.Information($"instance opened");
            Thread.Sleep(100);
            if (await controller.TryDetect("#LAB?", "AngstremLabController", cts.Token))
            {
                Log.Information("instance detect success");
            }
            Task.Factory.StartNew(async () =>
            {
                await controller.TryWrite("#LAB?");
                await Task.Delay(100);
            });
            
            controller.TryWrite("#LAB?");
            controller.TryWrite("#LAB?");
            Console.ReadKey();

        }

       
    }
}
