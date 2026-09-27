using Microsoft.Win32.SafeHandles;

namespace TalosDesk.Core.Processes;

internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle() : base(ownsHandle: true) { }

    protected override bool ReleaseHandle() => WindowsNative.CloseHandle(handle);
}
