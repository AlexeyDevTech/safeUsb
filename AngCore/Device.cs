using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;
using AngCore.Enums;
using Serilog;


namespace AngCore
{
    
    public abstract class Device
    {
        public event Action<string> OnDataReceived;

        protected abstract void SendData(string data);
        protected abstract string ReadResponse();

        public void WaitResponse()
        {            
            Thread.Sleep(1000);
            string response = ReadResponse();
            OnDataReceived?.Invoke(response);
        }

        protected bool CheckResult(string response)
        {
            // Проверка корректности ответа
            return !string.IsNullOrEmpty(response);
        }
    }

    public class TestDevice : Device
    {
        private string _portData;

        public TestDevice()
        {
            OnDataReceived += HandleDataReceived;
        }

        protected override void SendData(string data)
        {
            // Отправка данных
            Console.WriteLine($"Sent: {data}");
            _portData = $"Response to {data}"; // Ответ устройства
        }

        protected override string ReadResponse()
        {
            return _portData; // Возвращаем данные, полученные от устройства
        }

        private void HandleDataReceived(string data)
        {
            Console.WriteLine($"Data received: {data}");
        }

        public bool GetStatus()
        {
            SendData("#Status");
            WaitResponse();
            string response = ReadResponse();
            if (CheckResult(response))
            {                
                return true; 
            }
            else
            {                
                return false; 
            }
        }
    }

}
