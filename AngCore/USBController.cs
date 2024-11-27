using AngCore.Enums;
using Serilog;
using System.IO.Ports;
using System.Net.Http.Headers;
using System.Text;


namespace AngCore
{
    public delegate void ControllerDataReceivedEventHandler(object sender, PortDataReceivedType type, object message);

    public partial class USBController : IDisposable
    {
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
            Log.Warning($"{_port.PortName} Write block");
            WriteTimeoutTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }

        //механизм чтения по событию
        private void OnData(object sender, SerialDataReceivedEventArgs e)
        {
            var p = ((SerialPort)sender);
            if((Status & PortStatus.Open) != 0 && (Status & PortStatus.Reading) == 0)
                if (p.BytesToRead > 0)
                {
                    Read(p);
                }
        }     
        public bool TryWrite(byte[] data)
        {
            var res = false;
            try
            {
                Write(data);
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
        public bool TryWrite(string data)
        {
            var res = false;
            try
            {
                Write(data);
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
                        res = true;

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

        public async bool TryDetect(string request, string responce, CancellationToken token, int attempts = 3)
        {
            if (TryWrite(request))
            {
                token.Register(() => );
            }

        }
        public async bool TryDetect(byte[] request, byte[] responce, int attempts = 3)
        {

        }
    }

    public partial class USBController
    {
        SerialPort _port;
        public event ControllerDataReceivedEventHandler OnDataReceived;
        int lostTryingWrite = 3;
        private Timer WriteTimeoutTimer;
        private void WriteFree(object? state)
        {
            Log.Information($"{_port?.PortName} Write status free");
            Status &= ~PortStatus.Writing;
        }
        private void Read(SerialPort p)
        {
            Log.Information($"{p.PortName} Reading data. Type data = {ReadingDataType}");
            try
            {
                if((Status & PortStatus.Fault) != 0) throw new PortFaultException();
                if ((Status & PortStatus.Reading) != 0) throw new PortBusyException();
                Status |= PortStatus.Reading;
                switch (ReadingDataType)
                {
                    case PortDataReceivedType.String:
                        var d = p.ReadExisting();
                        foreach (var item in d.Split('\n'))
                        {
                            Log.Information($"{p.PortName} data Read. result = {item}. Call event");
                            //try call event from data received
                            if(!OnDataTCS?.Task.IsCompleted ?? false)
                                OnDataTCS?.TrySetResult(new DataPacket
                                {
                                    TypeData = ReadingDataType,
                                    Data = item,
                                });

                            OnDataReceived?.Invoke(this, ReadingDataType, item);
                        }
                        break;
                    case PortDataReceivedType.Byte:
                        var buffer = new byte[p.BytesToRead];
                        p.Read(buffer, 0, buffer.Length);
                        Log.Information($"{p.PortName} data Read. result = {Encoding.UTF8.GetString(buffer)}. Call event");
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
            finally
            {
                Status &= ~PortStatus.Reading;
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
                    //физическое открытие порта
                    if ((Status & (PortStatus.Open)) != 0)
                    {
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
