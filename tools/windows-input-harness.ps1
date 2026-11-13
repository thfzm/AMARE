param(
    [Parameter(Mandatory = $true)][string]$Output,
    [Parameter(Mandatory = $true)][string]$ScreenshotDir
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$env:AMARE_HARNESS_OUTPUT = $Output
$env:AMARE_HARNESS_SCREENSHOT_DIR = $ScreenshotDir
New-Item -ItemType Directory -Force -Path (Split-Path $Output -Parent), $ScreenshotDir | Out-Null

$code = @'
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

public static class HarnessNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO {
        public int cbSize; public int flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT {
        public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref Point point);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    public static uint SendKey(ushort vk, bool down) {
        INPUT input = new INPUT();
        input.type = 1;
        input.U.ki.wVk = vk;
        input.U.ki.dwFlags = down ? 0u : 2u;
        return SendInput(1, new[]{input}, Marshal.SizeOf(typeof(INPUT)));
    }
    public static void SendCtrl(ushort vk) {
        SendKey(0x11, true); SendKey(vk, true); SendKey(vk, false); SendKey(0x11, false);
    }
    public static IntPtr FocusWindow() {
        GUITHREADINFO info = new GUITHREADINFO();
        info.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
        if (GetGUIThreadInfo(GetCurrentThreadId(), ref info)) return info.hwndFocus;
        return IntPtr.Zero;
    }
}

public class HarnessForm : Form {
    private readonly string outputPath;
    private readonly string screenshotDir;
    private readonly Label status;
    private readonly Button button;
    private readonly Timer timer;
    private int phase;
    private int keyDownCount;
    private int ctrlTCount;
    private int ctrlNCount;
    private int mouseDownCount;
    private int clickCount;
    private int postKeyCount;
    private string sendInputResult = "";
    private string postMessageResult = "";
    private long startForeground;
    private long startFocus;

    public HarnessForm(string output, string screenshotDir) {
        this.outputPath = output;
        this.screenshotDir = screenshotDir;
        Text = "AMARE Input Harness";
        Width = 640; Height = 420;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        status = new Label();
        status.Dock = DockStyle.Top;
        status.Height = 180;
        status.Font = new Font("Consolas", 11);
        Controls.Add(status);

        button = new Button();
        button.Name = "harness_click_button";
        button.Text = "Click Me";
        button.Width = 180; button.Height = 56;
        button.Left = 220; button.Top = 220;
        button.Click += (s, e) => { clickCount++; UpdateStatus(); };
        Controls.Add(button);

        KeyDown += (s, e) => {
            keyDownCount++;
            if (e.Control && e.KeyCode == Keys.T) ctrlTCount++;
            if (e.Control && e.KeyCode == Keys.N) ctrlNCount++;
            UpdateStatus();
        };
        MouseDown += (s, e) => { mouseDownCount++; UpdateStatus(); };

        timer = new Timer();
        timer.Interval = 1100;
        timer.Tick += OnTick;
        Shown += (s, e) => {
            startForeground = HarnessNative.GetForegroundWindow().ToInt64();
            startFocus = HarnessNative.FocusWindow().ToInt64();
            HarnessNative.BringWindowToTop(Handle);
            HarnessNative.SetForegroundWindow(Handle);
            Activate();
            button.Focus();
            timer.Start();
            UpdateStatus();
        };
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == 0x0100 || m.Msg == 0x0101) postKeyCount++;
        base.WndProc(ref m);
    }

    private void UpdateStatus() {
        status.Text = string.Format(
            "foreground={0}\r\nfocus={1}\r\nkeyDown={2} ctrlT={3} ctrlN={4}\r\nmouseDown={5} click={6} postKey={7}\r\nsendInput={8} postMessage={9}\r\nphase={10}",
            HarnessNative.GetForegroundWindow().ToInt64(), HarnessNative.FocusWindow().ToInt64(),
            keyDownCount, ctrlTCount, ctrlNCount, mouseDownCount, clickCount, postKeyCount,
            sendInputResult, postMessageResult, phase);
    }

    private void OnTick(object sender, EventArgs e) {
        phase++;
        if (phase == 1) {
            HarnessNative.BringWindowToTop(Handle);
            HarnessNative.SetForegroundWindow(Handle);
            Activate();
            button.Focus();
            uint sent = HarnessNative.SendKey(0x11, true) + HarnessNative.SendKey(0x54, true) +
                        HarnessNative.SendKey(0x54, false) + HarnessNative.SendKey(0x11, false);
            sendInputResult = "ctrl+t sent=" + sent;
        } else if (phase == 2) {
            uint sent = HarnessNative.SendKey(0x11, true) + HarnessNative.SendKey(0x4E, true) +
                        HarnessNative.SendKey(0x4E, false) + HarnessNative.SendKey(0x11, false);
            sendInputResult = "ctrl+n sent=" + sent;
        } else if (phase == 3) {
            Point p = button.PointToScreen(new Point(button.Width / 2, button.Height / 2));
            HarnessNative.SetCursorPos(p.X, p.Y);
            HarnessNative.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            HarnessNative.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        } else if (phase == 4) {
            bool down = HarnessNative.PostMessage(Handle, 0x0100, (IntPtr)0x54, (IntPtr)0);
            bool up = HarnessNative.PostMessage(Handle, 0x0101, (IntPtr)0x54, (IntPtr)0);
            postMessageResult = "wm_keydown=" + down + " wm_keyup=" + up;
        } else if (phase == 5) {
            timer.Stop();
            Save();
            Close();
        }
        UpdateStatus();
    }

    private void Save() {
        string png = Path.Combine(screenshotDir, "input-harness.png");
        try {
            Rectangle r = Bounds;
            using (Bitmap bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height)))
            using (Graphics g = Graphics.FromImage(bmp)) {
                g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
                bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            }
        } catch { png = ""; }
        string json = "{";
        json += "\"generated_at\":\"" + DateTime.UtcNow.ToString("o") + "\",";
        json += "\"hwnd\":" + Handle.ToInt64() + ",";
        json += "\"start_foreground\":" + startForeground + ",";
        json += "\"end_foreground\":" + HarnessNative.GetForegroundWindow().ToInt64() + ",";
        json += "\"start_focus\":" + startFocus + ",";
        json += "\"end_focus\":" + HarnessNative.FocusWindow().ToInt64() + ",";
        json += "\"key_down\":" + keyDownCount + ",";
        json += "\"ctrl_t\":" + ctrlTCount + ",";
        json += "\"ctrl_n\":" + ctrlNCount + ",";
        json += "\"mouse_down\":" + mouseDownCount + ",";
        json += "\"button_click\":" + clickCount + ",";
        json += "\"posted_key_messages\":" + postKeyCount + ",";
        json += "\"send_input\":\"" + sendInputResult.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",";
        json += "\"post_message\":\"" + postMessageResult.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",";
        json += "\"screenshot\":\"" + png.Replace("\\", "\\\\") + "\"";
        json += "}";
        File.WriteAllText(outputPath, json, Encoding.UTF8);
    }
}
'@

Add-Type -TypeDefinition $code -ReferencedAssemblies @(
    [System.Windows.Forms.Form].Assembly.Location,
    [System.Drawing.Bitmap].Assembly.Location,
    [System.Drawing.Graphics].Assembly.Location,
    [System.ComponentModel.Component].Assembly.Location,
    [System.Drawing.Point].Assembly.Location
)

$form = New-Object HarnessForm($Output, $ScreenshotDir)
[System.Windows.Forms.Application]::Run($form)
if (-not (Test-Path $Output)) { throw "input harness output missing" }
Write-Host "input harness output: $Output"
