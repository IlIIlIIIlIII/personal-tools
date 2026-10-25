using System.Runtime.InteropServices;

namespace PersonalTools;

// Windows 11 draws a secondary taskbar's clock in XAML with no window of its own, so only UI Automation knows where
// it starts. Every tray button there (clock, notification bell, icons) has the AutomationId "SystemTrayIcon".
internal static class TaskbarUia
{
    private const int Descendants = 4, AutomationIdProperty = 30011, BoundingRectangleProperty = 30001;
    private static readonly object Gate = new();
    private static IUIAutomation? automation;
    private static IUIAutomationCondition? trayButtons;

    // Left edge, in screen pixels, of the leftmost tray button on this taskbar; null if none is found. It waits on
    // Explorer, so never call it on the thread that owns windows inside the taskbar.
    public static int? TrayLeft(nint taskbar)
    {
        lock (Gate)
        {
            try
            {
                automation ??= (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("ff48dba4-60ef-4201-aa87-54103eef594e"))!)!;
                trayButtons ??= automation.CreatePropertyCondition(AutomationIdProperty, "SystemTrayIcon");
                var found = automation.ElementFromHandle(taskbar).FindAll(Descendants, trayButtons);
                int? left = null;
                for (int i = 0; i < found.Length; i++)
                    if (found.GetElement(i).GetCurrentPropertyValue(BoundingRectangleProperty) is double[] { Length: 4 } bounds && bounds[2] > 0)
                        left = Math.Min(left ?? int.MaxValue, (int)Math.Round(bounds[0]));
                return left;
            }
            catch { return null; }
        }
    }

    // Only the vtable slots up to the ones called matter; the rest are placeholders in UIAutomationClient.h order.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        IUIAutomationElement ElementFromHandle(nint window);
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache(); void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache(); void GetFocusedElementBuildCache(); void CreateTreeWalker(); void ControlViewWalker();
        void ContentViewWalker(); void RawViewWalker(); void RawViewCondition(); void ControlViewCondition(); void ContentViewCondition();
        void CreateCacheRequest(); void CreateTrueCondition(); void CreateFalseCondition();
        IUIAutomationCondition CreatePropertyCondition(int property, [MarshalAs(UnmanagedType.Struct)] object value);
    }
    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }
    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus(); void GetRuntimeId(); void FindFirst();
        IUIAutomationElementArray FindAll(int scope, IUIAutomationCondition condition);
        void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int property);
    }
    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int Length { get; }
        IUIAutomationElement GetElement(int index);
    }
}
