// MonitorBar - tray popover for DDC/CI monitor control.
// Targets .NET Framework 4.x (C# 5) so it compiles with the in-box csc.exe.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace MonitorBar
{
    // ---------------------------------------------------------------- native

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data);

        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprc, MonitorEnumProc cb, IntPtr data);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr h, ref uint n);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr h, uint n, [Out] PHYSICAL_MONITOR[] p);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetMonitorBrightness(IntPtr h, ref uint min, ref uint cur, ref uint max);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool SetMonitorBrightness(IntPtr h, uint value);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, ref uint cur, ref uint max);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool SetVCPFeature(IntPtr h, byte code, uint value);

        [DllImport("dxva2.dll", SetLastError = true)]
        public static extern bool GetCapabilitiesStringLength(IntPtr h, ref uint len);

        [DllImport("dxva2.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr h, StringBuilder sb, uint len);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        public const int WH_MOUSE_LL = 14;
        public const int WM_MOUSEWHEEL = 0x020A;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);
    }

    // ------------------------------------------------------------- ddc layer

    public sealed class MonitorState
    {
        public string Model = "显示器";
        public string Mccs = "";
        public readonly List<byte> Codes = new List<byte>();
        public readonly Dictionary<byte, List<byte>> Values = new Dictionary<byte, List<byte>>();
        public bool Ok;

        public bool BrightnessOk; public uint BrightCur, BrightMin, BrightMax;
        public bool ContrastOk; public uint ContrastCur, ContrastMax;

        public bool GainOk; public uint GainR, GainG, GainB, GainMax;
        public bool OffsetOk; public uint OffR, OffG, OffB, OffMax;

        public string Error;

        public bool Supports(byte code) { return Codes.Contains(code); }
    }

    public sealed class DdcWorker : IDisposable
    {
        const byte VCP_BRIGHTNESS = 0x10;
        const byte VCP_CONTRAST = 0x12;
        const byte VCP_GAIN_R = 0x16;
        const byte VCP_GAIN_G = 0x18;
        const byte VCP_GAIN_B = 0x1A;
        const byte VCP_OFF_R = 0x6C;
        const byte VCP_OFF_G = 0x6E;
        const byte VCP_OFF_B = 0x70;

        readonly object _lock = new object();
        readonly Dictionary<byte, uint> _pending = new Dictionary<byte, uint>();
        readonly AutoResetEvent _signal = new AutoResetEvent(false);
        readonly List<Native.PHYSICAL_MONITOR> _monitors = new List<Native.PHYSICAL_MONITOR>();
        Native.MonitorEnumProc _enumProc;

        Thread _thread;
        volatile bool _stop;
        volatile bool _readRequested;
        IntPtr _handle = IntPtr.Zero;
        bool _ready;
        string _model = "显示器";
        string _capsRaw = "";
        volatile int _brightness = -1;

        public event Action<MonitorState> StateUpdated;
        public event Action<string> Faulted;

        public DdcWorker()
        {
            string model, caps;
            if (IdentityCache.TryLoad(out model, out caps))
            {
                _model = model;
                _capsRaw = caps;
            }
        }

        public int Brightness { get { return _brightness; } }
        public void NoteBrightness(int value) { _brightness = value; }

        public void Start()
        {
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "ddc-worker";
            _thread.Start();
        }

        public void RequestState()
        {
            _readRequested = true;
            _signal.Set();
        }

        public void Set(byte code, uint value)
        {
            lock (_lock) { _pending[code] = value; }
            if (code == VCP_BRIGHTNESS) _brightness = (int)value;
            _signal.Set();
        }

        public MonitorState ReadStateSync()
        {
            return ReadAll();
        }

        void Loop()
        {
            while (!_stop)
            {
                _signal.WaitOne(250);
                if (_stop) break;

                if (_readRequested)
                {
                    _readRequested = false;
                    MonitorState st = ReadAll();
                    Action<MonitorState> h = StateUpdated;
                    if (h != null) h(st);
                }

                Dictionary<byte, uint> work = null;
                lock (_lock)
                {
                    if (_pending.Count > 0)
                    {
                        work = new Dictionary<byte, uint>(_pending);
                        _pending.Clear();
                    }
                }

                if (work != null)
                {
                    foreach (KeyValuePair<byte, uint> kv in work)
                    {
                        if (!EnsureHandle()) break;
                        bool ok = kv.Key == VCP_BRIGHTNESS
                            ? Native.SetMonitorBrightness(_handle, kv.Value)
                            : Native.SetVCPFeature(_handle, kv.Key, kv.Value);
                        if (!ok)
                        {
                            _ready = false;
                            _handle = IntPtr.Zero;
                            RaiseFault("写入失败（VCP 0x" + kv.Key.ToString("X2") + "）");
                        }
                    }
                }
            }
        }

        void RaiseFault(string msg)
        {
            Action<string> h = Faulted;
            if (h != null) h(msg);
        }

        bool EnsureHandle()
        {
            if (_ready) return true;
            if (!Enumerate()) return false;
            return _ready;
        }

        bool Enumerate()
        {
            _monitors.Clear();
            _enumProc = delegate(IntPtr hm, IntPtr hdc, ref Native.RECT r, IntPtr d)
            {
                uint n = 0;
                if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hm, ref n) || n == 0) return true;
                Native.PHYSICAL_MONITOR[] arr = new Native.PHYSICAL_MONITOR[n];
                if (Native.GetPhysicalMonitorsFromHMONITOR(hm, n, arr))
                {
                    for (int i = 0; i < arr.Length; i++) _monitors.Add(arr[i]);
                }
                return true;
            };
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, _enumProc, IntPtr.Zero);
            DebugLog.Write("enum monitors=" + _monitors.Count);

            foreach (Native.PHYSICAL_MONITOR pm in _monitors)
            {
                uint min = 0, cur = 0, max = 0;
                if (Native.GetMonitorBrightness(pm.hPhysicalMonitor, ref min, ref cur, ref max))
                {
                    _handle = pm.hPhysicalMonitor;
                    _ready = true;
                    ReadIdentity();
                    return true;
                }
            }
            return false;
        }

        void ReadIdentity()
        {
            _model = EdidReader.ModelName();
            if (_capsRaw.Length > 0) return;
            uint len = 0;
            if (Native.GetCapabilitiesStringLength(_handle, ref len) && len > 0 && len < 65535)
            {
                StringBuilder sb = new StringBuilder((int)len);
                if (Native.CapabilitiesRequestAndCapabilitiesReply(_handle, sb, len))
                    _capsRaw = sb.ToString();
            }
            IdentityCache.Save(_model, _capsRaw);
        }

        bool ReadVcp(byte code, out uint cur, out uint max)
        {
            cur = 0; max = 0;
            if (!EnsureHandle()) return false;
            return Native.GetVCPFeatureAndVCPFeatureReply(_handle, code, IntPtr.Zero, ref cur, ref max);
        }

        MonitorState ReadAll()
        {
            MonitorState st = new MonitorState();

            if (!EnsureHandle())
            {
                st.Ok = false;
                st.Error = "未检测到支持 DDC/CI 的显示器";
                return st;
            }

            st.Model = _model;
            st.Mccs = ParseMccs(_capsRaw);
            ParseCodes(_capsRaw, st.Codes, st.Values);

            uint min = 0, cur = 0, max = 0;
            if (Native.GetMonitorBrightness(_handle, ref min, ref cur, ref max))
            {
                st.BrightnessOk = true;
                st.BrightMin = min; st.BrightCur = cur; st.BrightMax = max;
                _brightness = (int)cur;
            }

            uint v = 0, m = 0;
            if (Native.GetVCPFeatureAndVCPFeatureReply(_handle, VCP_CONTRAST, IntPtr.Zero, ref v, ref m))
            { st.ContrastOk = true; st.ContrastCur = v; st.ContrastMax = m; }

            uint a = 0, b = 0, c = 0, mx = 0;
            if (ReadVcp(VCP_GAIN_R, out a, out mx) && ReadVcp(VCP_GAIN_G, out b, out mx) && ReadVcp(VCP_GAIN_B, out c, out mx))
            {
                st.GainOk = true;
                st.GainR = a; st.GainG = b; st.GainB = c; st.GainMax = mx;
            }

            if (ReadVcp(VCP_OFF_R, out a, out mx) && ReadVcp(VCP_OFF_G, out b, out mx) && ReadVcp(VCP_OFF_B, out c, out mx))
            {
                st.OffsetOk = true;
                st.OffR = a; st.OffG = b; st.OffB = c; st.OffMax = mx;
            }

            st.Ok = st.BrightnessOk || st.ContrastOk;
            if (!st.Ok) st.Error = "显示器未响应 DDC/CI 请求";
            return st;
        }

        static string ParseMccs(string caps)
        {
            int i = caps.IndexOf("mccs_ver(", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            int j = caps.IndexOf(')', i);
            if (j < 0) return "";
            return caps.Substring(i + 9, j - i - 9);
        }

        static void ParseCodes(string caps, List<byte> into, Dictionary<byte, List<byte>> values)
        {
            int i = caps.IndexOf("vcp(", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return;

            StringBuilder buf = new StringBuilder();
            int p = i + 4;
            byte currentCode = 0;
            bool hasCode = false;
            List<byte> pending = null;

            while (p < caps.Length)
            {
                char c = caps[p];
                if (c == '(')
                {
                    byte parsed;
                    hasCode = TryHex(buf.ToString(), out parsed);
                    currentCode = parsed;
                    buf.Length = 0;
                    if (hasCode) pending = new List<byte>();
                    p++;
                    continue;
                }
                if (c == ')')
                {
                    if (pending != null)
                    {
                        byte parsed;
                        if (TryHex(buf.ToString(), out parsed)) pending.Add(parsed);
                        buf.Length = 0;
                        if (hasCode && !values.ContainsKey(currentCode)) values[currentCode] = pending;
                        pending = null;
                    }
                    else
                    {
                        break;
                    }
                    p++;
                    continue;
                }
                if (c == ' ')
                {
                    string tok = buf.ToString();
                    buf.Length = 0;
                    byte parsed;
                    if (TryHex(tok, out parsed))
                    {
                        if (pending != null) pending.Add(parsed);
                        else if (!into.Contains(parsed)) into.Add(parsed);
                    }
                    p++;
                    continue;
                }
                buf.Append(c);
                p++;
            }
        }

        static bool TryHex(string token, out byte value)
        {
            value = 0;
            if (token == null || token.Length != 2) return false;
            return byte.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out value);
        }

        public void Dispose()
        {
            _stop = true;
            _signal.Set();
        }
    }

    // ----------------------------------------------------------------- edid

    internal static class EdidReader
    {
        public static string ModelName()
        {
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY"))
                {
                    if (root == null) return "显示器";
                    foreach (string vendor in root.GetSubKeyNames())
                    {
                        using (RegistryKey vk = root.OpenSubKey(vendor))
                        {
                            if (vk == null) continue;
                            foreach (string inst in vk.GetSubKeyNames())
                            {
                                using (RegistryKey dp = vk.OpenSubKey(inst + @"\Device Parameters"))
                                {
                                    if (dp == null) continue;
                                    byte[] edid = dp.GetValue("EDID") as byte[];
                                    if (edid == null || edid.Length < 128) continue;
                                    string name = ParseDescriptor(edid);
                                    if (!string.IsNullOrEmpty(name)) return name.Trim();
                                }
                            }
                        }
                    }
                    return "显示器";
                }
            }
            catch { return "显示器"; }
        }

        static string ParseDescriptor(byte[] e)
        {
            for (int i = 54; i <= 108; i += 18)
            {
                if (e[i] != 0 || e[i + 1] != 0 || e[i + 3] != 0xFC) continue;
                StringBuilder sb = new StringBuilder();
                for (int k = 5; k < 18; k++)
                {
                    byte b = e[i + k];
                    if (b == 0x0A || b == 0x00) break;
                    if (b >= 32 && b < 127) sb.Append((char)b);
                }
                return sb.ToString();
            }
            return null;
        }
    }

    // ------------------------------------------------------------ utilities

    internal static class DebugLog
    {
        static readonly object Gate = new object();
        static string _path;

        public static bool Enabled
        {
            get
            {
                if (_path == null) _path = Environment.GetEnvironmentVariable("MONITORBAR_LOG");
                return !string.IsNullOrEmpty(_path);
            }
        }

        public static void Write(string line)
        {
            if (!Enabled) return;
            try
            {
                lock (Gate)
                    System.IO.File.AppendAllText(_path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
            }
            catch { }
        }
    }

    internal static class Paths
    {
        public static string Dir
        {
            get
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonitorBar");
            }
        }

        public static string Identity { get { return System.IO.Path.Combine(Dir, "identity.txt"); } }
        public static string Presets { get { return System.IO.Path.Combine(Dir, "presets.txt"); } }
    }

    internal static class IdentityCache
    {
        public static bool TryLoad(out string model, out string caps)
        {
            model = null; caps = null;
            try
            {
                if (!System.IO.File.Exists(Paths.Identity)) return false;
                string[] lines = System.IO.File.ReadAllLines(Paths.Identity);
                if (lines.Length < 2) return false;
                model = lines[0];
                caps = lines[1];
                return model.Length > 0 && caps.Length > 0;
            }
            catch { return false; }
        }

        public static void Save(string model, string caps)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Paths.Dir);
                System.IO.File.WriteAllLines(Paths.Identity, new string[] { model ?? "", caps ?? "" });
            }
            catch { }
        }
    }

    // -------------------------------------------------------------- presets

    internal sealed class ProfilePreset
    {
        public string Name;
        public int Contrast;
        public int GainR, GainG, GainB;
        public int OffR, OffG, OffB;

        public override string ToString()
        {
            return Name + "|" + Contrast + "|" + GainR + "|" + GainG + "|" + GainB
                 + "|" + OffR + "|" + OffG + "|" + OffB;
        }
    }

    /// <summary>
    /// User-authored presets, one editable line each:
    /// name|contrast|gainR|gainG|gainB|offsetR|offsetG|offsetB
    /// Presets carry contrast and colour only - never brightness.
    /// </summary>
    internal static class PresetStore
    {
        public static readonly List<ProfilePreset> Items = new List<ProfilePreset>();

        const string Header =
            "# 显示器预设：一行一个，字段用竖线分隔\n" +
            "# 名称|对比度(0-100)|红增益|绿增益|蓝增益|红偏移|绿偏移|蓝偏移  (偏移 0-255)\n" +
            "# 改动保存后，下次打开面板生效；预设只影响对比度与色彩，不影响亮度。\n";

        public static void Load()
        {
            Items.Clear();
            try
            {

                if (!System.IO.File.Exists(Paths.Presets))
                {
                    Items.Add(Default("日常", 50, 50, 50, 50, 50, 50, 50));
                    Items.Add(Default("游戏", 62, 52, 50, 48, 50, 50, 50));
                    Save();
                    return;
                }

                foreach (string raw in System.IO.File.ReadAllLines(Paths.Presets))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    ProfilePreset p = Parse(line);
                    if (p != null) Items.Add(p);
                }
            }
            catch { }

            if (Items.Count == 0)
            {
                Items.Add(Default("日常", 50, 50, 50, 50, 50, 50, 50));
                Items.Add(Default("游戏", 62, 52, 50, 48, 50, 50, 50));
            }
        }

        static ProfilePreset Default(string name, int contrast, int gr, int gg, int gb, int or_, int og, int ob)
        {
            ProfilePreset p = new ProfilePreset();
            p.Name = name;
            p.Contrast = contrast;
            p.GainR = gr; p.GainG = gg; p.GainB = gb;
            p.OffR = or_; p.OffG = og; p.OffB = ob;
            return p;
        }

        static ProfilePreset Parse(string line)
        {
            string[] parts = line.Split('|');
            if (parts.Length < 8) return null;
            ProfilePreset p = new ProfilePreset();
            p.Name = parts[0].Trim();
            if (p.Name.Length == 0) return null;
            int.TryParse(parts[1].Trim(), out p.Contrast);
            int.TryParse(parts[2].Trim(), out p.GainR);
            int.TryParse(parts[3].Trim(), out p.GainG);
            int.TryParse(parts[4].Trim(), out p.GainB);
            int.TryParse(parts[5].Trim(), out p.OffR);
            int.TryParse(parts[6].Trim(), out p.OffG);
            int.TryParse(parts[7].Trim(), out p.OffB);
            return p;
        }

        public static void Save()
        {
            try
            {
                System.IO.Directory.CreateDirectory(Paths.Dir);
                List<string> lines = new List<string>();
                lines.Add(Header);
                foreach (ProfilePreset p in Items) lines.Add(p.ToString());
                System.IO.File.WriteAllLines(Paths.Presets, lines.ToArray(), Encoding.UTF8);
            }
            catch { }
        }

        public static void OpenInEditor()
        {
            try
            {
                if (!System.IO.File.Exists(Paths.Presets)) Save();
                System.Diagnostics.Process.Start("notepad.exe", "\"" + Paths.Presets + "\"");
            }
            catch { }
        }
    }

    // ------------------------------------------------------------ ui widgets

    public sealed class SlimSlider : FrameworkElement
    {
        public double Min = 0;
        public double Max = 100;
        double _value;

        public double Value
        {
            get { return _value; }
            set
            {
                double v = Math.Max(Min, Math.Min(Max, value));
                if (Math.Abs(v - _value) < 0.0001) return;
                _value = v;
                InvalidateVisual();
                if (ValueChanged != null) ValueChanged(_value);
            }
        }

        public event Action<double> ValueChanged;
        public event Action<double> Committed;

        bool _dragging;
        bool _hover;

        public SlimSlider()
        {
            Height = 26;
            Focusable = true;
            Cursor = Cursors.Hand;
        }

        double ThumbRadius { get { return _dragging || _hover ? 9.0 : 8.0; } }
        double TrackLeft { get { return ThumbRadius + 1; } }
        double TrackRight { get { return Math.Max(TrackLeft + 1, ActualWidth - ThumbRadius - 1); } }

        protected override void OnRender(DrawingContext dc)
        {
            double mid = ActualHeight / 2.0;
            double x0 = TrackLeft, x1 = TrackRight;
            double t = (Max > Min) ? (_value - Min) / (Max - Min) : 0;
            double x = x0 + (x1 - x0) * t;

            dc.DrawRoundedRectangle(Theme.Track, null, new Rect(x0, mid - 2, x1 - x0, 4), 2, 2);
            if (x > x0)
                dc.DrawRoundedRectangle(Theme.Accent, null, new Rect(x0, mid - 2, x - x0, 4), 2, 2);

            dc.DrawEllipse(Brushes.White, null, new Point(x, mid), ThumbRadius, ThumbRadius);
        }

        protected override void OnMouseEnter(MouseEventArgs e) { _hover = true; InvalidateVisual(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(MouseEventArgs e) { _hover = false; InvalidateVisual(); base.OnMouseLeave(e); }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (!IsEnabled) return;
            _dragging = true;
            CaptureMouse();
            Focus();
            SetFromPoint(e.GetPosition(this).X);
            e.Handled = true;
            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging) SetFromPoint(e.GetPosition(this).X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_dragging)
            {
                _dragging = false;
                ReleaseMouseCapture();
                InvalidateVisual();
                if (Committed != null) Committed(_value);
            }
            base.OnMouseLeftButtonUp(e);
        }

        void SetFromPoint(double px)
        {
            double x0 = TrackLeft, x1 = TrackRight;
            double t = (px - x0) / Math.Max(1, x1 - x0);
            t = Math.Max(0, Math.Min(1, t));
            Value = Min + (Max - Min) * t;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if (!IsEnabled) return;
            double step = (Max - Min) / 100.0 * (e.Delta > 0 ? 2 : -2);
            Value = Math.Round(_value + step);
            if (Committed != null) Committed(_value);
            e.Handled = true;
            base.OnMouseWheel(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (!IsEnabled) return;
            double step = Math.Max(1, Math.Abs((Max - Min) / 100.0 * 2));
            if (e.Key == Key.Left || e.Key == Key.Down) { Value = Math.Round(_value - step); if (Committed != null) Committed(_value); e.Handled = true; }
            else if (e.Key == Key.Right || e.Key == Key.Up) { Value = Math.Round(_value + step); if (Committed != null) Committed(_value); e.Handled = true; }
            base.OnKeyDown(e);
        }
    }

    internal static class Theme
    {
        public static readonly Brush Panel = Frozen(0xFA, 0x16, 0x16, 0x18);
        public static readonly Brush PanelStroke = Frozen(0x22, 0xFF, 0xFF, 0xFF);
        public static readonly Brush Card = Frozen(0xFF, 0x24, 0x24, 0x27);
        public static readonly Brush CardStroke = Frozen(0x14, 0xFF, 0xFF, 0xFF);
        public static readonly Brush Text = Frozen(0xFF, 0xF2, 0xF2, 0xF7);
        public static readonly Brush SubText = Frozen(0xFF, 0x8E, 0x8E, 0x93);
        public static readonly Brush Accent = Frozen(0xFF, 0x0A, 0x84, 0xFF);
        public static readonly Brush Chip = Frozen(0xFF, 0x3A, 0x3A, 0x3C);
        public static readonly Brush ChipHover = Frozen(0xFF, 0x45, 0x45, 0x48);
        public static readonly Brush Track = Frozen(0xFF, 0x3A, 0x3A, 0x3C);
        public static readonly Brush TabBar = Frozen(0xFF, 0x21, 0x21, 0x25);
        public static readonly Brush TabActive = Frozen(0xFF, 0x3A, 0x3A, 0x40);
        public static readonly Brush ChannelRed = Frozen(0xFF, 0xFF, 0x7B, 0x7B);

        static Brush Frozen(byte a, byte r, byte g, byte b)
        {
            SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    // --------------------------------------------------------------- popover

    public sealed class PanelWindow : Window
    {
        public const double PanelWidth = 392;

        readonly DdcWorker _ddc;
        readonly bool _preview;

        TextBlock _model, _sub, _status;
        TextBlock _vBright, _vContrast;
        SlimSlider _sBright, _sContrast;
        Border _cardBright;

        readonly List<SliderRow> _gainRows = new List<SliderRow>();
        readonly List<SliderRow> _offsetRows = new List<SliderRow>();

        WrapPanel _presetWrap;
        Grid _tabBarHost;
        StackPanel _displayPage, _colorPage;
        StackPanel _root;
        MonitorState _state;
        bool _loading = true;
        bool _suppress;
        int _activeTab;

        sealed class SliderRow
        {
            public TextBlock Value;
            public SlimSlider Slider;
            public byte Code;
            public bool Enabled = true;
        }

        public PanelWindow(DdcWorker ddc, bool preview)
        {
            _ddc = ddc;
            _preview = preview;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            Width = PanelWidth;
            Title = "MonitorBar";
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");

            PresetStore.Load();
            BuildShell();
            SizeChanged += delegate { Reposition(); };
            Deactivated += OnDeactivated;
            PreviewKeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) Hide(); };

            _ddc.StateUpdated += OnState;
            _ddc.Faulted += OnFault;
        }

        // ---- primitives

        TextBlock Text(string s, double size, Brush brush, FontWeight weight)
        {
            TextBlock t = new TextBlock();
            t.Text = s;
            t.FontSize = size;
            t.Foreground = brush;
            t.FontWeight = weight;
            t.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            TextOptions.SetTextFormattingMode(t, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(t, TextRenderingMode.Grayscale);
            return t;
        }

        Border Card(UIElement content, double bottom)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(14);
            b.Background = Theme.Card;
            b.BorderBrush = Theme.CardStroke;
            b.BorderThickness = new Thickness(1);
            b.Padding = new Thickness(14, 12, 14, 12);
            b.Margin = new Thickness(0, 0, 0, bottom);
            b.Child = content;
            return b;
        }

        // ---- shell

        void BuildShell()
        {
            Border frame = new Border();
            frame.CornerRadius = new CornerRadius(18);
            frame.Background = Theme.Panel;
            frame.BorderBrush = Theme.PanelStroke;
            frame.BorderThickness = new Thickness(1);
            frame.Margin = new Thickness(12, 6, 12, 10);
            DropShadowEffect shadow = new DropShadowEffect();
            shadow.Color = Colors.Black;
            shadow.BlurRadius = 26;
            shadow.ShadowDepth = 4;
            shadow.Opacity = 0.5;
            shadow.Direction = 270;
            frame.Effect = shadow;

            _root = new StackPanel();
            _root.Margin = new Thickness(16, 14, 16, 14);
            frame.Child = _root;
            Content = frame;

            _root.Children.Add(BuildHeader());
            _root.Children.Add(BuildTabBar());

            _displayPage = new StackPanel();
            _displayPage.Children.Add(BuildBrightnessCard());
            _displayPage.Children.Add(BuildCard("对比度", 0x12));
            _displayPage.Children.Add(BuildPresetCard());

            _colorPage = new StackPanel();
            _colorPage.Children.Add(BuildChannelCard("色温 · RGB 增益", 0x16, 0x18, 0x1A, _gainRows));
            _colorPage.Children.Add(BuildChannelCard("色调 · RGB 偏移", 0x6C, 0x6E, 0x70, _offsetRows));
            _colorPage.Visibility = Visibility.Collapsed;

            _root.Children.Add(_displayPage);
            _root.Children.Add(_colorPage);
            _root.Children.Add(BuildFooter());
        }

        FrameworkElement BuildHeader()
        {
            Grid g = new Grid();
            g.Margin = new Thickness(2, 2, 2, 12);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Border badge = new Border();
            badge.Width = 34;
            badge.Height = 34;
            badge.CornerRadius = new CornerRadius(9);
            badge.Background = Theme.Chip;
            FrameworkElement glyph = Glyph(IconKind.Monitor, 18, Theme.Text);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            badge.Child = glyph;
            g.Children.Add(badge);

            StackPanel texts = new StackPanel();
            texts.Margin = new Thickness(11, 0, 0, 0);
            texts.VerticalAlignment = VerticalAlignment.Center;
            _model = Text("显示器", 14, Theme.Text, FontWeights.SemiBold);
            _sub = Text("正在读取…", 11, Theme.SubText, FontWeights.Normal);
            _sub.Margin = new Thickness(0, 1, 0, 0);
            texts.Children.Add(_model);
            texts.Children.Add(_sub);
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);

            Border refresh = IconButton(IconKind.Refresh, "重新读取", delegate { _ddc.RequestState(); });
            Grid.SetColumn(refresh, 2);
            refresh.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(refresh);
            return g;
        }

        Border IconButton(IconKind icon, string tip, Action click)
        {
            Border b = new Border();
            b.Width = 30;
            b.Height = 30;
            b.CornerRadius = new CornerRadius(8);
            b.Background = Brushes.Transparent;
            b.Cursor = Cursors.Hand;
            b.ToolTip = tip;
            FrameworkElement glyph = Glyph(icon, 15, Theme.SubText);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            b.Child = glyph;
            b.MouseLeftButtonUp += delegate { click(); };
            b.MouseEnter += delegate { b.Background = Theme.ChipHover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            return b;
        }

        FrameworkElement BuildTabBar()
        {
            Border bar = new Border();
            bar.Background = Theme.TabBar;
            bar.CornerRadius = new CornerRadius(10);
            bar.Padding = new Thickness(3);
            bar.Margin = new Thickness(0, 0, 0, 10);

            _tabBarHost = new Grid();
            _tabBarHost.ColumnDefinitions.Add(new ColumnDefinition());
            _tabBarHost.ColumnDefinitions.Add(new ColumnDefinition());
            bar.Child = _tabBarHost;
            RenderTabBar();
            return bar;
        }

        void RenderTabBar()
        {
            if (_tabBarHost == null) return;
            _tabBarHost.Children.Clear();
            _tabBarHost.Children.Add(TabButton("显示", IconKind.Monitor, 0));
            Border color = TabButton("色彩", IconKind.Contrast, 1);
            Grid.SetColumn(color, 1);
            _tabBarHost.Children.Add(color);
        }

        Border TabButton(string label, IconKind icon, int index)
        {
            bool on = _activeTab == index;
            Border b = new Border();
            b.CornerRadius = new CornerRadius(8);
            b.Height = 30;
            b.Background = on ? Theme.TabActive : Brushes.Transparent;
            b.Cursor = Cursors.Hand;

            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.HorizontalAlignment = HorizontalAlignment.Center;
            sp.VerticalAlignment = VerticalAlignment.Center;

            FrameworkElement ic = Glyph(icon, 13, on ? Theme.Text : Theme.SubText);
            ic.Margin = new Thickness(0, 0, 6, 0);
            sp.Children.Add(ic);
            sp.Children.Add(Text(label, 12.5, on ? Theme.Text : Theme.SubText, FontWeights.Medium));

            b.Child = sp;
            b.MouseLeftButtonUp += delegate { SetTab(index); };
            return b;
        }

        void SetTab(int index)
        {
            if (_activeTab == index) return;
            _activeTab = index;
            _displayPage.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            _colorPage.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            RenderTabBar();
        }

        // ---- cards

        Border BuildBrightnessCard()
        {
            TextBlock value = Text("—", 21, Theme.Text, FontWeights.Bold);
            value.HorizontalAlignment = HorizontalAlignment.Right;

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock label = Text("亮度", 13, Theme.SubText, FontWeights.Medium);
            label.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(label);
            Grid.SetColumn(value, 1);
            head.Children.Add(value);
            _vBright = value;

            _sBright = new SlimSlider();
            _sBright.Max = 100;
            _sBright.Margin = new Thickness(-2, 8, -2, -2);
            _sBright.ValueChanged += delegate(double v)
            {
                if (_loading || _suppress) return;
                _vBright.Text = ((int)Math.Round(v)).ToString();
                _ddc.Set(0x10, (uint)Math.Round(v));
            };

            StackPanel sp = new StackPanel();
            sp.Children.Add(head);
            sp.Children.Add(_sBright);
            _cardBright = Card(sp, 8);
            return _cardBright;
        }

        Border BuildCard(string title, byte code)
        {
            TextBlock value = Text("—", 21, Theme.Text, FontWeights.Bold);
            value.HorizontalAlignment = HorizontalAlignment.Right;

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock label = Text(title, 13, Theme.SubText, FontWeights.Medium);
            label.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(label);
            Grid.SetColumn(value, 1);
            head.Children.Add(value);

            SlimSlider slider = new SlimSlider();
            slider.Max = 100;
            slider.Margin = new Thickness(-2, 8, -2, -2);
            TextBlock captured = value;
            slider.ValueChanged += delegate(double v)
            {
                if (_loading || _suppress) return;
                captured.Text = ((int)Math.Round(v)).ToString();
                _ddc.Set(code, (uint)Math.Round(v));
            };

            if (code == 0x12) { _vContrast = value; _sContrast = slider; }

            StackPanel sp = new StackPanel();
            sp.Children.Add(head);
            sp.Children.Add(slider);
            return Card(sp, 8);
        }

        Border BuildChannelCard(string title, byte r, byte g, byte b, List<SliderRow> into)
        {
            StackPanel sp = new StackPanel();
            TextBlock head = Text(title, 13, Theme.SubText, FontWeights.Medium);
            head.Margin = new Thickness(0, 0, 0, 8);
            sp.Children.Add(head);
            sp.Children.Add(CompactRow("红", r, into));
            sp.Children.Add(CompactRow("绿", g, into));
            sp.Children.Add(CompactRow("蓝", b, into));
            return Card(sp, 8);
        }

        FrameworkElement CompactRow(string label, byte code, List<SliderRow> into)
        {
            Grid g = new Grid();
            g.Height = 34;
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

            TextBlock l = Text(label, 12.5, Theme.SubText, FontWeights.Medium);
            l.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(l);

            SlimSlider s = new SlimSlider();
            s.Height = 24;
            s.Max = 100;
            s.Margin = new Thickness(2, 0, 8, 0);
            Grid.SetColumn(s, 1);
            g.Children.Add(s);

            TextBlock v = Text("—", 13.5, Theme.Text, FontWeights.SemiBold);
            v.HorizontalAlignment = HorizontalAlignment.Right;
            v.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(v, 2);
            g.Children.Add(v);

            TextBlock captured = v;
            s.ValueChanged += delegate(double val)
            {
                if (_loading || _suppress) return;
                captured.Text = ((int)Math.Round(val)).ToString();
                _ddc.Set(code, (uint)Math.Round(val));
            };

            SliderRow row = new SliderRow();
            row.Slider = s;
            row.Value = v;
            row.Code = code;
            into.Add(row);
            return g;
        }

        // ---- presets

        Border BuildPresetCard()
        {
            StackPanel sp = new StackPanel();

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock label = Text("预设", 13, Theme.SubText, FontWeights.Medium);
            label.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(label);

            TextBlock edit = Text("编辑文件…", 11.5, Theme.SubText, FontWeights.Normal);
            edit.VerticalAlignment = VerticalAlignment.Center;
            edit.Cursor = Cursors.Hand;
            edit.ToolTip = Paths.Presets;
            edit.MouseLeftButtonUp += delegate { PresetStore.OpenInEditor(); };
            Grid.SetColumn(edit, 1);
            head.Children.Add(edit);
            sp.Children.Add(head);

            _presetWrap = new WrapPanel();
            _presetWrap.Margin = new Thickness(0, 9, 0, 0);
            sp.Children.Add(_presetWrap);

            RenderPresets();
            return Card(sp, 0);
        }

        void RenderPresets()
        {
            if (_presetWrap == null) return;
            _presetWrap.Children.Clear();

            foreach (ProfilePreset preset in PresetStore.Items)
            {
                ProfilePreset captured = preset;
                Border chip = PresetChip(preset.Name, delegate { ApplyPreset(captured); });
                chip.ContextMenu = PresetMenu(captured);
                _presetWrap.Children.Add(chip);
            }

            Border add = PresetChip("＋ 新建", delegate { CreatePresetFromCurrent(); });
            add.ToolTip = "把当前对比度与色彩存成新预设";
            _presetWrap.Children.Add(add);
            RefreshPresetHighlight();
        }

        Border PresetChip(string name, Action click)
        {
            Border b = new Border();
            b.CornerRadius = new CornerRadius(9);
            b.Background = Theme.Chip;
            b.Padding = new Thickness(12, 6, 12, 6);
            b.Margin = new Thickness(0, 0, 6, 6);
            b.Cursor = Cursors.Hand;
            b.Child = Text(name, 12.5, Theme.Text, FontWeights.Medium);
            b.MouseLeftButtonUp += delegate { click(); };
            b.MouseEnter += delegate { if (!(b.Tag is bool && (bool)b.Tag)) b.Background = Theme.ChipHover; };
            b.MouseLeave += delegate { if (!(b.Tag is bool && (bool)b.Tag)) b.Background = Theme.Chip; };
            return b;
        }

        ContextMenu PresetMenu(ProfilePreset preset)
        {
            ContextMenu menu = new ContextMenu();
            menu.Background = Theme.Card;
            menu.Foreground = Theme.Text;
            menu.BorderBrush = Theme.CardStroke;

            MenuItem overwrite = new MenuItem();
            overwrite.Header = "用当前设置覆盖";
            overwrite.Click += delegate { CaptureInto(preset); };
            menu.Items.Add(overwrite);

            MenuItem rename = new MenuItem();
            rename.Header = "重命名…";
            rename.Click += delegate
            {
                string name = NameDialog.Ask(this, preset.Name);
                if (name == null) return;
                preset.Name = name;
                PresetStore.Save();
                RenderPresets();
            };
            menu.Items.Add(rename);

            MenuItem delete = new MenuItem();
            delete.Header = "删除";
            delete.Click += delegate
            {
                PresetStore.Items.Remove(preset);
                PresetStore.Save();
                RenderPresets();
            };
            menu.Items.Add(delete);
            return menu;
        }

        void CreatePresetFromCurrent()
        {
            if (_state == null || !_state.Ok) return;
            ProfilePreset p = new ProfilePreset();
            p.Name = "预设 " + (PresetStore.Items.Count + 1);
            p.Contrast = (int)_state.ContrastCur;
            p.GainR = (int)_state.GainR; p.GainG = (int)_state.GainG; p.GainB = (int)_state.GainB;
            p.OffR = (int)_state.OffR; p.OffG = (int)_state.OffG; p.OffB = (int)_state.OffB;

            PresetStore.Items.Add(p);
            PresetStore.Save();

            string name = NameDialog.Ask(this, p.Name);
            if (name != null) { p.Name = name; PresetStore.Save(); }
            RenderPresets();
        }

        void CaptureInto(ProfilePreset preset)
        {
            if (_state == null || !_state.Ok) return;
            preset.Contrast = (int)_state.ContrastCur;
            preset.GainR = (int)_state.GainR; preset.GainG = (int)_state.GainG; preset.GainB = (int)_state.GainB;
            preset.OffR = (int)_state.OffR; preset.OffG = (int)_state.OffG; preset.OffB = (int)_state.OffB;
            PresetStore.Save();
            RefreshPresetHighlight();
            _ddc.RequestState();
        }

        void ApplyPreset(ProfilePreset preset)
        {
            if (_state == null || !_state.Ok) return;

            _suppress = true;
            if (_state.ContrastOk)
            {
                _sContrast.Value = Math.Min(preset.Contrast, _sContrast.Max);
                _vContrast.Text = ((int)_sContrast.Value).ToString();
            }
            ApplyRowValue(_gainRows, 0, preset.GainR);
            ApplyRowValue(_gainRows, 1, preset.GainG);
            ApplyRowValue(_gainRows, 2, preset.GainB);
            ApplyRowValue(_offsetRows, 0, preset.OffR);
            ApplyRowValue(_offsetRows, 1, preset.OffG);
            ApplyRowValue(_offsetRows, 2, preset.OffB);
            _suppress = false;

            _ddc.Set(0x12, (uint)preset.Contrast);
            _ddc.Set(0x16, (uint)preset.GainR);
            _ddc.Set(0x18, (uint)preset.GainG);
            _ddc.Set(0x1A, (uint)preset.GainB);
            _ddc.Set(0x6C, (uint)preset.OffR);
            _ddc.Set(0x6E, (uint)preset.OffG);
            _ddc.Set(0x70, (uint)preset.OffB);

            RefreshPresetHighlight();
            _ddc.RequestState();
        }

        void ApplyRowValue(List<SliderRow> rows, int index, int value)
        {
            if (index >= rows.Count) return;
            SliderRow row = rows[index];
            if (!row.Enabled) return;
            double v = Math.Max(row.Slider.Min, Math.Min(row.Slider.Max, value));
            row.Slider.Value = v;
            row.Value.Text = ((int)Math.Round(v)).ToString();
        }

        bool Matches(ProfilePreset preset)
        {
            if (_state == null || !_state.Ok) return false;
            if (!_state.ContrastOk || !_state.GainOk || !_state.OffsetOk) return false;
            return Math.Abs((int)_state.ContrastCur - preset.Contrast) <= 2
                && Math.Abs((int)_state.GainR - preset.GainR) <= 2
                && Math.Abs((int)_state.GainG - preset.GainG) <= 2
                && Math.Abs((int)_state.GainB - preset.GainB) <= 2
                && Math.Abs((int)_state.OffR - preset.OffR) <= 2
                && Math.Abs((int)_state.OffG - preset.OffG) <= 2
                && Math.Abs((int)_state.OffB - preset.OffB) <= 2;
        }

        void RefreshPresetHighlight()
        {
            if (_presetWrap == null) return;
            int i = 0;
            foreach (ProfilePreset preset in PresetStore.Items)
            {
                if (i >= _presetWrap.Children.Count) break;
                Border chip = _presetWrap.Children[i] as Border;
                i++;
                if (chip == null) continue;
                bool active = Matches(preset);
                chip.Tag = active;
                chip.Background = active ? Theme.Accent : Theme.Chip;
                TextBlock label = chip.Child as TextBlock;
                if (label != null) label.Foreground = active ? Brushes.White : Theme.Text;
            }
        }

        // ---- footer

        FrameworkElement BuildFooter()
        {
            Grid g = new Grid();
            g.Margin = new Thickness(2, 4, 2, 0);
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _status = Text("DDC/CI", 10.5, Theme.SubText, FontWeights.Normal);
            _status.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(_status);

            TextBlock hint = Text("Esc 关闭", 10.5, Theme.SubText, FontWeights.Normal);
            hint.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(hint, 1);
            g.Children.Add(hint);
            return g;
        }

        // ---- icons

        enum IconKind { Monitor, Contrast, Refresh }

        static FrameworkElement Glyph(IconKind kind, double size, Brush brush)
        {
            if (kind == IconKind.Contrast) return ContrastGlyph(size, brush);

            string data = kind == IconKind.Monitor
                ? "M2.5 4.5 H17.5 V13 H2.5 Z M7.5 16.2 H12.5 M10 13 V16.2"
                : "M16.4 7.6 A6.6 6.6 0 1 0 16.9 12 M16.4 3.4 V7.9 H12.1";

            Path path = new Path();
            path.Data = Geometry.Parse(data);
            path.Stroke = brush;
            path.StrokeThickness = 1.5;
            path.StrokeStartLineCap = PenLineCap.Round;
            path.StrokeEndLineCap = PenLineCap.Round;
            path.StrokeLineJoin = PenLineJoin.Round;

            Viewbox vb = new Viewbox();
            vb.Width = size;
            vb.Height = size;
            vb.Child = path;
            vb.Stretch = Stretch.Uniform;
            return vb;
        }

        static FrameworkElement ContrastGlyph(double size, Brush brush)
        {
            Canvas canvas = new Canvas();
            canvas.Width = 20;
            canvas.Height = 20;

            Ellipse ring = new Ellipse();
            ring.Width = 16;
            ring.Height = 16;
            ring.Stroke = brush;
            ring.StrokeThickness = 1.6;
            Canvas.SetLeft(ring, 2);
            Canvas.SetTop(ring, 2);
            canvas.Children.Add(ring);

            Path half = new Path();
            half.Data = Geometry.Parse("M10 2 A8 8 0 0 1 10 18 Z");
            half.Fill = brush;
            canvas.Children.Add(half);

            Viewbox vb = new Viewbox();
            vb.Width = size;
            vb.Height = size;
            vb.Child = canvas;
            vb.Stretch = Stretch.Uniform;
            return vb;
        }

        // ---- state

        void OnState(MonitorState st)
        {
            Dispatcher.BeginInvoke(new Action(delegate { ApplyState(st); }));
        }

        void OnFault(string msg)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_status != null) { _status.Text = msg; _status.Foreground = Theme.ChannelRed; }
            }));
        }

        void ApplyState(MonitorState st)
        {
            _state = st;
            if (!st.Ok)
            {
                _model.Text = "未连接";
                _sub.Text = st.Error == null ? "DDC/CI 不可用" : st.Error;
                return;
            }

            _loading = true;
            _model.Text = string.IsNullOrEmpty(st.Model) ? "显示器" : st.Model;
            _sub.Text = st.Mccs.Length > 0 ? "MCCS " + st.Mccs + " · DDC/CI" : "DDC/CI";

            if (st.BrightnessOk)
            {
                _sBright.Min = st.BrightMin;
                _sBright.Max = st.BrightMax;
                _sBright.Value = st.BrightCur;
                _vBright.Text = st.BrightCur.ToString();
            }
            _cardBright.Opacity = st.BrightnessOk ? 1.0 : 0.5;

            if (st.ContrastOk)
            {
                _sContrast.Min = 0;
                _sContrast.Max = Math.Max(1, st.ContrastMax);
                _sContrast.Value = st.ContrastCur;
                _vContrast.Text = st.ContrastCur.ToString();
            }

            ApplyRows(_gainRows, st.GainOk, st.GainR, st.GainG, st.GainB, st.GainMax);
            ApplyRows(_offsetRows, st.OffsetOk, st.OffR, st.OffG, st.OffB, st.OffMax);

            if (_status != null)
            {
                _status.Text = st.Codes.Count > 0 ? "DDC/CI · 可用 " + st.Codes.Count + " 项" : "DDC/CI 已连接";
                _status.Foreground = Theme.SubText;
            }

            _loading = false;
            RefreshPresetHighlight();
        }

        void ApplyRows(List<SliderRow> rows, bool ok, uint r, uint g, uint b, uint max)
        {
            if (rows.Count < 3) return;
            ApplyRow(rows[0], ok, r, max);
            ApplyRow(rows[1], ok, g, max);
            ApplyRow(rows[2], ok, b, max);
        }

        void ApplyRow(SliderRow row, bool ok, uint cur, uint max)
        {
            row.Enabled = ok;
            row.Slider.IsEnabled = ok;
            row.Slider.Opacity = ok ? 1.0 : 0.3;
            row.Value.Opacity = ok ? 1.0 : 0.4;
            if (!ok) { row.Value.Text = "—"; return; }
            row.Slider.Max = Math.Max(1, max);
            row.Slider.Value = cur;
            row.Value.Text = cur.ToString();
        }

        // ---- show / hide

        public void ShowPanel()
        {
            Show();
            Reposition();
            Activate();
            Topmost = true;
        }

        public void ApplyForRender(MonitorState st)
        {
            ApplyState(st);
        }

        public void SelectTab(int index)
        {
            SetTab(index);
        }

        public void ReloadPresets()
        {
            PresetStore.Load();
            RenderPresets();
        }

        void OnDeactivated(object sender, EventArgs e)
        {
            if (_preview) return;
            LastAutoHidden = DateTime.Now;
            Dispatcher.BeginInvoke(new Action(delegate { Hide(); }), DispatcherPriority.Background);
        }

        public DateTime LastAutoHidden = DateTime.MinValue;

        void Reposition()
        {
            Rect work = SystemParameters.WorkArea;
            double h = ActualHeight > 0 ? ActualHeight : 420;
            Left = work.Right - Width - 12;
            Top = work.Bottom - h - 8;
        }
    }

    // ---------------------------------------------------------- name dialog

    internal sealed class NameDialog : Window
    {
        string _result;

        NameDialog(string initial)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SizeToContent = SizeToContent.Height;
            Width = 300;
            Topmost = true;

            Border frame = new Border();
            frame.CornerRadius = new CornerRadius(14);
            frame.Background = Theme.Panel;
            frame.BorderBrush = Theme.PanelStroke;
            frame.BorderThickness = new Thickness(1);
            frame.Padding = new Thickness(16);
            DropShadowEffect shadow = new DropShadowEffect();
            shadow.Color = Colors.Black;
            shadow.BlurRadius = 22;
            shadow.Opacity = 0.5;
            frame.Effect = shadow;

            StackPanel sp = new StackPanel();
            TextBlock label = new TextBlock();
            label.Text = "预设名称";
            label.FontSize = 12;
            label.Foreground = Theme.SubText;
            label.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            sp.Children.Add(label);

            TextBox box = new TextBox();
            box.Text = initial;
            box.FontSize = 14;
            box.Margin = new Thickness(0, 8, 0, 12);
            box.Padding = new Thickness(8, 6, 8, 6);
            box.Background = Theme.Chip;
            box.Foreground = Theme.Text;
            box.BorderBrush = Theme.CardStroke;
            box.CaretBrush = Theme.Text;
            box.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            sp.Children.Add(box);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;

            Button cancel = DialogButton("取消");
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.Click += delegate { _result = null; Close(); };
            Button ok = DialogButton("确定");
            ok.Click += delegate
            {
                string text = (box.Text ?? "").Trim();
                _result = text.Length == 0 ? null : text;
                Close();
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            sp.Children.Add(buttons);

            frame.Child = sp;
            Content = frame;

            Loaded += delegate { box.Focus(); box.SelectAll(); };
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { string t = (box.Text ?? "").Trim(); _result = t.Length == 0 ? null : t; Close(); }
                if (e.Key == Key.Escape) { _result = null; Close(); }
            };
        }

        static Button DialogButton(string text)
        {
            Button b = new Button();
            b.Content = text;
            b.FontSize = 12.5;
            b.Padding = new Thickness(14, 5, 14, 5);
            b.Background = Theme.Chip;
            b.Foreground = Theme.Text;
            b.BorderThickness = new Thickness(0);
            b.Cursor = Cursors.Hand;
            b.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            return b;
        }

        public static string Ask(Window owner, string initial)
        {
            NameDialog d = new NameDialog(initial);
            if (owner != null && owner.IsVisible) d.Owner = owner;
            d.ShowDialog();
            return d._result;
        }
    }

    // ------------------------------------------------------------------- osd

    public sealed class OsdWindow : Window
    {
        readonly TextBlock _value;
        readonly Border _fill;
        readonly DispatcherTimer _timer;

        public OsdWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Focusable = false;
            IsHitTestVisible = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            Width = 178;

            Border frame = new Border();
            frame.CornerRadius = new CornerRadius(14);
            frame.Background = Theme.Panel;
            frame.BorderBrush = Theme.PanelStroke;
            frame.BorderThickness = new Thickness(1);
            frame.Padding = new Thickness(14, 10, 14, 12);
            DropShadowEffect shadow = new DropShadowEffect();
            shadow.Color = Colors.Black;
            shadow.BlurRadius = 20;
            shadow.ShadowDepth = 3;
            shadow.Opacity = 0.45;
            frame.Effect = shadow;

            StackPanel sp = new StackPanel();
            sp.Width = 150;

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock label = new TextBlock();
            label.Text = "亮度";
            label.FontSize = 12;
            label.Foreground = Theme.SubText;
            label.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            label.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(label);

            _value = new TextBlock();
            _value.Text = "—";
            _value.FontSize = 15;
            _value.FontWeight = FontWeights.SemiBold;
            _value.Foreground = Theme.Text;
            _value.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Grid.SetColumn(_value, 1);
            head.Children.Add(_value);
            sp.Children.Add(head);

            Grid barHost = new Grid();
            barHost.Margin = new Thickness(0, 8, 0, 0);
            Border track = new Border();
            track.Height = 3;
            track.CornerRadius = new CornerRadius(1.5);
            track.Background = Theme.Track;
            barHost.Children.Add(track);

            _fill = new Border();
            _fill.Height = 3;
            _fill.CornerRadius = new CornerRadius(1.5);
            _fill.Background = Theme.Accent;
            _fill.HorizontalAlignment = HorizontalAlignment.Left;
            _fill.Width = 0;
            barHost.Children.Add(_fill);
            sp.Children.Add(barHost);

            frame.Child = sp;
            Content = frame;

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(1100);
            _timer.Tick += delegate { _timer.Stop(); Hide(); };
        }

        public void Show(int percent, Rect anchor)
        {
            _value.Text = percent + "%";
            _fill.Width = Math.Max(2, 150.0 * Math.Max(0, Math.Min(100, percent)) / 100.0);

            if (!IsVisible) Show();
            UpdateLayout();

            double w = Width;
            double h = ActualHeight > 0 ? ActualHeight : 74;
            Left = Math.Max(8, anchor.Right - w);
            Top = Math.Max(8, anchor.Top - h - 8);
            Topmost = true;

            _timer.Stop();
            _timer.Start();
        }
    }

    // ------------------------------------------------------------- tray host

    internal static class TrayLocator
    {
        static readonly object Gate = new object();
        static Rect _rect = Rect.Empty;
        static Timer _timer;

        public static void Start()
        {
            if (_timer != null) return;
            _timer = new Timer(delegate { Refresh(); }, null, 0, 2000);
        }

        public static Rect Current
        {
            get { lock (Gate) return _rect; }
        }

        public static bool Hit(int x, int y)
        {
            Rect r = Current;
            if (r.IsEmpty) return false;
            return x >= r.Left && x <= r.Right && y >= r.Top && y <= r.Bottom;
        }

        public static void Refresh()
        {
            try
            {
                AutomationElement root = AutomationElement.RootElement;
                if (root == null) return;

                AutomationElement host = root.FindFirst(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
                if (host == null) return;

                AutomationElement el = FindIcon(host);
                if (el == null)
                {
                    AutomationElement overflow = root.FindFirst(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ClassNameProperty, "NotifyIconOverflowWindow"));
                    if (overflow != null) el = FindIcon(overflow);
                }

                if (el == null) return;
                Rect r = el.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width < 4) return;
                lock (Gate) _rect = r;
            }
            catch { }
        }

        static AutomationElement FindIcon(AutomationElement host)
        {
            System.Windows.Automation.Condition byName = new PropertyCondition(AutomationElement.NameProperty, "显示器控制");
            return host.FindFirst(TreeScope.Descendants, byName);
        }
    }

    public sealed class TrayHost : IDisposable
    {
        readonly DdcWorker _ddc = new DdcWorker();
        readonly PanelWindow _panel;
        readonly OsdWindow _osd;
        Native.LowLevelMouseProc _hookProc;
        IntPtr _hook;

        public TrayHost(bool preview)
        {
            _panel = new PanelWindow(_ddc, preview);
            _osd = new OsdWindow();
            _ddc.Start();
            _ddc.RequestState();
        }

        public PanelWindow Panel { get { return _panel; } }

        public WF.NotifyIcon CreateIcon()
        {
            WF.NotifyIcon icon = new WF.NotifyIcon();
            icon.Icon = TrayIcon.Create();
            icon.Text = "显示器控制";
            icon.Visible = true;
            icon.MouseClick += delegate(object s, WF.MouseEventArgs e)
            {
                if (e.Button == WF.MouseButtons.Left) Toggle();
            };
            icon.DoubleClick += delegate { Toggle(); };

            WF.ContextMenuStrip menu = new WF.ContextMenuStrip();
            WF.ToolStripMenuItem open = new WF.ToolStripMenuItem("打开控制面板");
            open.Click += delegate { Toggle(); };
            WF.ToolStripMenuItem presets = new WF.ToolStripMenuItem("编辑预设文件…");
            presets.Click += delegate { PresetStore.OpenInEditor(); };
            WF.ToolStripMenuItem auto = new WF.ToolStripMenuItem("开机自动启动");
            auto.Checked = Autostart.IsEnabled();
            auto.Click += delegate
            {
                bool want = !Autostart.IsEnabled();
                Autostart.Set(want);
                auto.Checked = want;
            };
            WF.ToolStripMenuItem quit = new WF.ToolStripMenuItem("退出");
            quit.Click += delegate { Shutdown(); };
            menu.Items.Add(open);
            menu.Items.Add(presets);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(auto);
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add(quit);
            icon.ContextMenuStrip = menu;
            return icon;
        }

        public void InstallWheelHook()
        {
            TrayLocator.Start();
            _hookProc = HookCallback;
            _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _hookProc, Native.GetModuleHandle(null), 0);
            DebugLog.Write("wheel hook=" + _hook);
        }

        IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)Native.WM_MOUSEWHEEL)
            {
                Native.MSLLHOOKSTRUCT info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                if (TrayLocator.Hit(info.pt.X, info.pt.Y))
                {
                    int delta = (short)((info.mouseData >> 16) & 0xFFFF);
                    OnTrayWheel(delta);
                    return (IntPtr)1;
                }
            }
            return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        void OnTrayWheel(int delta)
        {
            int current = _ddc.Brightness;
            if (current < 0)
            {
                _ddc.RequestState();
                return;
            }

            int next = Math.Max(0, Math.Min(100, current + (delta > 0 ? 3 : -3)));
            if (next == current) return;

            _ddc.Set(0x10, (uint)next);
            _ddc.NoteBrightness(next);

            Rect anchor = TrayLocator.Current;
            if (anchor.IsEmpty)
            {
                Rect work = SystemParameters.WorkArea;
                anchor = new Rect(work.Right - 120, work.Bottom, 120, 0);
            }
            _osd.Show(next, anchor);
        }

        public void Toggle()
        {
            if (_panel.IsVisible) { _panel.Hide(); return; }
            if ((DateTime.Now - _panel.LastAutoHidden).TotalMilliseconds < 350) return;
            _panel.ReloadPresets();
            _ddc.RequestState();
            _panel.ShowPanel();
        }

        public void Shutdown()
        {
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
            _ddc.Dispose();
            Application.Current.Shutdown();
        }

        public void Dispose() { _ddc.Dispose(); }
    }

    internal static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "MonitorBar";

        public static bool IsEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(Name) != null;
        }

        public static void Set(bool enabled)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (k == null) return;
                if (enabled) k.SetValue(Name, "\"" + Assembly.GetExecutingAssembly().Location + "\"");
                else k.DeleteValue(Name, false);
            }
        }
    }

    internal static class TrayIcon
    {
        public static System.Drawing.Icon Create()
        {
            System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(32, 32);
            System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);

            System.Drawing.Pen pen = new System.Drawing.Pen(System.Drawing.Color.White, 2.4f);
            pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
            pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;

            System.Drawing.Drawing2D.GraphicsPath screen = new System.Drawing.Drawing2D.GraphicsPath();
            float x = 3.5f, y = 5.5f, w = 25f, h = 16f, r = 3f;
            screen.AddArc(x, y, r * 2, r * 2, 180, 90);
            screen.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            screen.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            screen.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
            screen.CloseFigure();
            g.DrawPath(pen, screen);

            g.DrawLine(pen, 16f, y + h, 16f, 26.5f);
            g.DrawLine(pen, 11f, 26.5f, 21f, 26.5f);

            pen.Dispose();
            screen.Dispose();

            IntPtr hIcon = bmp.GetHicon();
            return System.Drawing.Icon.FromHandle(hIcon);
        }
    }

    // ------------------------------------------------------------------ main

    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try { System.IO.File.WriteAllText(CrashLogPath(), Convert.ToString(e.ExceptionObject)); }
                catch { }
            };

            try { Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
            catch { }

            bool preview = false;
            string renderPath = null;
            string setSpec = null;
            int renderTab = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--preview", StringComparison.OrdinalIgnoreCase)) preview = true;
                if (string.Equals(args[i], "--render", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) renderPath = args[++i];
                if (string.Equals(args[i], "--set", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) setSpec = args[++i];
                if (string.Equals(args[i], "--tab", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    renderTab = string.Equals(args[++i], "color", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            }

            if (renderPath != null) { RenderToFile(renderPath, renderTab); return; }
            if (setSpec != null) { ApplySet(setSpec); return; }

            bool created;
            Mutex mutex = new Mutex(true, "Local\\MonitorBar.Singleton", out created);
            if (!created && !preview) return;

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                try { System.IO.File.WriteAllText(CrashLogPath(), Convert.ToString(e.Exception)); }
                catch { }
                e.Handled = true;
            };

            TrayHost host = new TrayHost(preview);
            WF.NotifyIcon icon = host.CreateIcon();
            host.InstallWheelHook();

            if (preview) host.Panel.ShowPanel();

            app.Run();

            if (icon != null) { icon.Visible = false; icon.Dispose(); }
            GC.KeepAlive(mutex);
        }

        static string CrashLogPath()
        {
            return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MonitorBar-crash.txt");
        }

        static void ApplySet(string spec)
        {
            int eq = spec.IndexOf('=');
            if (eq <= 0) return;

            byte code;
            uint value;
            if (!byte.TryParse(spec.Substring(0, eq).Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out code)) return;
            if (!uint.TryParse(spec.Substring(eq + 1), out value)) return;

            DdcWorker ddc = new DdcWorker();
            ddc.Start();
            MonitorState st = ddc.ReadStateSync();
            if (!st.Ok)
            {
                DebugLog.Write("set: no monitor (" + st.Error + ")");
                return;
            }
            ddc.Set(code, value);
            Thread.Sleep(400);
            MonitorState after = ddc.ReadStateSync();
            DebugLog.Write("set 0x" + code.ToString("X2") + "=" + value + " -> brightness=" + after.BrightCur);
            ddc.Dispose();
        }

        static void RenderToFile(string path, int tab)
        {
            DdcWorker ddc = new DdcWorker();
            ddc.Start();
            MonitorState st = ddc.ReadStateSync();

            PanelWindow w = new PanelWindow(ddc, true);
            w.ApplyForRender(st);
            w.SelectTab(tab);

            FrameworkElement root = w.Content as FrameworkElement;
            if (root == null) return;

            double width = PanelWindow.PanelWidth;
            root.Measure(new Size(width, double.PositiveInfinity));
            root.Arrange(new Rect(0, 0, width, root.DesiredSize.Height));
            root.UpdateLayout();

            int pw = (int)Math.Ceiling(width);
            int ph = (int)Math.Ceiling(root.DesiredSize.Height);

            RenderTargetBitmap rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);

            DrawingVisual dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1E)), null, new Rect(0, 0, pw, ph));
                dc.DrawImage(rtb, new Rect(0, 0, pw, ph));
            }
            RenderTargetBitmap flat = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
            flat.Render(dv);

            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(flat));
            using (System.IO.FileStream fs = new System.IO.FileStream(path, System.IO.FileMode.Create))
                enc.Save(fs);

            ddc.Dispose();
        }
    }
}
