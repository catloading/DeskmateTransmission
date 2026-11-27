using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DeskLink
{
    internal sealed class TrayService : IDisposable
    {
        private sealed class Notice { internal string Title, Text; internal Action Open; }
        private readonly Queue<Notice> pending = new Queue<Notice>();
        private readonly Forms.NotifyIcon icon;
        private readonly Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
        private readonly Icon image;
        private Notice active;
        private readonly DispatcherTimer expiry = new DispatcherTimer();

        internal TrayService(Action show, Action quit)
        {
            using (var stream = Application.GetResourceStream(new Uri("/同桌传;component/Assets/desklink.ico", UriKind.Relative)).Stream)
            using (var source = new Icon(stream)) image = (Icon)source.Clone();
            menu.Items.Add("打开同桌传", null, (s, e) => show());
            menu.Items.Add("退出", null, (s, e) => quit());
            icon = new Forms.NotifyIcon { Icon = image, Text = "同桌传", ContextMenuStrip = menu, Visible = true };
            icon.DoubleClick += (s, e) => show();
            expiry.Tick += (s, e) => FinishNotice();
            icon.BalloonTipClicked += (s, e) => OpenActiveNotice();
            icon.BalloonTipClosed += (s, e) => FinishNotice();
        }

        internal void OpenActiveNotice()
        {
            var open = active?.Open;
            if (open == null) return;
            active.Open = null;
            expiry.Stop(); expiry.Interval = TimeSpan.FromMilliseconds(300); expiry.Start();
            open();
        }

        internal void Notify(string name, string text, Action open)
        {
            // Keep the notification linked to the conversation that produced it.
            if (pending.Count < 16) pending.Enqueue(new Notice { Title = name, Text = text, Open = open });
            ShowNext();
        }

        private void ShowNext()
        {
            if (active != null || pending.Count == 0) return;
            active = pending.Dequeue();
            expiry.Interval = TimeSpan.FromSeconds(30); expiry.Start();
            icon.ShowBalloonTip(3500, active.Title, active.Text, Forms.ToolTipIcon.Info);
        }

        private void FinishNotice() { expiry.Stop(); active = null; ShowNext(); }

        public void Dispose() { expiry.Stop(); pending.Clear(); icon.Visible = false; icon.Dispose(); menu.Dispose(); image.Dispose(); }
    }
}
