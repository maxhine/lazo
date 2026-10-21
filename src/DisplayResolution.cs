using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Lazo
{
    /// <summary>
    /// Cambio temporal de resolución del monitor compartido, como el escritorio remoto. Usa <c>CDS_FULLSCREEN</c>: Windows lo
    /// revierte si Lazo se cierra, y <see cref="Restore"/> devuelve el modo guardado en el registro.
    /// </summary>
    internal sealed class DisplayResolution
    {
        private const int EnumCurrent = -1;
        private const uint CdsFullscreen = 4;
        private const uint DmPelsWidth = 0x80000, DmPelsHeight = 0x100000, DmDisplayFrequency = 0x400000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public uint dmFields;
            public int dmPositionX, dmPositionY;
            public uint dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public Native.RECT rcMonitor, rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoEx(IntPtr monitor, ref MONITORINFOEX info);
        [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE devMode);
        [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE devMode, IntPtr hwnd, uint flags, IntPtr param);
        [DllImport("user32.dll", EntryPoint = "ChangeDisplaySettingsExW", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsExReset(string device, IntPtr devMode, IntPtr hwnd, uint flags, IntPtr param);

        private readonly ShareSource _source;
        private string _device;
        private bool _changed;

        public DisplayResolution(ShareSource source)
        {
            _source = source;
        }

        /// <summary>Ahora mismo hay un cambio hecho por Lazo que falta restaurar.</summary>
        public bool Changed { get { return _changed; } }

        /// <summary>Modos de 32 bits o más que ofrece el monitor compartido.</summary>
        public List<DisplayMode> Modes()
        {
            List<DisplayMode> modes = new List<DisplayMode>();
            string device = Device();
            if (device == null) return modes;
            DEVMODE mode = NewMode();
            for (int i = 0; EnumDisplaySettings(device, i, ref mode); i++)
            {
                if (mode.dmBitsPerPel < 32) continue;
                modes.Add(new DisplayMode((int)mode.dmPelsWidth, (int)mode.dmPelsHeight, (int)mode.dmDisplayFrequency));
                mode = NewMode();
            }
            return modes;
        }

        /// <summary>Ajusta el monitor a lo que mejor sirve a una ventana de <paramref name="width"/> × <paramref name="height"/> píxeles. Devuelve el texto del modo o null si no se pudo.</summary>
        public string Fit(int width, int height)
        {
            if (_source == null || _source.IsWindow) return null;
            string device = Device();
            if (device == null) return null;
            DEVMODE current = NewMode();
            if (!EnumDisplaySettings(device, EnumCurrent, ref current)) return null;
            DisplayMode chosen;
            if (!DisplayModePicker.Choose(Modes(), width, height, (int)current.dmDisplayFrequency, out chosen)) return null;
            if (chosen.Width == (int)current.dmPelsWidth && chosen.Height == (int)current.dmPelsHeight)
                return chosen.ToString();
            DEVMODE target = current;
            target.dmPelsWidth = (uint)chosen.Width;
            target.dmPelsHeight = (uint)chosen.Height;
            target.dmFields = DmPelsWidth | DmPelsHeight;
            if (chosen.Frequency > 0) { target.dmDisplayFrequency = (uint)chosen.Frequency; target.dmFields |= DmDisplayFrequency; }
            int result = ChangeDisplaySettingsEx(device, ref target, IntPtr.Zero, CdsFullscreen, IntPtr.Zero);
            if (result != 0) return null;
            _changed = true;
            ScreenSources.Refresh(_source);
            return chosen.ToString();
        }

        /// <summary>Devuelve el monitor al modo guardado en el registro (el que tenía antes de ajustarlo).</summary>
        public bool Restore()
        {
            if (!_changed) return false;
            _changed = false;
            string device = Device();
            if (device == null) return false;
            bool ok = ChangeDisplaySettingsExReset(device, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero) == 0;
            ScreenSources.Refresh(_source);
            return ok;
        }

        private static DEVMODE NewMode()
        {
            DEVMODE mode = new DEVMODE();
            mode.dmDeviceName = "";
            mode.dmFormName = "";
            mode.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));
            return mode;
        }

        /// <summary>Nombre de dispositivo del monitor compartido («\\.\DISPLAY1»).</summary>
        private string Device()
        {
            if (_device != null) return _device;
            if (_source == null || _source.IsWindow || _source.Handle == IntPtr.Zero) return null;
            IntPtr previous = Native.EnterPerMonitorDpi();
            try
            {
                MONITORINFOEX info = new MONITORINFOEX();
                info.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                info.szDevice = "";
                if (!GetMonitorInfoEx(_source.Handle, ref info)) return null;
                _device = info.szDevice;
                return _device;
            }
            finally { Native.LeaveDpi(previous); }
        }
    }
}
