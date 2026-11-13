using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VideoWallpaper
{
    // ======================= Win32 API =======================
    internal static class Native
    {
        public const uint WM_SPAWN_WORKER = 0x052C;
        public const int SPAWN_WPARAM = 0x000D; // correct value to spawn the WorkerW split (not zero)
        public const int SMTO_ABORTIFHUNG = 0x0002;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
        public const uint SPI_SETDESKWALLPAPER = 0x0014;
        public const uint SPIF_UPDATEINIFILE = 0x01;
        public const uint SPIF_SENDCHANGE = 0x02;

        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_LAYERED = 0x00080000L;
        public const long WS_EX_TRANSPARENT = 0x00000020L;
        public const long WS_EX_NOACTIVATE = 0x08000000L;

        public const uint LWA_ALPHA = 0x00000002;

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint VK_W = 0x57;
        public const int HOTKEY_ID = 0xB001;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }

    // ======================= Video player host (Windows Media Player) =======================
    internal sealed class WmpControl : AxHost
    {
        // CLSID of Windows Media Player (built into Windows)
        public WmpControl() : base("6BF52A52-394A-11D3-B153-00C04F79FAA6")
        {
            Dock = DockStyle.Fill;
        }

        public dynamic Ocx
        {
            get { return GetOcx(); }
        }
    }

    // Frameless video window, shown behind desktop icons (click-through)
    internal sealed class WallpaperWindow : Form
    {
        public WmpControl Player { get; private set; }

        public WallpaperWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            // Plain off-screen buffering — no WS_EX_COMPOSITED (DWM composition
            // causes black flashes with ActiveX, especially on integrated GPUs)
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            Rectangle vs = SystemInformation.VirtualScreen;
            Location = vs.Location;
            Size = vs.Size;

            Player = new WmpControl();
            Controls.Add(Player);
        }
    }

    // ======================= Main settings window =======================
    public sealed class MainForm : Form
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ConfigKey = @"Software\VideoWallpaper";
        private const string AppName = "VideoWallpaper";

        private readonly WallpaperWindow wallpaperWindow = new WallpaperWindow();
        private NotifyIcon trayIcon;

        private Label pathLabel;
        private Button btnBrowse, btnApply, btnRemove;
        private CheckBox chkStartup, chkSound;

        private string videoPath;
        private bool wallpaperApplied;
        private bool suppressStartupEvent;
        private bool balloonShown;
        private bool hotkeyRegistered;
        private System.Windows.Forms.Timer stateTimer;
        private System.Windows.Forms.Timer watchdogTimer;
        private int spawnRetryTicks;
        private long tickCount;
        private IntPtr hostCache = IntPtr.Zero;
        private bool pendingShow;          // window is hidden until the first real frame arrives (no black flash)
        private int pendingShowTicks;      // seconds to wait for the first frame before treating the file as unplayable
        private double cachedDuration = -1.0; // video length in seconds — read once from the media instead of polling
        private bool lastApplySilent;      // was the last apply silent (at startup)? used to decide about showing an error box
        private bool pausedForSession;     // video paused by session lock / sleep
        private dynamic ocxCache;          // cached OCX reference — avoids repeated GetOcx() calls every watchdog tick

        public MainForm(bool startMinimized)
        {
            Text = "Video Wallpaper";
            ClientSize = new Size(460, 126);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            BuildUi();

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Settings", null, (s, e) => ShowSettings());
            menu.Items.Add("Remove Wallpaper", null, (s, e) => RemoveWallpaper());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => QuitApp());
            trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "Video Wallpaper",
                ContextMenuStrip = menu,
                Visible = true
            };
            trayIcon.DoubleClick += (s, e) => ShowSettings();

            if (startMinimized)
                ShowInTaskbar = false;

            Load += (s, e) => OnAppLoad(startMinimized);
            FormClosing += OnFormClosing;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!hotkeyRegistered)
            {
                // Global panic hotkey: Ctrl+Alt+W — removes the wallpaper and restores the normal desktop instantly
                hotkeyRegistered = Native.RegisterHotKey(Handle, Native.HOTKEY_ID, Native.MOD_CONTROL | Native.MOD_ALT, Native.VK_W);
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (hotkeyRegistered)
            {
                Native.UnregisterHotKey(Handle, Native.HOTKEY_ID);
                hotkeyRegistered = false;
            }
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == Native.HOTKEY_ID)
            {
                RemoveWallpaper();
                if (trayIcon != null)
                    trayIcon.ShowBalloonTip(2500, "Video Wallpaper", "Wallpaper removed — desktop restored.", ToolTipIcon.Info);
                return;
            }
            base.WndProc(ref m);
        }

        private void BuildUi()
        {
            pathLabel = new Label
            {
                Text = "No video selected yet …",
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
                ForeColor = Color.Gray,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Padding = new Padding(6, 0, 6, 0),
                Location = new Point(12, 12),
                Size = new Size(328, 30)
            };
            btnBrowse = new Button
            {
                Text = "Browse…",
                Location = new Point(348, 12),
                Size = new Size(100, 30)
            };
            btnBrowse.Click += (s, e) => BrowseVideo();

            btnApply = new Button
            {
                Text = "Apply",
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                Location = new Point(12, 52),
                Size = new Size(214, 38)
            };
            btnApply.Click += (s, e) => { ApplyWallpaper(videoPath, silent: false); };

            btnRemove = new Button
            {
                Text = "Remove Wallpaper",
                Location = new Point(234, 52),
                Size = new Size(214, 38)
            };
            btnRemove.Click += (s, e) => RemoveWallpaper();

            chkStartup = new CheckBox
            {
                Text = "Start with Windows",
                Location = new Point(12, 98),
                Size = new Size(214, 24)
            };
            chkStartup.CheckedChanged += (s, e) =>
            {
                if (suppressStartupEvent) return;
                try { SetStartup(chkStartup.Checked); }
                catch (Exception ex) { MessageBox.Show(this, "Could not update startup settings:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };

            chkSound = new CheckBox
            {
                Text = "Enable audio (muted by default)",
                Location = new Point(234, 98),
                Size = new Size(214, 24)
            };
            chkSound.CheckedChanged += (s, e) => ApplyAudio();

            Controls.Add(pathLabel);
            Controls.Add(btnBrowse);
            Controls.Add(btnApply);
            Controls.Add(btnRemove);
            Controls.Add(chkStartup);
            Controls.Add(chkSound);
        }

        private void OnAppLoad(bool startMinimized)
        {
            // Below-normal priority: the app serves the desktop quietly and never competes with games/browsers for CPU
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }

            suppressStartupEvent = true;
            try { chkStartup.Checked = IsStartupEnabled(); } catch { }
            suppressStartupEvent = false;

            // Re-apply the last selected video automatically (important at startup)
            string cfg = LoadConfig();
            if (!string.IsNullOrEmpty(cfg) && File.Exists(cfg))
            {
                videoPath = cfg;
                SetPathLabel(cfg);
                ApplyWallpaper(cfg, silent: true);
            }

            if (startMinimized)
                Hide();

            // Cheap periodic watchdog: touches windows only when something actually changed — every 3 seconds
            stateTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            stateTimer.Tick += EnsureWallpaperState;
            stateTimer.Start();

            // Video watchdog: a light check every 250 ms (only two COM calls in steady state)
            // 1) reveals the window when the first frame arrives (no black flash)
            // 2) seeks back to the start before the last ~0.4 s — Windows Media never reaches its "ended" state (the black screen)
            watchdogTimer = new System.Windows.Forms.Timer { Interval = 250 };
            watchdogTimer.Tick += (s, e) => WatchdogTick();
            watchdogTimer.Start();

            // Auto pause/resume on screen lock or sleep — event-driven, zero polling
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

            // Trim the working set once the startup load is done — frees pages the runtime allocated
            // for initialization that are no longer needed (keeps RAM usage low on weak PCs)
            try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1)); } catch { }
        }

        /// <summary>Returns the cached OCX reference, or fetches (and caches) it from the player control.</summary>
        private dynamic GetCurrentOcx()
        {
            dynamic ocx = ocxCache;
            if (ocx != null) return ocx;
            try { ocx = wallpaperWindow.Player.Ocx; } catch { ocx = null; }
            if (ocx != null) ocxCache = ocx;
            return ocx;
        }

        private void ShowSettings()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                if (!balloonShown)
                {
                    balloonShown = true;
                    trayIcon.ShowBalloonTip(2000, "Video Wallpaper", "Video Wallpaper is still running in the tray. Use Exit to quit.", ToolTipIcon.Info);
                }
            }
        }

        private void BrowseVideo()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Select a video file";
                try
                {
                    string vids = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                    dlg.InitialDirectory = Directory.Exists(vids) ? vids : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                }
                catch { }
                dlg.Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.m4v;*.mpg;*.mpeg;*.3gp|All files|*.*";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    videoPath = dlg.FileName;
                    SetPathLabel(videoPath);

                }
            }
        }

        private void SetPathLabel(string path)
        {
            pathLabel.Text = path;
            pathLabel.ForeColor = Color.Black;
        }



        private bool ApplyWallpaper(string path, bool silent)
        {
            lastApplySilent = silent;
            if (string.IsNullOrEmpty(path))
            {
                if (!silent)
                {
                    BrowseVideo();
                    path = videoPath;
                }
                if (string.IsNullOrEmpty(path)) return false;
            }
            if (!File.Exists(path))
            {
                if (!silent)
                    MessageBox.Show(this, "File not found:\n" + path, "Video Wallpaper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            try
            {
                if (!wallpaperApplied)
                {
                    if (!EmbedToDesktop())
                    {

                        if (!silent)
                            MessageBox.Show(this,
                                "Could not find the correct layer to place the video behind the desktop icons on this system.\nYour desktop was not changed — press Ctrl+Alt+W if you need to undo anything.",
                                "Video Wallpaper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return false;
                    }
                }

                // When re-applying over a running wallpaper, go fully transparent until the first frame arrives (no reload flash)
                if (wallpaperApplied && wallpaperWindow.Visible)
                {
                    pendingShow = true;
                    pendingShowTicks = 0;
                    SetWindowAlpha(0, addLayered: true);
                }
                object rawOcx = wallpaperWindow.Player.Ocx;
                if (rawOcx == null)
                    throw new Exception("Could not create Windows Media Player — make sure it is installed and enabled on this PC.");
                dynamic ocx = rawOcx;
                ocxCache = ocx; // cache for the watchdog instead of re-querying via GetOcx() every 250ms
                try { ocx.controls.stop(); } catch { }
                try { ocx.uiMode = "none"; } catch { }
                try { ocx.windowlessVideo = true; } catch { }
                try { ocx.enableErrorDialogs = false; } catch { }
                try { ocx.enableContextMenu = false; } catch { }
                try { ocx.stretchToFit = true; } catch { }
                try { ocx.settings.maintainAspectRatio = true; } catch { } // keep the video's aspect ratio while filling the screen — no distortion
                try { ocx.fullScreen = false; } catch { }
                // WMP settings — failures here are non-fatal (ignored instead of breaking everything)
                dynamic settings = null;
                try { settings = ocx.settings; } catch { }
                if (settings != null)
                {
                    try { settings.autoStart = true; } catch { }
                    try { settings.mute = !chkSound.Checked; } catch { }
                    try { settings.setMode("loop", true); } catch { }
                }
                // Play it: native loop mode + the watchdog seeks back before the last 0.4 s
                // → the media never reaches its "ended" state = no black flash, with a single media object (light for weak PCs)
                ocx.URL = path;
                cachedDuration = -1.0; // new video → duration is re-read from scratch
                try { ocx.settings.setMode("loop", true); } catch { }
                try { ocx.controls.play(); } catch { }
                // IMPORTANT: mute must be applied AFTER the media starts —
                // Windows Media Player drops a mute that is set before the file is loaded.
                // Set it on both the settings object and the active media object (belt and braces).
                try { ocx.settings.mute = !chkSound.Checked; } catch { }
                try { ocx.currentMedia.settings.mute = !chkSound.Checked; } catch { }

                wallpaperApplied = true;
                SaveConfig(path);

                return true;
            }
            catch (Exception ex)
            {
                // Important: roll back — return the desktop to its previous state instead of leaving a full-screen window behind
                RollbackWallpaper();
                if (!silent)
                    MessageBox.Show(this, "Could not apply the wallpaper, desktop restored:\n" + ex.Message,
                        "Video Wallpaper", MessageBoxButtons.OK, MessageBoxIcon.Error);

                return false;
            }
        }

        /// <summary>
        /// The standard trick: send 0x052C with wParam=0xD to Progman, which splits the desktop
        /// into two WorkerW windows (icons view + wallpaper layer). The video window is parented
        /// to the wallpaper WorkerW behind the icons, fully click-through.
        /// </summary>
        private IntPtr FindIconsHost()
        {
            IntPtr iconsHost = IntPtr.Zero;
            Native.EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
            {
                IntPtr shellView = Native.FindWindowEx(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellView != IntPtr.Zero)
                    iconsHost = hWnd;
                return true;
            }, IntPtr.Zero);
            if (iconsHost == IntPtr.Zero)
                iconsHost = Native.FindWindow("Progman", null);
            return iconsHost;
        }

        /// <summary>The WorkerW that comes right after the icons host in z-order — the perfect spot for the video.</summary>
        private IntPtr FindWallpaperHost()
        {
            IntPtr iconsHost = FindIconsHost();
            if (iconsHost == IntPtr.Zero) return IntPtr.Zero;
            return Native.FindWindowEx(IntPtr.Zero, iconsHost, "WorkerW", null);
        }

        [DllImport("user32.dll")]
        static extern bool GetClientRect(IntPtr hWnd, out Native.RECT lpRect);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hWnd, out Native.RECT lpRect);

        [DllImport("user32.dll")]
        static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        // Checks whether the desktop got broken (Explorer restarted, wallpaper layer re-created, ...)
        // by re-parenting the video window to the current wallpaper WorkerW when needed — every 3 seconds,
        // without touching windows in steady state (no flicker, no repaints).
        private void EnsureWallpaperState(object sender, EventArgs e)
        {
            if (!wallpaperApplied) return;
            try
            {
                tickCount++;
                IntPtr target = hostCache;
                bool parentMismatch = target == IntPtr.Zero || GetParent(wallpaperWindow.Handle) != target;
                bool cacheStale = (tickCount % 10 == 0); // full re-scan every ~30 seconds

                if (parentMismatch || cacheStale)
                {
                    target = FindWallpaperHost();

                    // If the desktop never split — re-spawn the wallpaper layer (at most once per 6 rounds)
                    if (target == IntPtr.Zero && spawnRetryTicks % 6 == 0)
                    {
                        IntPtr progman = Native.FindWindow("Progman", null);
                        IntPtr res;
                        if (progman != IntPtr.Zero)
                        {
                            Native.SendMessageTimeout(progman, Native.WM_SPAWN_WORKER, new IntPtr(Native.SPAWN_WPARAM), IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 1500, out res);
                            Native.SendMessageTimeout(progman, Native.WM_SPAWN_WORKER, new IntPtr(Native.SPAWN_WPARAM), new IntPtr(1), Native.SMTO_ABORTIFHUNG, 1500, out res);
                            System.Threading.Thread.Sleep(100);
                            target = FindWallpaperHost();
                        }
                    }
                    if (target == IntPtr.Zero) target = FindIconsHost();
                    if (target == IntPtr.Zero) { WatchdogTick(); return; } // Explorer is rebuilding — try again later
                    hostCache = target;
                }
                spawnRetryTicks++;

                if (GetParent(wallpaperWindow.Handle) != target)
                {
                    Native.SetParent(wallpaperWindow.Handle, target);
                    SetFullSize(target);
                }
                else
                {
                    // Only verify the size, without touching anything else
                    Native.RECT rc;
                    GetClientRect(target, out rc);
                    Native.RECT wr;
                    GetWindowRect(wallpaperWindow.Handle, out wr);
                    int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
                    if (w != (wr.Right - wr.Left) || h != (wr.Bottom - wr.Top))
                        Native.SetWindowPos(wallpaperWindow.Handle, Native.HWND_BOTTOM, 0, 0, w, h,
                            Native.SWP_NOACTIVATE | Native.SWP_NOMOVE); // no move, only resize — prevents repaint flashes
                }
            }
            catch { }
        }

        /// <summary>
        /// Fully transparent / fully visible: alpha=0 = technically visible (all WinForms handles are created and
        /// WMP paints) but invisible to the user and transparent to clicks —
        /// revealed by switching alpha to 255 when the first real frame arrives.
        /// </summary>
        private void SetWindowAlpha(byte alpha, bool addLayered)
        {
            IntPtr h = wallpaperWindow.Handle;
            if (addLayered)
            {
                long ex = unchecked((uint)Native.GetWindowLong(h, Native.GWL_EXSTYLE));
                ex |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE;
                Native.SetWindowLong(h, Native.GWL_EXSTYLE, unchecked((int)ex));
            }
            Native.SetLayeredWindowAttributes(h, 0, alpha, Native.LWA_ALPHA);
        }

        private void SetFullSize(IntPtr target)
        {
            Native.RECT rc;
            GetClientRect(target, out rc);
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (w <= 0 || h <= 0) { Rectangle vs = SystemInformation.VirtualScreen; w = vs.Width; h = vs.Height; }
            Native.SetWindowPos(wallpaperWindow.Handle, Native.HWND_BOTTOM, 0, 0, w, h,
                Native.SWP_NOACTIVATE);
        }

        /// <summary>
        /// Video watchdog every 250 ms — only two COM calls in steady state:
        /// 1) reveals the wallpaper window only when the first frame arrives (no black flash while decoding)
        /// 2) the main rule against the black screen: forces the video back to the start before the last ~0.4 s —
        ///    Windows Media never reaches its "ended" state (the real source of the loop black flicker)
        /// 3) safety net: if it ever reaches "ended/stopped" (by any path) → seek back instantly
        /// 4) if the file is broken/unsupported and no frame arrives within ~10 s → roll back, desktop restored
        /// </summary>
        private void WatchdogTick()
        {
            if (!wallpaperApplied) return;
            try
            {
                dynamic ocx = GetCurrentOcx();
                if (ocx == null) return;
                int ps = -1;
                try { ps = (int)ocx.playState; } catch { return; }

                if (pendingShow)
                {
                    if (ps == 3 || ps == 6 || ps == 7) { RevealWallpaperWindow(); return; } // playing/buffering/waiting = frames on the way
                    if (ps <= 1 || ps >= 10)                                    // stopped/broken/error: no frames coming
                    {
                        pendingShowTicks++;
                        if (pendingShowTicks >= 40) AbortPendingLoad();          // ~10 seconds
                    }
                    return; // 2=paused (locked) and 6/7/9=preparing/buffering — not counted, not revealed yet
                }

                // Read the video length once from the media and cache it — never polled per tick
                if (cachedDuration <= 1.0)
                {
                    try { double d = (double)ocx.currentMedia.duration; if (d > 1.0 && d < 86400.0) cachedDuration = d; } catch { }
                }

                // Safety net: if it reached "stopped/ended" via any path → seek back instantly
                if (ps == 1 || ps == 8) { SeekZeroAndPlay(ocx); return; }

                // Main rule: before the last ~0.4 s of the video → seek back to the start
                // so the media never reaches the "ended" state (no black flicker)
                if (ps == 3 || ps == 7)
                {
                    double pos = -1.0;
                    try { pos = (double)ocx.controls.currentPosition; } catch { return; }
                    if (cachedDuration > 1.0 && pos >= 0.0 && pos >= cachedDuration - 0.40)
                        try { ocx.controls.currentPosition = 0; } catch { } // no play() — it is already playing
                }
            }
            catch { }
        }

        private void RevealWallpaperWindow()
        {
            pendingShow = false;
            pendingShowTicks = 0;
            try
            {
                SetWindowAlpha(255, addLayered: false); // the window is technically visible already — just make it opaque
                if (hostCache != IntPtr.Zero) SetFullSize(hostCache);
            }
            catch { }
        }

        /// <summary>No frame arrived within the timeout: clean rollback — desktop restored, no permanent black screen</summary>
        private void AbortPendingLoad()
        {
            pendingShow = false;
            string msg = "Could not play the video (corrupt file or unsupported format). Your desktop was restored.";
            RollbackWallpaper();

            if (!lastApplySilent)
                MessageBox.Show(this, msg + "\nTry an MP4 file encoded with H.264.", "Video Wallpaper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ======================= Session lock / sleep: pause and resume =======================
        private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock ||
                e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLogoff)
                PauseForSession();
            else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock ||
                     e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLogon)
                ResumeFromSession();
        }

        private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Suspend) PauseForSession();
            else if (e.Mode == Microsoft.Win32.PowerModes.Resume) ResumeFromSession();
        }

        private void PauseForSession()
        {
            if (!wallpaperApplied || pausedForSession) return;
            try { dynamic ocx = GetCurrentOcx(); if (ocx != null) ocx.controls.pause(); pausedForSession = true; } catch { }
        }

        private void ResumeFromSession()
        {
            if (!pausedForSession) return;
            pausedForSession = false;
            try { dynamic ocx = GetCurrentOcx(); if (ocx != null) ocx.controls.play(); } catch { }
        }

        private void SeekZeroAndPlay(dynamic ocx)
        {
            try { ocx.controls.currentPosition = 0; } catch { }
            try { ocx.controls.play(); } catch { }
        }

        private bool EmbedToDesktop()
        {
            // Windows Media Player's ActiveX is only created for a *visible* window — a hidden window makes GetOcx() return null.
            // Solution: a visible but fully transparent window (alpha=0) — all handles are created and WMP paints,
            // the user sees nothing (no black flash whatsoever)
            SetWindowAlpha(0, addLayered: true);
            wallpaperWindow.Show();

            // Spawn the background WorkerW (wParam=0xD — otherwise the desktop does not split)
            IntPtr progman = Native.FindWindow("Progman", null);
            IntPtr res;
            if (progman != IntPtr.Zero)
            {
                Native.SendMessageTimeout(progman, Native.WM_SPAWN_WORKER, new IntPtr(Native.SPAWN_WPARAM), IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 3000, out res);
                Native.SendMessageTimeout(progman, Native.WM_SPAWN_WORKER, new IntPtr(Native.SPAWN_WPARAM), new IntPtr(1), Native.SMTO_ABORTIFHUNG, 3000, out res);
            }

            // Brief wait for the split, then find the wallpaper window
            IntPtr host = IntPtr.Zero;
            for (int attempt = 0; attempt < 6 && host == IntPtr.Zero; attempt++)
            {
                System.Threading.Thread.Sleep(150);
                host = FindWallpaperHost();
            }
            if (host == IntPtr.Zero)
                host = FindIconsHost(); // last resort: deepest child of the icons host (directly behind it)

            if (host == IntPtr.Zero)
            {
                wallpaperWindow.Hide();
                return false;
            }

            if (!Native.SetParent(wallpaperWindow.Handle, host))
            {
                wallpaperWindow.Hide();
                return false;
            }

            SetFullSize(host);
            hostCache = host;
            pendingShow = true;   // the window is not shown until the first real frame arrives
            pendingShowTicks = 0;
            return true;
        }

        /// <summary>Panic button (Ctrl+Alt+W) or automatic rollback after an error: removes the wallpaper and restores the desktop.</summary>
        private void RollbackWallpaper()
        {
            RemoveWallpaper();
        }

        private void RemoveWallpaper()
        {
            try
            {
                dynamic ocx = GetCurrentOcx();
                if (ocx != null)
                {
                    try { ocx.controls.stop(); } catch { }
                    try { ocx.URL = null; } catch { }
                }
                ocxCache = null; // drop the cached OCX reference as well
                cachedDuration = -1.0;
            }
            catch { }

            try
            {
                if (wallpaperWindow.Parent != null)
                    Native.SetParent(wallpaperWindow.Handle, IntPtr.Zero);
                wallpaperWindow.Hide();
            }
            catch { }

            // Repaint the original Windows wallpaper
            try
            {
                string wp;
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                    wp = k == null ? null : k.GetValue("Wallpaper") as string;
                Native.SystemParametersInfoW(Native.SPI_SETDESKWALLPAPER, 0, wp ?? string.Empty, Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE);
            }
            catch { }

            wallpaperApplied = false;
            pendingShow = false;
            pausedForSession = false;
            DeleteConfig();

        }

        private void ApplyAudio()
        {
            if (!wallpaperApplied) return;
            bool mute = !chkSound.Checked;
            dynamic ocx = GetCurrentOcx();
            if (ocx == null) return;
            try { ocx.settings.mute = mute; } catch { }
            try { ocx.currentMedia.settings.mute = mute; } catch { }
        }

        private void QuitApp()
        {
            if (stateTimer != null) stateTimer.Stop();
            if (watchdogTimer != null) watchdogTimer.Stop();
            try
            {
                // Unsubscribe from system events
                Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
                Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch { }
            RemoveWallpaper();
            trayIcon.Visible = false;
            trayIcon.Dispose();
            Application.Exit();
        }

        // ======================= Registry: startup with Windows + save settings =======================
        private static bool IsStartupEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(AppName) != null;
        }

        private static void SetStartup(bool enable)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enable)
                    k.SetValue(AppName, "\"" + Application.ExecutablePath + "\" --minimized");
                else
                    k.DeleteValue(AppName, false);
            }
        }

        private static void SaveConfig(string path)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(ConfigKey))
                    k.SetValue("VideoPath", path);
            }
            catch { }
        }

        private static string LoadConfig()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(ConfigKey))
                    return k == null ? null : k.GetValue("VideoPath") as string;
            }
            catch { return null; }
        }

        private static void DeleteConfig()
        {
            try { Registry.CurrentUser.DeleteSubKey(ConfigKey, false); } catch { }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, "VideoWallpaper_SingleInstance_Mutex", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("VideoWallpaper is already running in the system tray.", "Video Wallpaper", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                bool minimized = false;
                foreach (string a in args)
                    if (string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)) minimized = true;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(minimized));
            }
        }
    }
}

