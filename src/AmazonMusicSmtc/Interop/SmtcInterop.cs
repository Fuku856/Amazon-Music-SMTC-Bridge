using System.Runtime.InteropServices;
using Windows.Media;

namespace AmazonMusicSmtc.Interop;

/// <summary>
/// SMTC has no activatable constructor for desktop apps; an instance must be
/// obtained per-HWND via ISystemMediaTransportControlsInterop.
///
/// The vtable is invoked directly rather than through a [ComImport] interface,
/// because modern .NET does not support ComInterfaceType.InterfaceIsIInspectable
/// ("Marshalling as IInspectable is not supported in the .NET runtime").
/// </summary>
internal static unsafe class SmtcInterop
{
    /// <summary>IUnknown (0-2) + IInspectable (3-5), so GetForWindow is slot 6.</summary>
    private const int GetForWindowSlot = 6;

    private static readonly Guid IidSystemMediaTransportControls =
        new("99fa3ff4-1742-42a6-902e-087d41f965ec");

    private static readonly Guid IidInterop =
        new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    public static SystemMediaTransportControls GetForWindow(IntPtr hwnd)
    {
        const string className = "Windows.Media.SystemMediaTransportControls";

        Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out var classId));
        try
        {
            var interopIid = IidInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId, ref interopIid, out var factory));

            try
            {
                var vtable = *(void***)factory;
                var getForWindow =
                    (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vtable[GetForWindowSlot];

                var smtcIid = IidSystemMediaTransportControls;
                IntPtr abi;
                Marshal.ThrowExceptionForHR(getForWindow(factory, hwnd, &smtcIid, &abi));

                try
                {
                    return WinRT.MarshalInspectable<SystemMediaTransportControls>.FromAbi(abi);
                }
                finally
                {
                    // FromAbi takes its own reference on the projected object.
                    Marshal.Release(abi);
                }
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            WindowsDeleteString(classId);
        }
    }
}
