using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace PersonalTools.HdrWidget;

internal static class Program
{
    [MTAThread]
    private static void Main()
    {
        try
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
            var factory = new ProviderFactory();
            Marshal.ThrowExceptionForHR(CoRegisterClassObject(typeof(WidgetProvider).GUID, factory, 4, 1, out uint cookie));
            try { WidgetProvider.Empty.WaitOne(); }
            finally { CoRevokeClassObject(cookie); GC.KeepAlive(factory); CoUninitialize(); }
        }
        catch (Exception e) { WidgetProvider.Report(e); }
    }
    [DllImport("ole32.dll")] private static extern int CoRegisterClassObject([MarshalAs(UnmanagedType.LPStruct)] Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object factory, uint context, uint flags, out uint cookie);
    [DllImport("ole32.dll")] private static extern int CoRevokeClassObject(uint cookie);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}

[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClassFactory
{
    [PreserveSig] int CreateInstance(nint outer, ref Guid iid, out nint result);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool locked);
}
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class ProviderFactory : IClassFactory
{
    private WidgetProvider? provider;
    private readonly object sync = new();
    public int CreateInstance(nint outer, ref Guid iid, out nint result)
    {
        result = 0;
        if (outer != 0) return unchecked((int)0x80040110);
        try
        {
            lock (sync) provider ??= new WidgetProvider();
            nint inspectable = MarshalInspectable<IWidgetProvider>.FromManaged(provider);
            try { return Marshal.QueryInterface(inspectable, ref iid, out result); }
            finally { Marshal.Release(inspectable); }
        }
        catch (Exception e) { WidgetProvider.Report(e); return Marshal.GetHRForException(e); }
    }
    public int LockServer(bool locked) => 0;
}
