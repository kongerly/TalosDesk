using System;
using System.Runtime.InteropServices;
using System.Text;

// 仅供远端包测试填写本次应用拥有的原生文件对话框；不发送全局按键或操作其他进程。
public static class PackageImportNativeControls
{
    private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr dialog, int id);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SetText(IntPtr window, uint message, IntPtr parameter, string text, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr ReadText(IntPtr window, uint message, IntPtr capacity, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr parameter, IntPtr data);

    private static void VerifyOwner(IntPtr window, int processId)
    {
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var owner) == 0 || owner != (uint)processId)
            throw new InvalidOperationException("Native import control is not owned by the package process.");
    }

    private static string ClassName(IntPtr window)
    {
        var text = new StringBuilder(256);
        GetClassName(window, text, text.Capacity);
        return text.ToString();
    }

    private static IntPtr Control(IntPtr dialog, int processId, int id)
    {
        VerifyOwner(dialog, processId);
        var control = GetDlgItem(dialog, id);
        if (control == IntPtr.Zero)
        {
            var matches = 0;
            EnumChildWindows(dialog, (child, _) =>
            {
                if (GetDlgCtrlID(child) == id && (id != 1 || ClassName(child) == "Button")) { control = child; matches++; }
                return true;
            }, IntPtr.Zero);
            if (matches != 1) throw new InvalidOperationException("Expected one owned native import control: " + id);
        }
        VerifyOwner(control, processId);
        if (!IsChild(dialog, control)) throw new InvalidOperationException("Unexpected import control parent.");
        return control;
    }

    public static void SetFileName(IntPtr dialog, int processId, string path)
    {
        var host = Control(dialog, processId, 1148);
        var editor = ClassName(host) == "Edit" ? host : IntPtr.Zero;
        var editors = 0;
        if (editor == IntPtr.Zero)
        {
            EnumChildWindows(host, (child, _) =>
            {
                if (ClassName(child) == "Edit") { editor = child; editors++; }
                return true;
            }, IntPtr.Zero);
            if (editors != 1) throw new InvalidOperationException("Expected exactly one native filename editor under control 1148 (" + ClassName(host) + ").");
        }
        VerifyOwner(editor, processId);
        if (!IsChild(dialog, editor) || !IsWindowEnabled(editor)) throw new InvalidOperationException("Native filename editor is unavailable.");
        // 每次跨进程消息都有期限；回读完整路径后才允许点击打开。
        if (SetText(editor, 0x000C, IntPtr.Zero, path, 3, 5000, out var setResult) == IntPtr.Zero || setResult == UIntPtr.Zero)
            throw new InvalidOperationException("Native filename update failed or timed out.");
        var text = new StringBuilder(path.Length + 1);
        if (ReadText(editor, 0x000D, (IntPtr)text.Capacity, text, 3, 5000, out _) == IntPtr.Zero || text.ToString() != path)
            throw new InvalidOperationException("Native filename readback did not match the isolated import source.");
    }

    public static bool CanOpen(IntPtr dialog, int processId) => IsWindowEnabled(Control(dialog, processId, 1));

    public static void Open(IntPtr dialog, int processId)
    {
        var button = Control(dialog, processId, 1);
        if (ClassName(button) != "Button" || !IsWindowEnabled(button) || !PostMessage(button, 0x00F5, IntPtr.Zero, IntPtr.Zero))
            throw new InvalidOperationException("Native import open button could not be invoked.");
    }
}
