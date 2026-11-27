using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeskLink.Transfer;

namespace DeskLink
{
    public partial class PairWindow : Window
    {
        private readonly bool hosting;
        private readonly DeviceNetwork network;
        private readonly string targetId;
        private readonly string suggestedAddress;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private Invitation invitation;
        private List<Discovery.LocalAddress> addresses;
        private bool closed;
        internal Peer ConnectedPeer { get; private set; }

        internal PairWindow(bool create, DeviceNetwork service = null, string address = null, string selectedId = null)
        {
            InitializeComponent();
            hosting = create;
            network = service;
            targetId = selectedId;
            suggestedAddress = address;
            Heading.Text = Title = create ? "我的配对码" : "添加设备";
            Intro.Text = create ? "在另一台电脑上点「输入配对码」，输入下方的代码。首次配对后，两台电脑会记住彼此。" :
                "请在对方电脑点击「显示配对码」。输入它显示的代码，或将完整邀请粘贴到地址栏。";
            HostPanel.Visibility = create ? Visibility.Visible : Visibility.Collapsed;
            JoinPanel.Visibility = create ? Visibility.Collapsed : Visibility.Visible;
            MainAction.Content = create ? "复制邀请" : "连接";
            if (!string.IsNullOrEmpty(address)) AddressInput.Text = address;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (hosting) await CreateAsync();
            else { AddressInput.Focus(); await RefreshAsync(); }
        }

        private void ShowFeedback(string message, bool error = false)
        {
            Feedback.Text = message;
            Feedback.Foreground = error ? Brushes.Firebrick : (Brush)FindResource("Muted");
        }

        private void Finish(Peer peer)
        {
            if (closed) { peer.Dispose(); return; }
            ConnectedPeer = peer;
            DialogResult = true;
        }

        private async Task CreateAsync()
        {
            MainAction.IsEnabled = false;
            ShowFeedback("正在准备连接…");
            try
            {
                var created = await Task.Run(() => new Invitation(Environment.MachineName, network?.Local));
                if (closed) { created.Dispose(); return; }
                invitation = created;
                network?.SetInvitationPort(created.Port);
                addresses = Discovery.Addresses();
                HostAddress.ItemsSource = addresses.Select(a => a.Ip + ":" + created.Port + "  ·  " + a.Adapter).ToList();
                HostAddress.SelectedIndex = 0;
                InvitationCode.Text = Pairing.FormatCode(created.Code);
                MainAction.Content = "复制邀请";
                MainAction.IsEnabled = true;
                ShowFeedback("正在等待加入。配对码 10 分钟内有效，成功连接后失效。");
                Finish(await created.Accepted);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (closed) return;
                invitation?.Dispose(); invitation = null;
                network?.SetInvitationPort(0);
                InvitationCode.Text = "—";
                HostAddress.ItemsSource = null;
                MainAction.Content = "重新创建";
                MainAction.IsEnabled = true;
                ShowFeedback(ex.Message, true);
            }
        }

        private async Task RefreshAsync()
        {
            RefreshButton.IsEnabled = false;
            NearbyHint.Text = "正在查找…";
            try
            {
                var devices = await Task.Run(Discovery.Find);
                if (closed) return;
                NearbyList.ItemsSource = devices;
                NearbyHint.Visibility = Visibility.Visible;
                NearbyHint.Text = devices.Count == 0 ? "未发现设备，可直接填写下方地址" : "请选择一台电脑";
                if (devices.Count > 0 && string.IsNullOrWhiteSpace(AddressInput.Text)) NearbyList.SelectedIndex = 0;
                else MatchNearbyAddress();
            }
            catch (Exception ex) when (ex is SocketException || ex is System.Net.NetworkInformation.NetworkInformationException)
            {
                if (!closed) NearbyHint.Text = "无法自动发现，可直接填写下方地址";
            }
            finally { if (!closed) RefreshButton.IsEnabled = true; }
        }

        private async void Refresh(object sender, RoutedEventArgs e) { await RefreshAsync(); }

        private void ChooseNearby(object sender, SelectionChangedEventArgs e)
        {
            var selected = NearbyList.SelectedItem as Discovery.Nearby;
            if (selected == null) return;
            NearbyHint.Visibility = Visibility.Collapsed;
            if (AddressInput.Text != selected.Address) AddressInput.Text = selected.Address;
        }

        private void MatchNearbyAddress()
        {
            var match = NearbyList.Items.OfType<Discovery.Nearby>().FirstOrDefault(d => d.Address == AddressInput.Text.Trim());
            NearbyList.SelectedItem = match;
            NearbyHint.Visibility = match == null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddressChanged(object sender, TextChangedEventArgs e)
        {
            if (AddressHint == null) return;
            AddressHint.Visibility = AddressInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            string address, code;
            if (Pairing.TryInvite(AddressInput.Text, out address, out code))
            {
                AddressInput.Text = address; CodeInput.Text = code;
                ShowFeedback("已填入邀请中的地址和配对码。");
            }
            else MatchNearbyAddress();
        }

        private void CodeChanged(object sender, TextChangedEventArgs e)
        {
            if (CodeHint != null) CodeHint.Visibility = CodeInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void PrimaryClick(object sender, RoutedEventArgs e)
        {
            if (hosting)
            {
                if (invitation == null) { await CreateAsync(); return; }
                if (HostAddress.SelectedIndex < 0) return;
                string address = addresses[HostAddress.SelectedIndex].Ip + ":" + invitation.Port;
                try
                {
                    Clipboard.SetText(Pairing.Scheme + address + "/" + Pairing.FormatCode(invitation.Code));
                    ShowFeedback("邀请已复制，可粘贴到另一台电脑的「对方地址或完整邀请」。");
                }
                catch (System.Runtime.InteropServices.ExternalException) { ShowFeedback("剪贴板正忙，也可以手动输入地址和配对码。", true); }
                return;
            }
            string endpoint = AddressInput.Text, code = CodeInput.Text;
            if (targetId != null && network != null && endpoint == suggestedAddress)
            {
                var seen = network.Snapshot().FirstOrDefault(d => d.Id == targetId);
                // The invitation may have opened on a fallback port after this dialog was shown.
                if (seen?.PairAddress != null && seen.PairAddress.Split(':')[0] == endpoint.Split(':')[0])
                    endpoint = seen.PairAddress;
            }
            JoinPanel.IsEnabled = false;
            MainAction.IsEnabled = false;
            ShowFeedback("正在连接并验证配对码…");
            CancellationToken token = lifetime.Token;
            try
            {
                var peer = await Task.Run(() => Pairing.Join(endpoint, code, Environment.MachineName, token, network?.Local));
                if (targetId != null && peer.Device?.Id != targetId)
                { peer.Dispose(); ShowFeedback("实际连接的设备与所选设备不同，请核对地址。", true); }
                else Finish(peer);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!closed) ShowFeedback(ex is SocketException ?
                    "无法连接该地址。请检查对方是否正在等待、地址是否正确，以及专用网络的防火墙设置。" : ex.Message, true);
            }
            finally { if (!closed) { JoinPanel.IsEnabled = true; MainAction.IsEnabled = true; } }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            closed = true;
            lifetime.Cancel();
            invitation?.Dispose();
            if (hosting) network?.SetInvitationPort(0);
            lifetime.Dispose();
        }
    }
}
