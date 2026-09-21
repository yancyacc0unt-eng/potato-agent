// 回收站：只走 shell32 的 SHFileOperationW + FOF_ALLOWUNDO。
//
// 为什么不用 Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile（它内部也是这套 API）：
//   本工程零 NuGet、零额外框架引用，直接 P/Invoke 最省事，而且能自己控制 STA 线程与标志位。
//
// ⚠ 两条不能省的细节：
//   1) pFrom 必须是"双 null 结尾"的多字符串（StringToHGlobalUni 会把我们给的 "\0" 再补一个结尾 null）
//   2) SHFileOperation 要求 STA 线程调用，工具却在任意线程上被调用 —— 所以在这里现开一个 STA 线程

using System.Runtime.InteropServices;

namespace PotatoAgent.Files;

/// <summary>把文件/目录送进回收站（内部用，绝不永久删除）。</summary>
internal static class RecycleBin
{
    /// <summary>FO_DELETE：删除操作。</summary>
    private const uint FoDelete = 0x0003;

    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    /// <summary>关键标志：把删除变成"可撤销"（进回收站）。</summary>
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmMkDir = 0x0200;
    private const ushort FofNoErrorUi = 0x0400;

    /// <summary>COINIT_APARTMENTTHREADED。</summary>
    private const uint CoInitApartmentThreaded = 0x0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public IntPtr pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public IntPtr lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCTW op);

    [DllImport("ole32.dll", EntryPoint = "CoInitializeEx")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll", EntryPoint = "CoUninitialize")]
    private static extern void CoUninitialize();

    /// <summary>把路径送进回收站，返回 SHFileOperation 的返回码（0 = 成功）。</summary>
    /// <param name="fullPath">要回收的绝对路径（文件或目录，目录会连内容一起回收）。</param>
    /// <param name="aborted">shell 是否报告"操作被中止"。</param>
    /// <returns>SHFileOperationW 的返回码；0 表示 shell 报告成功。</returns>
    internal static int Delete(string fullPath, out bool aborted)
    {
        var op = new SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = FoDelete,
            // "路径\0"：StringToHGlobalUni 自己再补一个结尾 null，落到非托管内存就是 路径\0\0（双 null 结尾）。
            pFrom = Marshal.StringToHGlobalUni(fullPath + "\0"),
            pTo = IntPtr.Zero,
            fFlags = (ushort)(FofAllowUndo | FofNoConfirmation | FofNoConfirmMkDir | FofNoErrorUi | FofSilent),
            fAnyOperationsAborted = 0,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = IntPtr.Zero,
        };

        var result = -1;
        var wasAborted = false;

        var thread = new Thread(() =>
        {
            // S_OK 与 S_FALSE 都算"这次调用成功初始化了 COM"，两种情况都要配对一次 CoUninitialize。
            var comReady = CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded) >= 0;
            try
            {
                result = SHFileOperation(ref op);
                wasAborted = op.fAnyOperationsAborted != 0;
            }
            catch (Exception)
            {
                result = -1;
            }
            finally
            {
                if (comReady)
                {
                    CoUninitialize();
                }
            }
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (op.pFrom != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(op.pFrom);
        }

        aborted = wasAborted;
        return result;
    }

    /// <summary>把 SHFileOperationW 的返回码翻译成一句给模型看的英文；未知码带上十六进制值。</summary>
    internal static string Describe(int code) => code switch
    {
        0x75 => "the shell reported the operation was cancelled",
        0x78 => "access to the file was denied",
        0x7C => "the shell rejected the path (invalid file name)",
        0x85 => "the item is too large for the Recycle Bin",
        0x86 => "Windows could not move it to the Recycle Bin",
        0x10074 => "the operation failed at the destination",
        _ => $"the shell returned error 0x{code:X}",
    };
}
