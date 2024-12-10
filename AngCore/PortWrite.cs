using AngCore;
using Serilog;
using System.IO.Ports;


public class PortWrite
{

    private readonly Action _OnFreeWrite;

    public PortWrite(Action OnFreeWrite)
    {
        _OnFreeWrite = OnFreeWrite ?? throw new ArgumentNullException(nameof(OnFreeWrite));
    }

    public void Write(SerialPort port, object data, ref PortStatus Status)
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

                if (data is string stringData)
                {
                    Log.Information($"{port.PortName} Write string: {stringData}");
                    port.Write(stringData);
                }
                else if (data is byte[] byteData)
                {
                    Log.Information($"{port.PortName} Write bytes: Length={byteData.Length}");
                    port.Write(byteData, 0, byteData.Length);
                }
            }
            finally
            {
                _OnFreeWrite();
            }
        }
    }

    public bool TryWrite(SerialPort port, object data, ref PortStatus Status, bool AutoFaultWhenOverTryingWrite, int lostTryingWrite)
    {
        var res = false;
        try
        {
            while ((Status & PortStatus.Writing) != 0)
            {
                Thread.Sleep(10);
            }

            if (data is string stringData)
            {
                Write(port, stringData, ref Status);
            }
            else if (data is byte[] byteData)
            {
                Write(port, byteData, ref Status);
            }

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

    public async Task<bool> TryWriteAsync(SerialPort port, object data, PortStatus Status, bool AutoFaultWhenOverTryingWrite, int lostTryingWrite)
    {
        var res = false;
        try
        {
            while ((Status & PortStatus.Writing) != 0)
            {
                Thread.Sleep(10);
            }

            if (data is string stringData)
            {
                Write(port, stringData, ref Status);
            }
            else if (data is byte[] byteData)
            {
                Write(port, byteData, ref Status);
            }

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

}

