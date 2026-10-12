using System;
using System.Runtime.InteropServices;

namespace Postfold.Desktop.Models;

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WindowsNotificationData
{
    internal uint Size;
    internal IntPtr Window;
    internal uint Id;
    internal uint Flags;
    internal uint CallbackMessage;
    internal IntPtr Icon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    internal string Tooltip;
    internal uint State;
    internal uint StateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    internal string Information;
    internal uint Version;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    internal string Title;
    internal uint InformationFlags;
    internal Guid Guid;
    internal IntPtr BalloonIcon;
}
