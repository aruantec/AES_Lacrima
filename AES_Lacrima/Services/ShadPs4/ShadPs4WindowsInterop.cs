using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AES_Lacrima.Services.ShadPs4;

internal static class ShadPs4WindowsInterop
{
    private const uint CoinitApartmentThreaded = 0x2;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    public static T RunOnStaThread<T>(Func<T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
            return RunWithCom(action);

        T? result = default;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = RunWithCom(action);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
            throw error;

        return result!;
    }

    private static T RunWithCom<T>(Func<T> action)
    {
        var needsUninitialize = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded) is 0 or 1;
        try
        {
            return action();
        }
        finally
        {
            if (needsUninitialize)
                CoUninitialize();
        }
    }
}
