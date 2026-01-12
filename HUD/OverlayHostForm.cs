#nullable enable
using CinecorePlayer2025.Engines;
using CinecorePlayer2025.HUD;
using CinecorePlayer2025.Utilities;
using DirectShowLib;
using FFmpeg.AutoGen;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HDRMode = global::CinecorePlayer2025.Utilities.HdrMode;
using VRChoice = global::CinecorePlayer2025.Utilities.VideoRendererChoice;

namespace CinecorePlayer2025
{

    // ======= OverlayHostForm – top-level davvero trasparente =======
    internal sealed class OverlayHostForm : Form
    {
        public Panel Surface { get; } = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };

        public OverlayHostForm()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;

            BackColor = Color.Black;           // colorkey
            TransparencyKey = Color.Black;

            Controls.Add(Surface);
            Surface.BackColor = this.TransparencyKey;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryApplyLayeredColorKey(this.TransparencyKey);
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_ERASEBKGND = 0x0014;
            if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }
            base.WndProc(ref m);
        }

        private void TryApplyLayeredColorKey(Color key)
        {
            if (!IsHandleCreated) return;
            uint rgb = (uint)(key.R | (key.G << 8) | (key.B << 16));
            try { SetLayeredWindowAttributes(this.Handle, rgb, 255, LWA_COLORKEY); } catch { }
            try
            {
                Win32.SetWindowPos(this.Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOZORDER | Win32.SWP_NOSIZE | Win32.SWP_NOMOVE | Win32.SWP_FRAMECHANGED);
            }
            catch { }
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;  // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000;  // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00080000;  // WS_EX_LAYERED
                return cp;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(this.TransparencyKey);
        }

        public void SyncTo(Form owner)
        {
            if (!owner.Visible) return;
            var rc = owner.RectangleToScreen(owner.ClientRectangle);
            Bounds = rc;
            if (Visible) { try { BringToFront(); } catch { } }
        }

        private const int SWP_NOACTIVATE = 0x0010;

        public void SyncToScreen(Rectangle screenRect)
        {
            if (screenRect.Width <= 0 || screenRect.Height <= 0) return;
            try
            {
                Win32.SetWindowPos(this.Handle, IntPtr.Zero,
                    screenRect.X, screenRect.Y, screenRect.Width, screenRect.Height,
                    Win32.SWP_SHOWWINDOW | Win32.SWP_NOZORDER | SWP_NOACTIVATE);
            }
            catch
            {
                Bounds = screenRect; // fallback
            }
            if (Visible) { try { BringToFront(); } catch { } }
        }

        public void SetClickThrough(bool passThrough)
        {
            if (!IsHandleCreated) return;
            int ex = GetWindowLong(this.Handle, GWL_EXSTYLE);
            if (passThrough) ex |= WS_EX_TRANSPARENT; else ex &= ~WS_EX_TRANSPARENT;
            SetWindowLong(this.Handle, GWL_EXSTYLE, ex);
            Win32.SetWindowPos(this.Handle, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_FRAMECHANGED | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER);
        }

        [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        const int GWL_EXSTYLE = -20;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const int WS_EX_TRANSPARENT = 0x00000020;
        const uint LWA_COLORKEY = 0x00000001;

        public void SetInteractive(bool on)
        {
            if (!IsHandleCreated) return;
            int ex = GetWindowLong(this.Handle, GWL_EXSTYLE);
            if (on) ex &= ~WS_EX_NOACTIVATE; else ex |= WS_EX_NOACTIVATE;
            SetWindowLong(this.Handle, GWL_EXSTYLE, ex);
            Win32.SetWindowPos(this.Handle, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SWP_FRAMECHANGED | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER);
        }
    }
}
