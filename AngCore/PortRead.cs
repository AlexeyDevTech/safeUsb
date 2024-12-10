using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;
using AngCore.Enums;
using Serilog;


namespace AngCore
{
    public class PortRead
    {
        public event ControllerDataReceivedEventHandler OnDataReceived;
        internal TaskCompletionSource<DataPacket> OnDataTCS { get; set; }

        public void Read(SerialPort p, PortDataReceivedType ReadingDataType, ref PortStatus Status)
        {
            try
            {
                if ((Status & PortStatus.Fault) != 0) throw new PortFaultException();
                if ((Status & PortStatus.Reading) != 0) throw new PortBusyException();
                Status |= PortStatus.Reading;
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
    }

}
