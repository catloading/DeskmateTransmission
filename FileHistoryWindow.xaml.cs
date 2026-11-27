using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DeskLink
{
    public partial class FileHistoryWindow : Window
    {
        private readonly LocalStore store;
        private readonly string device;
        private static readonly int[] Days = { -1, 7, 30, 90, 0 };
        private List<ChatItem> records;
        private bool ready;

        internal FileHistoryWindow(LocalStore store, string device, string name, List<ChatItem> records)
        {
            this.store = store; this.device = device; this.records = records;
            InitializeComponent(); Title = name + " · 会话文件";
            Retention.SelectedIndex = Array.IndexOf(Days, store.RetentionDays);
            ready = true; RefreshResults();
        }

        internal static bool Matches(ChatItem item, string device, string query, string type, DateTime? day)
        {
            if (!item.IsFile || (item.DeviceId ?? "legacy:" + (item.Peer ?? "旧版设备")) != device) return false;
            string extension = item.IsFolder ? "文件夹" : Path.GetExtension(item.Content).TrimStart('.');
            return item.Content.IndexOf(query.Trim(), StringComparison.OrdinalIgnoreCase) >= 0 &&
                (type.Trim().Length == 0 || extension.Equals(type.Trim().TrimStart('.'), StringComparison.OrdinalIgnoreCase)) &&
                (!day.HasValue || item.Utc.ToLocalTime().Date == day.Value.Date);
        }

        private void FilterChanged(object sender, RoutedEventArgs e) { if (ready) RefreshResults(); }
        private void RefreshResults()
        {
            var rows = records.Where(i => Matches(i, device, Query.Text, FileType.Text, Day.SelectedDate))
                .OrderByDescending(i => i.Utc).ToList();
            Results.ItemsSource = rows;
            Empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Feedback.Text = rows.Count + " 项";
        }

        private void RetentionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ready || Retention.SelectedIndex < 0) return;
            try { store.SetRetention(Days[Retention.SelectedIndex]); records = store.Load(); RefreshResults(); }
            catch (Exception ex) { Feedback.Text = ex.Message; }
        }

        private void Reveal(object sender, RoutedEventArgs e)
        {
            var item = Results.SelectedItem as ChatItem;
            if (item == null) return;
            if (!File.Exists(item.FilePath) && !Directory.Exists(item.FilePath)) { Feedback.Text = "文件已被移动或删除。"; return; }
            try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + item.FilePath + "\"") { UseShellExecute = false }); }
            catch (Exception ex) { Feedback.Text = ex.Message; }
        }
    }
}
