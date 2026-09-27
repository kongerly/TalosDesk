using Microsoft.Win32.SafeHandles;

namespace TalosDesk.Core.Processes;

internal sealed class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeProcessHandle(IntPtr handle, bool ownsHandle) : base(ownsHandle) => SetHandle(handle);

    protected override bool ReleaseHandle() => WindowsNative.CloseHandle(handle);
}
