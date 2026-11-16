using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Core.ClipboardSync;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private TextBlock? _lanSyncStatus;

    private void AddLanSync()
    {
        var service = _app.ClipboardSync;
        var panel = new StackPanel();
        var card = new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(22, 18, 22, 14),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 16)
        };
        card.SetResourceReference(Border.BackgroundProperty, "SettingsSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        panel.Children.Add(TextLine(Loc.T("局域网同步", "Local network sync"), 16, TextPrimary, FontWeights.SemiBold));
        var status = TextLine("", 12, TextSecondary);
        status.Margin = new Thickness(0, 6, 0, 12);
        status.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(status);
        _lanSyncStatus = status;

        bool paired = service.Accounts.Lan is not null;
        if (paired)
        {
            var unpair = ActionButton(Loc.T("解除配对", "Unpair"), () =>
            {
                try
                {
                    service.SetLanPeer(null);
                    Change(s => s.LanClipboardSync = false);
                    ShowPage(ClipboardSyncSettingsPage);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    status.Text = Loc.T("无法保存配对设置，请重试。", "Could not save the pairing settings. Try again.");
                }
            });
            unpair.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(unpair);
        }
        else
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (bool host in new[] { true, false })
            {
                var button = ActionButton(host ? Loc.T("创建邀请码", "Create invitation") : Loc.T("输入邀请码", "Enter invitation"), () => { });
                button.Margin = new Thickness(0, 0, 8, 0);
                button.Click += async (_, _) => await OpenLanPairingAsync(host);
                buttons.Children.Add(button);
            }
            panel.Children.Add(buttons);
        }
        panel.Children.Add(Toggle(Loc.T("自动同步文字", "Automatically sync text"), S.LanClipboardSync && paired && !service.TestMode,
            value => Change(s => s.LanClipboardSync = value), enabled: paired && !service.TestMode,
            help: Loc.T("两台设备均需开启。只同步连接期间新复制的文字。", "Enable this on both devices to sync new text copied while connected.")));
        Target.Children.Add(card);
    }

    private void RefreshLanSyncStatus()
    {
        if (_lanSyncStatus is null) return;
        var service = _app.ClipboardSync;
        _lanSyncStatus.Text = service.Accounts.Lan is null
            ? Loc.T("将同一局域网内的两台 Windows 电脑配对。", "Pair two Windows PCs on the same local network.")
            : !S.LanClipboardSync || service.TestMode
                ? Loc.T("已配对 · 同步已暂停", "Paired · Sync paused")
                : service.LanStatus;
    }

    private async Task OpenLanPairingAsync(bool host)
    {
        if (_clipboardConnectorOpen) return;
        _clipboardConnectorOpen = true;
        var panel = new StackPanel();
        var dialog = SyncDialog(host ? Loc.T("创建局域网邀请码", "Create LAN invitation") : Loc.T("输入局域网邀请码", "Enter LAN invitation"), panel);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
        var token = stop.Token;
        dialog.Closed += (_, _) => stop.Cancel();
        var note = TextLine(host
            ? Loc.T("在另一台电脑的 BrimDeck 中输入邀请码。邀请码两分钟内有效，请仅提供给要配对的设备。", "Enter the invitation in BrimDeck on the other PC. It expires in two minutes. Share it only with the device you want to pair.")
            : Loc.T("输入另一台电脑上的 BrimDeck 邀请码。两台电脑必须位于同一局域网。", "Enter the BrimDeck invitation from the other PC. Both PCs must be on the same local network."), 12, TextSecondary);
        note.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(note);
        var addresses = new ComboBox
        {
            Margin = new Thickness(0, 12, 0, 0),
            Visibility = host ? Visibility.Visible : Visibility.Collapsed,
            DisplayMemberPath = nameof(LanInterface.Label)
        };
        AutomationProperties.SetName(addresses, Loc.T("本机局域网地址", "Local network address"));
        panel.Children.Add(addresses);
        var input = new TextBox
        {
            Style = (Style)FindResource("SettingsField"),
            IsReadOnly = host,
            Visibility = host ? Visibility.Collapsed : Visibility.Visible,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = 128,
            Margin = new Thickness(0, 12, 0, 0)
        };
        AutomationProperties.SetName(input, Loc.T("局域网邀请码", "LAN invitation"));
        panel.Children.Add(input);
        var status = TextLine("", 12, TextSecondary);
        status.TextWrapping = TextWrapping.Wrap;
        status.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(status);
        var submit = ActionButton(host ? Loc.T("生成邀请码", "Generate invitation") : Loc.T("连接", "Connect"), () => { }, true);
        submit.Margin = new Thickness(0, 12, 0, 0);
        submit.IsEnabled = false;
        addresses.SelectionChanged += (_, _) => submit.IsEnabled = addresses.SelectedItem is LanInterface;
        input.TextChanged += (_, _) => { if (!host) submit.IsEnabled = !string.IsNullOrWhiteSpace(input.Text); };
        panel.Children.Add(submit);
        Task active = Task.CompletedTask;

        void RefreshAddresses()
        {
            IReadOnlyList<LanInterface> available;
            try { available = LanNetwork.Interfaces(); }
            catch (NetworkInformationException) { available = []; }
            var previous = addresses.SelectedItem as LanInterface;
            addresses.ItemsSource = available;
            addresses.SelectedItem = available.FirstOrDefault(adapter => adapter == previous) ?? LanNetwork.DefaultHost(available);
            submit.IsEnabled = addresses.SelectedItem is LanInterface;
        }
        Button? refresh = null;
        if (host)
        {
            RefreshAddresses();
            refresh = ActionButton(Loc.T("刷新网卡", "Refresh network adapters"), RefreshAddresses);
            refresh.HorizontalAlignment = HorizontalAlignment.Left;
            refresh.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Insert(panel.Children.IndexOf(addresses) + 1, refresh);
            if (!submit.IsEnabled)
                status.Text = addresses.Items.Count == 0
                    ? Loc.T("未找到可用的局域网地址。", "No local network address is available.")
                    : Loc.T("请选择与另一台电脑同子网的网卡地址。", "Select the network address on the same subnet as the other PC.");
        }

        async Task PairAsync()
        {
            submit.IsEnabled = addresses.IsEnabled = false;
            if (refresh is not null) refresh.IsEnabled = false;
            if (!host) input.IsEnabled = false;
            try
            {
                LanClipboardPeer peer;
                if (host)
                {
                    if (addresses.SelectedItem is not LanInterface network) return;
                    using var invitation = new LanClipboardLink.Invitation(network.Address);
                    input.Visibility = Visibility.Visible;
                    input.Text = invitation.Code;
                    status.Text = Loc.T("正在等待另一台电脑连接到 ", "Waiting for the other PC to connect to ") + $"{network.Address}:{invitation.Port}";
                    peer = await invitation.AcceptAsync(token);
                }
                else
                {
                    status.Text = Loc.T("正在连接局域网设备…", "Connecting to the LAN device…");
                    peer = await LanClipboardLink.JoinAsync(input.Text, token);
                }
                token.ThrowIfCancellationRequested();
                _app.ClipboardSync.SetLanPeer(peer);
                dialog.Close();
            }
            catch (Exception ex) when (token.IsCancellationRequested && ex is not OutOfMemoryException) { }
            catch (Exception ex) when (ex is LanHostAddressUnavailableException or LanNetworkChangedException)
            { status.Text = Loc.T("局域网地址已变更。请选择当前地址并重新生成邀请码。", "The network address changed. Select a current address and generate another invitation."); }
            catch (LanPeerOffSubnetException)
            { status.Text = Loc.T("当前没有与邀请地址同子网的连接。", "No local connection shares the invitation's subnet."); }
            catch (OperationCanceledException)
            {
                status.Text = host ? Loc.T("邀请码已过期，请重新生成。", "The invitation expired. Generate a new one.")
                    : Loc.T("连接超时，请检查两台设备是否在同一局域网及邀请码是否仍有效。", "Connection timed out. Check the local network and whether the invitation is still valid.");
            }
            catch (FormatException) { status.Text = Loc.T("邀请码格式不正确。", "The invitation format is invalid."); }
            catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or AuthenticationException or CryptographicException or UnauthorizedAccessException or NetworkInformationException)
            { status.Text = Loc.T("无法配对，请检查局域网连接和邀请码。", "Could not pair. Check the local network and invitation."); }
            finally
            {
                if (host)
                {
                    input.Clear();
                    input.Visibility = Visibility.Collapsed;
                }
                if (!token.IsCancellationRequested)
                {
                    input.IsEnabled = addresses.IsEnabled = true;
                    if (refresh is not null) refresh.IsEnabled = true;
                    if (host) RefreshAddresses();
                    else submit.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
                }
            }
        }
        submit.Click += (_, _) => { if (active.IsCompleted) active = PairAsync(); };
        try
        {
            dialog.ShowDialog();
            await active;
        }
        finally
        {
            _clipboardConnectorOpen = false;
            if (IsVisible && _page == ClipboardSyncSettingsPage) ShowPage(_page);
        }
    }
}
