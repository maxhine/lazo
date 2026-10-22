using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using Lazo;

internal static class EverythingSmoke
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CopyData { public IntPtr DataId; public int ByteCount; public IntPtr Data; }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, ref CopyData data);

    [STAThread]
    private static int Main()
    {
        List<string> results = null;
        string error = null;
        using (HwndSource server = new HwndSource(new HwndSourceParameters("Everything mock")))
        using (HwndSource client = new HwndSource(new HwndSourceParameters("Lazo search test")))
        {
            server.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message != 0x4A) return IntPtr.Zero;
                CopyData query = (CopyData)Marshal.PtrToStructure(lParam, typeof(CopyData));
                string text = Marshal.PtrToStringUni(IntPtr.Add(query.Data, 20));
                if (query.DataId.ToInt32() != 2 || text != "file: neural" ||
                    Marshal.ReadInt32(query.Data, 16) != 24) throw new Exception("Consulta IPC incorrecta.");
                byte[] name = Encoding.Unicode.GetBytes("diseño-neural.pdf\0");
                byte[] folder = Encoding.Unicode.GetBytes("C:\\Documentos\0");
                byte[] packet = new byte[40 + name.Length + folder.Length];
                Buffer.BlockCopy(BitConverter.GetBytes(1), 0, packet, 16, 4); // numfiles
                Buffer.BlockCopy(BitConverter.GetBytes(1), 0, packet, 20, 4); // numitems
                Buffer.BlockCopy(BitConverter.GetBytes(40), 0, packet, 32, 4); // filename offset
                Buffer.BlockCopy(BitConverter.GetBytes(40 + name.Length), 0, packet, 36, 4); // path offset
                Buffer.BlockCopy(name, 0, packet, 40, name.Length);
                Buffer.BlockCopy(folder, 0, packet, 40 + name.Length, folder.Length);
                IntPtr memory = Marshal.AllocHGlobal(packet.Length);
                try
                {
                    Marshal.Copy(packet, 0, memory, packet.Length);
                    CopyData reply = new CopyData { DataId = new IntPtr(Marshal.ReadInt32(query.Data, 4)),
                        ByteCount = packet.Length, Data = memory };
                    long target = unchecked((uint)Marshal.ReadInt32(query.Data));
                    SendMessage(new IntPtr(target), 0x4A, server.Handle, ref reply);
                }
                finally { Marshal.FreeHGlobal(memory); }
                handled = true;
                return new IntPtr(1);
            });
            using (EverythingSearch search = new EverythingSearch(client.Handle,
                paths => results = paths, message => error = message, server.Handle))
                search.Search("neural");
        }
        if (error != null || results == null || results.Count != 1 ||
            results[0] != Path.Combine(@"C:\Documentos", "diseño-neural.pdf"))
            throw new Exception("Falló la respuesta IPC: " + error);
        Console.WriteLine("Everything IPC: consulta y respuesta Unicode correctas.");
        return 0;
    }
}
