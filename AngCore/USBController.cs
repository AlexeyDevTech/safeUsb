using AngCore.Enums;
using Serilog;
using System.IO.Ports;
using System.Net.Http.Headers;
using System.Text;


namespace AngCore
{
    public delegate void ControllerDataReceivedEventHandler(object sender, PortDataReceivedType type, object message);


    /*
     * пометки:
     * важно понимать, что операции write необходимо использовать с задержками, так как эти операции не могут выполниться 
     * мгновенно
     * среднее время операции для baudrate:
     * 9600 -- 150мс
     * 96000 -- 15мс
     * 115200 -- 10мс
     * 
     * + стоит учесть, что есть 
     */
    public partial class USBController : IDisposable
    {
        SerialPort _port;
        public event ControllerDataReceivedEventHandler OnDataReceived;
        int lostTryingWrite = 3;
        private Timer WriteTimeoutTimer;
        TaskCompletionSource<DataPacket> OnDataTCS { get; set; }

        private readonly PortWrite _PortWrite;
        private readonly PortRead _PortRead;

        public PortStatus Status = PortStatus.Idle;
        public PortDataReceivedType ReadingDataType { get; set; } = PortDataReceivedType.String;
        public bool AutoFaultWhenOverTryingWrite { get; set; } = false;
        //таймаут между отправками сообщений. Если выставить -1 
        public int WriteDelayTimeout { get; set; } = 1000;
        public USBController(string portName, int BaudRate = 115200, bool autoFault = false)
        {
            Log.Information($"Create a instance USBController from {portName}");
            _port = new SerialPort(portName, BaudRate);            
            _port.ReadTimeout = 100;  
            _port.DataReceived += OnData;
            WriteTimeoutTimer = new Timer(WriteFree);

            // Инициализация _portWrite с передачей ссылки на метод OnFreeWrite
            _PortWrite = new PortWrite(OnFreeWrite);
        }
        private void OnFreeWrite()
        {
            Log.Information($"{_port?.PortName} Call FreeWrite: Write delay = {WriteDelayTimeout}");

            if (WriteDelayTimeout > 0)
                WriteTimeoutTimer?.Change(WriteDelayTimeout, Timeout.Infinite);
            else WriteFree(null);
        }
        /// <summary>
        /// Блокирует отправку сообщений. Методы: <code>Write({string, byte[]} data)</code> будут вызывать исключение при попытке отправить сообщение в порт.
        /// </summary>
        public void BlockWrite()
        {
            Log.Warning($"{_port.PortName} Write block!");
            WriteTimeoutTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        //механизм чтения по событию
        private void OnData(object sender, SerialDataReceivedEventArgs e)
        {
            //Log.Information("Instance receive a message");
            //Thread.Sleep(10); //дросселирование
            var p = ((SerialPort)sender);
            if ((Status & PortStatus.Open) != 0)
                Task.Factory.StartNew(async() =>
                {
                    while (p.BytesToRead > 0)
                    {
                        if ((Status & PortStatus.Reading) == 0)
                            _PortRead.Read(p, ReadingDataType, ref Status);
                        await Task.Delay(10);
                    }
                });
                
        }

        public bool TryWrite(object data)
        {
            return _PortWrite.TryWrite(_port, data, ref Status, AutoFaultWhenOverTryingWrite, lostTryingWrite);
        }

        public async Task<bool> TryWriteAsync(object data)
        {
            return await _PortWrite.TryWriteAsync(_port, data, Status, AutoFaultWhenOverTryingWrite, lostTryingWrite);
        }


        public void Write(object data)
        {
            _PortWrite.Write(_port, data, ref Status);
        }


        public async Task<bool> SendCommandAndCheckResponse(object request, object ExpectedResponse, CancellationToken CancellationToken)
        {
            try
            {
                // Отправляем команду
                if (!TryWrite(request))
                {
                    Log.Warning("Failed to send command.");
                    return false;
                }

                // Создаем TaskCompletionSource для ожидания ответа
                OnDataTCS = new TaskCompletionSource<DataPacket>();

                // Отменяем ожидание по токену отмены
                CancellationToken.Register(() => OnDataTCS.TrySetCanceled());

                Log.Information("Command sent. Waiting for response...");

                // Ожидаем получения данных
                var ReceivedData = await OnDataTCS.Task;

                // Проверяем тип данных
                if (ExpectedResponse is string && ReceivedData.TypeData != PortDataReceivedType.String)
                {
                    Log.Warning("Response type mismatch. Expected string.");
                    return false;
                }
                if (ExpectedResponse is byte[] && ReceivedData.TypeData != PortDataReceivedType.Byte)
                {
                    Log.Warning("Response type mismatch. Expected byte[].");
                    return false;
                }

                // Сравниваем данные
                if (ExpectedResponse is string ExpectedString)
                {
                    if (ReceivedData.Data is string ActualString && ActualString.Contains(ExpectedString))
                    {
                        return true;
                    }
                }
                else if (ExpectedResponse is byte[] ExpectedBytes)
                {
                    if (ReceivedData.Data is byte[] ActualBytes && ActualBytes.SequenceEqual(ExpectedBytes))
                    {
                        return true;
                    }
                }

                Log.Warning("Response does not match the expected value.");
                return false;
            }
            catch (TaskCanceledException)
            {
                Log.Warning("Operation canceled while waiting for response.");
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"Unexpected error in SendCommandAndCheckResponse: {ex.Message}");
                return false;
            }
        }



        public bool TryOpen()
        {
            if ((Status & PortStatus.Fault) != 0)
            {
                Log.Error("Port instance already fault. Dispose it.");
            }
            var res = false;
            try
            {
                if((Status & PortStatus.Open) == 0)
                {
                    _port.Open();
                    Thread.Sleep(50);
                    if (_port.IsOpen)
                    {
                        res = true;
                        Status |= PortStatus.Open;
                    }

                }
            }
            catch (UnauthorizedAccessException)
            {
                Log.Error("port access is denied. Try again or reinit it");
            }
            catch(InvalidOperationException)
            {
                Log.Error("port is closed. ");
            }
            catch (IOException ioex)
            {
                Log.Error($"port IO Exception: {ioex.Message}");
            }
            return res;
        }

        public async Task<bool> TryDetect(string request, string responce, CancellationToken token, int attempts = 3)
        {
            Log.Information("Start detect instance...");
            while (attempts > 0)
            {
                Log.Information($"Try write (attempts = {attempts})...");
                OnDataTCS = new TaskCompletionSource<DataPacket>();
                if (await TryWriteAsync(request))
                {
                    token.Register(() => OnDataTCS.TrySetCanceled());
                    Log.Information($"{_port.PortName} -> Wait callback");
                    var msg = await OnDataTCS.Task;
                    if (msg != null)
                    {//TODO: добавить цикл на проверку, дочитал ли он до конца или нет 
                        if (msg.TypeData == PortDataReceivedType.String)
                        {
                            if ((msg.Data as string)?.Contains(responce) ?? false)
                            {
                                return true;
                            }
                            else
                            {
                                Log.Warning($"msg is not responce. Try again");
                                attempts--;
                            }
                        }
                        //else return false;
                    }
                    else
                    {
                        Log.Warning($"msg is null. Try again");
                        attempts--;
                    }
                }
                await Task.Delay(100);
                //attempts--;
            }
            return false;
        }
        public async Task<bool> TryDetect(byte[] request, byte[] responce, CancellationToken token ,int attempts = 3)
        {
            while (attempts > 0)
            {
                token.Register(() => OnDataTCS.TrySetCanceled());
                if (TryWrite(request))
                {
                    var msg = await OnDataTCS.Task;
                    if (msg != null)
                    {
                        if (msg.TypeData == PortDataReceivedType.Byte)
                        {
                            if((msg.Data as byte[])?.SequenceEqual(responce) ?? false)
                            {
                                return true;
                            }
                        }
                        else return false;
                    }
                }
                await Task.Delay(100);
                attempts--;
            }
            return false;
        }

      
        private void WriteFree(object? state)
        {
            Log.Information($"{_port?.PortName} Write status free");
            Status &= ~PortStatus.Writing;
        }        
        
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        private void Dispose(bool disposing)
        {

            if (disposing)
            {
                if (_port != null)
                {
                    _port.DataReceived -= OnData;
                    _port.Close();
                    _port.Dispose();
                }
            }
        }
    }

    internal class DataPacket
    {
        public PortDataReceivedType TypeData { get; set; }
        public object Data { get; set; }
    }
    public class PortBusyException : Exception
    {
    }
    public class PortFaultException : Exception
    {

    }
    public enum PortStatus : byte
    {
        Idle = 0x00,       //создан экземпляр, устройство не подключено
        Open = 0x01,       //порт может быть открыт, но факт наличия устройства ещё не установлен
        Connected = 0x02,       //устройство определено
        Writing = 0x04,       //для последовательного порта операции записи и чтения обязательно должны быть разделены
        Reading = 0x08,       //
        Fault = 0xF0        //устройство в ошибке, требуется повторная инициализация устройства
    }
}
