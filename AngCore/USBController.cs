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
    public class USBController : IDisposable
    {
        protected SerialPort _port;
        public event ControllerDataReceivedEventHandler OnDataReceived;
        int lostTryingWrite = 3;
        private Timer WriteTimeoutTimer;

        TaskCompletionSource<DataPacket> OnDataTCS { get; set; }

        public PortStatus Status = PortStatus.Idle;
        public PortDataReceivedType ReadingDataType { get; set; } = PortDataReceivedType.String;
        public bool AutoFaultWhenOverTryingWrite { get; set; } = false;
        //таймаут между отправками сообщений. Если выставить -1 
        public int WriteDelayTimeout { get; set; } = 200;
        public USBController(string portName, int BaudRate = 115200, bool autoFault = false)
        {
            Log.Information($"Create a instance USBController from {portName}");
            _port = new SerialPort(portName, BaudRate);
            _port.ReadTimeout = 100;  
            _port.DataReceived += OnData;
            WriteTimeoutTimer = new Timer(WriteFree);
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
        protected virtual void OnData(object sender, SerialDataReceivedEventArgs e)
        {
            //Log.Information("Instance receive a message");
            //Thread.Sleep(10); //дросселирование
            var p = ((SerialPort)sender);
            if ((Status & PortStatus.Open) != 0 && (Status & PortStatus.Reading) == 0)
                Task.Factory.StartNew(async() =>
                {
                    while (p.BytesToRead > 0)
                    {
                        Read(p);
                        await Task.Delay(10);
                    }
                });
                
        }     
        public bool TryWrite(byte[] data)
        {
            var res = false;
            try
            {
                while ((Status & PortStatus.Writing) != 0)
                {
                    Thread.Sleep(10);
                }
                Write(data);
                Thread.Sleep(150);
                res = true;
            }
            catch (PortBusyException)
            {
                return false;
            }
            catch (PortFaultException)
            {
                return false;
            }
            catch (TimeoutException toex)
            {
                if (AutoFaultWhenOverTryingWrite)
                {
                    lostTryingWrite--;
                    if (lostTryingWrite < 0)
                        Status |= PortStatus.Fault;
                }
                return false;
            }
            catch (InvalidOperationException ioex)
            {
                Status &= ~PortStatus.Open;
                Status |= PortStatus.Fault;
                return false;
            }
            catch (Exception ex)
            {
                return false;
            }
            lostTryingWrite = 3;
            return res;
        }
        public async Task<bool> TryWrite(string data)
        {
            var res = false;
            try
            {
                while ((Status & PortStatus.Writing) != 0) 
                {
                   await Task.Delay(10);
                }
                Write(data);
                res = true;
            }
            catch (PortBusyException)
            {

                Log.Error("Port writing busy. message ignored.");
                return false;
            }
            catch (PortFaultException)
            {
                Log.Error("Port fault. Please Dispose it");
                return false;
            }
            catch (TimeoutException toex)
            {
                if (AutoFaultWhenOverTryingWrite)
                {
                    lostTryingWrite--;
                    if (lostTryingWrite < 0)
                        Status |= PortStatus.Fault;
                }
                return false;
            }
            catch (InvalidOperationException ioex)
            {
                Status &= ~PortStatus.Open;
                Status |= PortStatus.Fault;
                return false;
            }
            catch (Exception ex)
            {
                return false;
            }
            lostTryingWrite = 3;
            return res;
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
                if (await TryWrite(request))
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
        private void Read(SerialPort p)
        {
            //Log.Information($"{p.PortName} Reading data. Type data = {ReadingDataType}");
            try
            {
                if ((Status & PortStatus.Fault) != 0) throw new PortFaultException();
                if ((Status & PortStatus.Reading) != 0) throw new PortBusyException();
                Status |= PortStatus.Reading;
                HandleData();
            }
            catch (TimeoutException toex)
            {
                Log.Error("Timeout reading");
            }
            catch (InvalidOperationException ioex)
            {
                Log.Error("Port closed. instance fault.");
                Status |= PortStatus.Fault;
            }
            catch (PortBusyException)
            {
                Log.Error("Port already reading. Please wait");
            }
            catch (PortFaultException)
            {
                Log.Error("Port instance fault. Dispose it.");
            }

            catch (Exception ex)
            {
                Log.Error($"Unexcepted error {ex.Message}");
            }
            Status &= ~PortStatus.Reading;

        }
        protected void HandleData()
        {
            switch (ReadingDataType)
            {
                case PortDataReceivedType.String:
                    var d = p.ReadLine();
                    Log.Information($"{p.PortName}(BTR={p.BytesToRead}) data Read(String). result = {d}");
                    if (!OnDataTCS?.Task.IsCompleted ?? false)
                        OnDataTCS?.TrySetResult(new DataPacket
                        {
                            TypeData = ReadingDataType,
                            Data = d,
                        });
                    OnDataReceived?.Invoke(this, ReadingDataType, d);
                    break;
                case PortDataReceivedType.Byte:
                    var buffer = new byte[p.BytesToRead];
                    p.Read(buffer, 0, buffer.Length);
                    Log.Information($"{p.PortName} data Read(Byte[]). Result = {Encoding.UTF8.GetString(buffer)}");
                    if (!OnDataTCS?.Task.IsCompleted ?? false)
                        OnDataTCS?.TrySetResult(new DataPacket
                        {
                            TypeData = ReadingDataType,
                            Data = buffer,
                        });

                    OnDataReceived?.Invoke(this, ReadingDataType, buffer);
                    break;
            }
        }
        public void Write(byte[] data)
        {
            if ((Status & (PortStatus.Writing)) != 0)
                throw new PortBusyException();
            if ((Status & (PortStatus.Fault)) != 0)
                throw new PortFaultException();
            try
            {
                Status |= PortStatus.Writing;
                if ((Status & (PortStatus.Open)) != 0)
                {
                    Log.Information($"{_port.PortName} Write bytes: L={data.Length}");
                    _port.Write(data, 0, data.Length);
                }
            }
            finally
            {
                OnFreeWrite();
            }
        }
        public void Write(string data)
        {
            if ((Status & (PortStatus.Writing)) != 0)
                throw new PortBusyException();
            if ((Status & (PortStatus.Fault)) != 0)
                throw new PortFaultException();
            if ((Status & (PortStatus.Open)) != 0)
            {
                try
                {
                    Status |= PortStatus.Writing;
                    //физическое открытие порта
                    if ((Status & (PortStatus.Open)) != 0)
                    {
                        Log.Information($"{_port.PortName} Write string: {data}");
                        _port.Write(data);
                    }
                }
                finally
                {
                    OnFreeWrite();
                }
            }
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

    public class UsbManagedController : USBController
    {
        ControllerReader reader;
        public UsbManagedController(string portName, int BaudRate = 115200, bool autoFault = false) : base(portName, BaudRate, autoFault)
        {
            reader = new ControllerReader(_port);
            reader.DataReceived = DR;
        }

        private void DR(string obj)
        {

        }

        protected override void OnData(object sender, SerialDataReceivedEventArgs e)
        {
            //base.OnData(sender, e);
            reader.Read();
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

    public class ControllerReader
    {
        SerialPort _port;
        bool _isReading = false;
        Queue<char> _chars = new Queue<char>();
        public Action<string> DataReceived { get; set; }
        public ControllerReader(SerialPort port)
        {
            _port = port;
        }

        public void Read()
        {
            _isReading = true;
            while (_port.BytesToRead > 0)
            {
                try
                {
                    var d = _port.ReadByte();
                    if (d != -1)
                        _chars.Enqueue((char)d);
                }
                catch (TimeoutException)
                {
                    Log.Error("port reading timeout");
                }
                catch(InvalidOperationException e)
                {
                    Log.Error(e, "port already closed");
                }

            }

            while(_chars.Count > 0)
            {
                var str = ReadFromQueue();
                if (string.IsNullOrEmpty(str)) break; //прерывание чтобы не было зацикливания
                DataReceived?.Invoke(str);
            }
        }
        // Метод для чтения данных из очереди
        public string ReadFromQueue()
        {
            var result = new List<char>();
            lock (_chars)
            {
                while (_chars.Count > 0)
                {
                    var ch = _chars.Dequeue();
                    result.Add(ch);
                    if (ch == '\n')
                        return new string(result.ToArray());
                }
            }
            if (_chars.Count == 0) // вычитали всё и не встретили /n
            {
                foreach (var ch in result)
                {
                    _chars.Enqueue(ch); // возвращаем в очередь
                }
            }
                

            return string.Empty;
        }
    }
}
