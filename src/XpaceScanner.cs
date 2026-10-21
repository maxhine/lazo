using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Lazo
{
    /// <summary>Una carpeta del análisis: lo suyo (archivos directos) y el total acumulado de todo lo que contiene.</summary>
    internal sealed class XNode
    {
        public string Name;
        public XNode Parent;
        public List<XNode> Children = new List<XNode>();
        /// <summary>Bytes y cantidad de los archivos que están directamente en esta carpeta.</summary>
        public long OwnSize, OwnFiles;
        /// <summary>Totales con todo el contenido (se calculan al terminar).</summary>
        public long Size, Files;
        public int Folders;
        /// <summary>Última modificación (FILETIME) de lo más reciente que contiene.</summary>
        public long Modified;
        public bool Denied;

        public string FullPath(string rootPath)
        {
            List<string> parts = new List<string>();
            for (XNode n = this; n != null && n.Parent != null; n = n.Parent) parts.Add(n.Name);
            parts.Reverse();
            return parts.Count == 0 ? rootPath : rootPath.TrimEnd('\\') + "\\" + string.Join("\\", parts);
        }
    }

    internal struct XFile
    {
        public string Name;
        public XNode Folder;
        public long Size, Modified;
    }

    internal sealed class XType
    {
        public string Extension;
        public long Size, Count;
    }

    internal sealed class XResult
    {
        public string Path;
        public XNode Root;
        public List<XFile> Largest;
        public List<XType> Types;
        public long Files, Bytes;
        public int Folders, Denied;
        public TimeSpan Elapsed;
        public bool Cancelled;
    }

    /// <summary>
    /// Recorre un árbol de carpetas lo más rápido que permite Windows sin ser administrador: FindFirstFileEx con
    /// lectura en bloques grandes y sin nombres cortos, varios hilos repartiéndose las carpetas, y nada de abrir
    /// los archivos. Mide tamaño lógico. No sigue enlaces ni uniones (evita ciclos y contar dos veces).
    /// </summary>
    internal sealed class XpaceScanner
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FindData
        {
            public uint Attributes;
            public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
            public uint SizeHigh, SizeLow, Reserved0, Reserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string Alternate;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileExW(string name, int infoLevel, out FindData data, int searchOp, IntPtr filter, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool FindNextFileW(IntPtr handle, out FindData data);
        [DllImport("kernel32.dll")]
        private static extern bool FindClose(IntPtr handle);

        private const uint DirectoryAttribute = 0x10, ReparsePoint = 0x400;

        private sealed class Worker
        {
            public readonly Dictionary<string, long[]> Types = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
            public List<XFile> Big = new List<XFile>();
            public long Threshold;
        }

        // Contadores en vivo para la interfaz.
        private long _files, _bytes;
        private int _folders, _denied, _pending;
        private volatile string _current = "";
        public long Files { get { return Interlocked.Read(ref _files); } }
        public long Bytes { get { return Interlocked.Read(ref _bytes); } }
        public int Folders { get { return _folders; } }
        public string Current { get { return _current; } }

        public XResult Scan(string path, CancellationToken cancel)
        {
            DateTime started = DateTime.UtcNow;
            string root = path.Length > 3 ? path.TrimEnd('\\') : path.TrimEnd('\\');
            XNode top = new XNode { Name = root };
            BlockingCollection<XNode> queue = new BlockingCollection<XNode>();
            List<Worker> workers = new List<Worker>();
            _pending = 1;
            queue.Add(top);
            int count = Math.Max(4, Math.Min(32, Environment.ProcessorCount * 2));
            Thread[] threads = new Thread[count];
            for (int t = 0; t < count; t++)
            {
                Worker worker = new Worker();
                workers.Add(worker);
                threads[t] = new Thread(() =>
                {
                    foreach (XNode node in queue.GetConsumingEnumerable())
                    {
                        try { if (!cancel.IsCancellationRequested) Process(node, root, worker, queue); }
                        catch { node.Denied = true; }
                        finally { if (Interlocked.Decrement(ref _pending) == 0) queue.CompleteAdding(); }
                    }
                }) { IsBackground = true, Name = "Xpace " + t };
                threads[t].Start();
            }
            foreach (Thread thread in threads) thread.Join();

            Totals(top);
            Dictionary<string, XType> types = new Dictionary<string, XType>(StringComparer.OrdinalIgnoreCase);
            List<XFile> big = new List<XFile>();
            foreach (Worker worker in workers)
            {
                big.AddRange(worker.Big);
                foreach (KeyValuePair<string, long[]> pair in worker.Types)
                {
                    XType type;
                    if (!types.TryGetValue(pair.Key, out type)) types[pair.Key] = type = new XType { Extension = pair.Key };
                    type.Size += pair.Value[0];
                    type.Count += pair.Value[1];
                }
            }
            return new XResult
            {
                Path = root, Root = top,
                Largest = big.OrderByDescending(f => f.Size).Take(300).ToList(),
                Types = types.Values.OrderByDescending(v => v.Size).ToList(),
                Files = top.Files, Bytes = top.Size, Folders = top.Folders, Denied = _denied,
                Elapsed = DateTime.UtcNow - started, Cancelled = cancel.IsCancellationRequested
            };
        }

        private static string Prefix(string path)
        {
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) return path;
            if (path.StartsWith("\\\\", StringComparison.Ordinal)) return "\\\\?\\UNC\\" + path.Substring(2);
            return "\\\\?\\" + path;
        }

        private void Process(XNode node, string rootPath, Worker worker, BlockingCollection<XNode> queue)
        {
            string folder = node.FullPath(rootPath);
            _current = folder;
            FindData data;
            IntPtr handle = FindFirstFileExW(Prefix(folder.TrimEnd('\\')) + "\\*", 1, out data, 0, IntPtr.Zero, 2);
            if (handle == new IntPtr(-1)) { node.Denied = true; Interlocked.Increment(ref _denied); return; }
            try
            {
                do
                {
                    string name = data.Name;
                    if (name == "." || name == "..") continue;
                    long write = ((long)data.WriteHigh << 32) | data.WriteLow;
                    if ((data.Attributes & DirectoryAttribute) != 0)
                    {
                        if ((data.Attributes & ReparsePoint) != 0) continue;
                        XNode child = new XNode { Name = name, Parent = node, Modified = write };
                        node.Children.Add(child);
                        Interlocked.Increment(ref _folders);
                        Interlocked.Increment(ref _pending);
                        queue.Add(child);
                    }
                    else
                    {
                        long size = ((long)data.SizeHigh << 32) | data.SizeLow;
                        node.OwnSize += size;
                        node.OwnFiles++;
                        if (write > node.Modified) node.Modified = write;
                        Interlocked.Increment(ref _files);
                        Interlocked.Add(ref _bytes, size);
                        string ext = Path.GetExtension(name).ToLowerInvariant();
                        if (ext.Length == 0) ext = "(sin extensión)";
                        long[] stat;
                        if (!worker.Types.TryGetValue(ext, out stat)) worker.Types[ext] = stat = new long[2];
                        stat[0] += size;
                        stat[1]++;
                        if (size > worker.Threshold)
                        {
                            worker.Big.Add(new XFile { Name = name, Folder = node, Size = size, Modified = write });
                            if (worker.Big.Count > 4000)
                            {
                                worker.Big = worker.Big.OrderByDescending(f => f.Size).Take(1000).ToList();
                                worker.Threshold = worker.Big[worker.Big.Count - 1].Size;
                            }
                        }
                    }
                } while (FindNextFileW(handle, out data));
            }
            finally { FindClose(handle); }
        }

        /// <summary>Suma de abajo hacia arriba y ordena cada carpeta por tamaño (sin recursión: hay árboles muy hondos).</summary>
        private static void Totals(XNode top)
        {
            List<XNode> order = new List<XNode>();
            Stack<XNode> stack = new Stack<XNode>();
            stack.Push(top);
            while (stack.Count > 0)
            {
                XNode n = stack.Pop();
                order.Add(n);
                foreach (XNode c in n.Children) stack.Push(c);
            }
            for (int i = order.Count - 1; i >= 0; i--)
            {
                XNode n = order[i];
                n.Size += n.OwnSize;
                n.Files += n.OwnFiles;
                if (n.Children.Count > 1) n.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
                if (n.Parent != null)
                {
                    n.Parent.Size += n.Size;
                    n.Parent.Files += n.Files;
                    n.Parent.Folders += n.Folders + 1;
                    if (n.Modified > n.Parent.Modified) n.Parent.Modified = n.Modified;
                }
            }
        }
    }
}
