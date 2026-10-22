using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace Lazo
{
    // Everything's documented local WM_COPYDATA IPC. No SDK DLL is shipped with Lazo.
    internal sealed class EverythingSearch : IDisposable
    {
        private const int WmCopyData = 0x004A;
        private const int QueryUnicode = 2;
        private const int ReplyId = 0x4C415A4F;
        private const int MaxResults = 24;
        private readonly HwndSource _source;
        private readonly IntPtr _serverOverride;
        private readonly Action<List<string>> _onResults;
        private readonly Action<string> _onError;
        private bool _pending;

        [StructLayout(LayoutKind.Sequential)]
        private struct CopyData
        {
            public IntPtr DataId;
            public int ByteCount;
            public IntPtr Data;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string title);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam,
            ref CopyData data, uint flags, uint timeout, out IntPtr result);

        public EverythingSearch(IntPtr windowHandle, Action<List<string>> onResults, Action<string> onError,
            IntPtr serverOverride = default(IntPtr))
        {
            _source = HwndSource.FromHwnd(windowHandle);
            _serverOverride = serverOverride;
            _onResults = onResults;
            _onError = onError;
            _source.AddHook(WndProc);
        }

        public void Search(string query)
        {
            IntPtr everything = _serverOverride != IntPtr.Zero ? _serverOverride : FindWindow("EVERYTHING_TASKBAR_NOTIFICATION", null);
            if (everything == IntPtr.Zero)
            {
                _pending = false;
                _onError("Abre Everything para buscar. También puedes elegir o arrastrar un archivo.");
                return;
            }
            // Five DWORD fields followed by a UTF-16 string including its null terminator.
            string text = "file: " + query.Trim();
            byte[] search = Encoding.Unicode.GetBytes(text + "\0");
            byte[] packet = new byte[20 + search.Length];
            Buffer.BlockCopy(BitConverter.GetBytes(unchecked((uint)_source.Handle.ToInt64())), 0, packet, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(ReplyId), 0, packet, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(MaxResults), 0, packet, 16, 4);
            Buffer.BlockCopy(search, 0, packet, 20, search.Length);
            IntPtr memory = Marshal.AllocHGlobal(packet.Length);
            try
            {
                Marshal.Copy(packet, 0, memory, packet.Length);
                CopyData data = new CopyData { DataId = new IntPtr(QueryUnicode), ByteCount = packet.Length, Data = memory };
                IntPtr accepted;
                _pending = true;
                IntPtr sent = SendMessageTimeout(everything, WmCopyData, _source.Handle, ref data, 0x0002, 1500, out accepted);
                if (sent == IntPtr.Zero || accepted == IntPtr.Zero)
                {
                    _pending = false;
                    _onError("Everything no respondió. Abre Everything o elige un archivo.");
                }
            }
            finally { Marshal.FreeHGlobal(memory); }
        }

        private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != WmCopyData || !_pending) return IntPtr.Zero;
            CopyData data = (CopyData)Marshal.PtrToStructure(lParam, typeof(CopyData));
            if (data.DataId.ToInt64() != ReplyId) return IntPtr.Zero;
            handled = true;
            _pending = false;
            try { _onResults(ParseResults(data)); }
            catch { _onError("No se pudieron leer los resultados de Everything."); }
            return new IntPtr(1);
        }

        private static List<string> ParseResults(CopyData data)
        {
            List<string> results = new List<string>();
            if (data.ByteCount < 28 || data.ByteCount > 4 * 1024 * 1024) throw new InvalidOperationException();
            int count = Marshal.ReadInt32(data.Data, 20);
            if (count < 0 || count > MaxResults || 28 + count * 12 > data.ByteCount) throw new InvalidOperationException();
            for (int i = 0; i < count; i++)
            {
                int item = 28 + i * 12;
                int flags = Marshal.ReadInt32(data.Data, item);
                if ((flags & 1) != 0) continue;
                int nameOffset = Marshal.ReadInt32(data.Data, item + 4);
                int pathOffset = Marshal.ReadInt32(data.Data, item + 8);
                string name = ReadString(data, nameOffset);
                string folder = ReadString(data, pathOffset);
                if (name.Length > 0 && folder.Length > 0)
                    results.Add(System.IO.Path.Combine(folder, name));
            }
            return results;
        }

        private static string ReadString(CopyData data, int offset)
        {
            if (offset < 28 || offset >= data.ByteCount || (offset & 1) != 0) throw new InvalidOperationException();
            int end = offset;
            while (end + 1 < data.ByteCount)
            {
                if (Marshal.ReadInt16(data.Data, end) == 0)
                    return Marshal.PtrToStringUni(IntPtr.Add(data.Data, offset), (end - offset) / 2);
                end += 2;
            }
            throw new InvalidOperationException();
        }

        public void Dispose() { _source.RemoveHook(WndProc); }
    }
}
