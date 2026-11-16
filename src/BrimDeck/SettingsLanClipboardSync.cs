using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BrimDeck.Core;
using BrimDeck.Core.ClipboardSync;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    private TextBlock? _lanSyncStatus;

    private void AddLanSync()
    {
        var service = _app.ClipboardSync;
        bool paired = service.Accounts.Lan is not null;
        var action = ActionButton(paired ? Loc.T("解除配对", "Unpair") : Loc.T("配对设备", "Pair device"), () => { }, !paired);
        var panel = AddSyncSection(Loc.T("局域网同步", "Local network sync"),
            Loc.T("两台电脑直接同步文字和图片。", "Sync text and images directly between two PCs."),
            action, out var status);
        _lanSyncStatus = status;
        action.Click += async (_, _) =>
        {
            if (!paired) { await OpenLanPairingAsync(); return; }
            try
            {
                service.SetLanPeer(null);
                Change(s => s.LanClipboardSync = false);
                ShowPage(ClipboardSyncSettingsPage);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            { status.Text = Loc.T("无法保存配对设置，请重试。", "Could not save the pairing settings. Try again."); }
        };
        if (paired)
        {
            panel.Children.Add(SyncDivider());
            panel.Children.Add(Toggle(Loc.T("自动同步文字和图片", "Automatically sync text and images"), S.LanClipboardSync && !service.TestMode,
                value => Change(s => s.LanClipboardSync = value), enabled: !service.TestMode,
                help: Loc.T("两台设备均需开启。图片以 PNG 传输，单张最多 8 MiB。", "Enable this on both devices. Images sync as PNG, up to 8 MiB each.")));
        }
    }

    private void RefreshLanSyncStatus()
    {
        if (_lanSyncStatus is null) return;
        var service = _app.ClipboardSync;
        var peer = service.Accounts.Lan;
        _lanSyncStatus.Text = peer is null ? Loc.T("未配对", "Not paired")
            : !S.LanClipboardSync || service.TestMode ? Loc.T("已配对 · 同步已暂停", "Paired · Sync paused")
            : service.LanStatus;
        if (peer?.DeviceName is { Length: > 0 } name) _lanSyncStatus.Text = name + " · " + _lanSyncStatus.Text;
    }

    private async Task OpenLanPairingAsync()
    {
        if (_clipboardConnectorOpen) return;
        _clipboardConnectorOpen = true;
        var panel = new StackPanel();
        var dialog = SyncDialog(Loc.T("局域网配对", "Pair on your local network"), panel);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        var show = ActionButton(Loc.T("显示配对码", "Show pairing code"), () => { });
        var enter = ActionButton(Loc.T("输入配对码", "Enter pairing code"), () => { });
        show.Style = enter.Style = (Style)FindResource("SettingsOutlineButton");
        enter.Margin = new Thickness(8, 0, 0, 0);
        modes.Children.Add(show);
        modes.Children.Add(enter);
        panel.Children.Add(modes);
        var body = new StackPanel();
        panel.Children.Add(body);
        var note = SyncDialogNote(Loc.T("在一台电脑上显示配对码，在另一台上输入。", "Show a pairing code on one PC and enter it on the other."));
        body.Children.Add(note);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
        var dialogToken = lifetime.Token;
        dialog.Closed += (_, _) => lifetime.Cancel();
        CancellationTokenSource? attempt = null;
        Task active = Task.CompletedTask;
        bool switching = false;

        async Task SelectAsync(bool host)
        {
            if (switching || dialogToken.IsCancellationRequested) return;
            switching = true;
            try
            {
                attempt?.Cancel();
                await active;
                if (dialogToken.IsCancellationRequested) return;
                show.IsEnabled = !host;
                enter.IsEnabled = host;
                active = RunModeAsync(host);
            }
            finally { switching = false; }
        }

        async Task RunModeAsync(bool host)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(dialogToken);
            attempt = stop;
            var token = stop.Token;
            try
            {
                LanClipboardPeer peer = host ? await ShowCodeAsync(token) : await EnterCodeAsync(token);
                token.ThrowIfCancellationRequested();
                _app.ClipboardSync.SetLanPeer(peer);
                dialog.Close();
            }
            catch (Exception ex) when (token.IsCancellationRequested && ex is not OutOfMemoryException) { }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SocketException or AuthenticationException or
                CryptographicException or UnauthorizedAccessException or NetworkInformationException or OperationCanceledException)
            {
                body.Children.Clear();
                body.Children.Add(SyncDialogNote(ex switch
                {
                    LanPairingDeclinedException => Loc.T("配对已拒绝。", "Pairing was declined."),
                    LanPairingRateLimitedException => Loc.T("尝试次数过多，请稍后再试。", "Too many attempts. Try again later."),
                    LanNetworkChangedException or LanPeerOffSubnetException => Loc.T("网络已变更或设备不在同一子网。请重新搜索后再试。", "The network changed or the devices are on different subnets. Search again."),
                    OperationCanceledException => Loc.T("等待超时，可重新开始配对。", "Pairing timed out. Start again."),
                    _ => Loc.T("配对未完成，请检查设备和配对码后重试。", "Pairing did not finish. Check the device and pairing code and try again.")
                }));
                show.IsEnabled = enter.IsEnabled = true;
            }
            finally
            {
                attempt = null;
                if (!dialogToken.IsCancellationRequested) show.IsEnabled = enter.IsEnabled = true;
            }
        }

        async Task<LanClipboardPeer> ShowCodeAsync(CancellationToken token)
        {
            body.Children.Clear();
            body.Children.Add(SyncDialogNote(Loc.T("正在准备局域网配对…", "Preparing local network pairing…")));
            using var host = await Task.Run(() => new LanClipboardLink.PairingHost(_app.ClipboardSync.Accounts.DeviceName), token);
            token.ThrowIfCancellationRequested();
            body.Children.Clear();
            body.Children.Add(SyncDialogNote(Loc.T("请在另一台电脑选择此设备，并输入下方六位配对码。两分钟内有效。", "Select this PC on the other device and enter the six-digit pairing code. It expires in two minutes.")));
            body.Children.Add(SyncLargeCode(host.Code));
            return await host.AcceptAsync(ConfirmAsync, token);
        }

        async Task<LanClipboardPeer> EnterCodeAsync(CancellationToken token)
        {
            body.Children.Clear();
            var description = SyncDialogNote(Loc.T("先在另一台电脑选择“显示配对码”。", "First, choose Show pairing code on the other PC."));
            var devices = new ComboBox { DisplayMemberPath = nameof(LanPairingDevice.Label), IsEnabled = false };
            AutomationProperties.SetName(devices, Loc.T("局域网设备", "Local network device"));
            var code = new TextBox
            {
                Style = (Style)FindResource("SettingsField"),
                MaxLength = 6, FontSize = 22, Height = 52, Width = 190,
                TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0),
                FontFamily = new FontFamily("Consolas")
            };
            AutomationProperties.SetName(code, Loc.T("六位配对码", "Six-digit pairing code"));
            code.PreviewTextInput += (_, e) => e.Handled = e.Text.Any(c => !char.IsAsciiDigit(c));
            var search = ActionButton(Loc.T("重新搜索", "Search again"), () => { });
            search.Style = (Style)FindResource("SettingsOutlineButton");
            var connect = ActionButton(Loc.T("继续", "Continue"), () => { }, true);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            connect.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(search);
            actions.Children.Add(connect);
            body.Children.Add(description);
            body.Children.Add(devices);
            body.Children.Add(code);
            body.Children.Add(actions);
            var selected = new TaskCompletionSource<(LanPairingDevice Device, string Code)>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool busy = false;
            void EnableConnect() => connect.IsEnabled = !busy && devices.SelectedItem is LanPairingDevice && LanClipboardLink.IsPairingCode(code.Text);
            devices.SelectionChanged += (_, _) => EnableConnect();
            code.TextChanged += (_, _) => EnableConnect();
            connect.Click += (_, _) =>
            {
                if (devices.SelectedItem is LanPairingDevice device && LanClipboardLink.IsPairingCode(code.Text))
                    selected.TrySetResult((device, code.Text));
            };
            async Task FindAsync()
            {
                if (busy || token.IsCancellationRequested || selected.Task.IsCompleted) return;
                busy = true;
                search.IsEnabled = devices.IsEnabled = false;
                EnableConnect();
                description.Text = Loc.T("正在搜索同一局域网内的设备…", "Searching for devices on the same local network…");
                try
                {
                    var found = await LanClipboardLink.DiscoverPairingDevicesAsync(token);
                    token.ThrowIfCancellationRequested();
                    devices.ItemsSource = found;
                    devices.SelectedIndex = found.Count == 1 ? 0 : -1;
                    devices.IsEnabled = found.Count > 0;
                    description.Text = found.Count == 0
                        ? Loc.T("未找到设备。确认两台电脑在同一局域网，且对端正在显示配对码。", "No devices found. Check that both PCs are on the same network and the other PC is showing its pairing code.")
                        : Loc.T("选择对端设备，输入其六位配对码。", "Select the other PC and enter its six-digit pairing code.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) when (ex is SocketException or NetworkInformationException or IOException)
                { description.Text = Loc.T("搜索失败，可以重新搜索。", "Search failed. You can search again."); }
                finally
                {
                    busy = false;
                    if (!token.IsCancellationRequested) { search.IsEnabled = true; EnableConnect(); }
                }
            }
            search.Click += async (_, _) => await FindAsync();
            EnableConnect();
            await FindAsync();
            var choice = await selected.Task.WaitAsync(token);
            body.Children.Clear();
            body.Children.Add(SyncDialogNote(Loc.T("正在验证配对码…", "Verifying the pairing code…")));
            return await LanClipboardLink.JoinPairingAsync(choice.Device, choice.Code, _app.ClipboardSync.Accounts.DeviceName, ConfirmAsync, token);
        }

        async Task<bool> ConfirmAsync(LanPairingConfirmation confirmation, CancellationToken token)
        {
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Dispatcher.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                body.Children.Clear();
                body.Children.Add(TextLine(confirmation.DeviceName, 16, TextPrimary, FontWeights.SemiBold));
                body.Children.Add(SyncDialogNote(Loc.T("核对两台电脑显示的核对串。只有完全相同才确认。", "Compare the verification string on both PCs. Confirm only if it matches exactly.")));
                body.Children.Add(SyncLargeCode(confirmation.Verification));
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                var accept = ActionButton(Loc.T("相同，确认配对", "Matches, confirm pairing"), () =>
                {
                    buttons.IsEnabled = false;
                    body.Children.Add(SyncDialogNote(Loc.T("已确认，等待另一台电脑确认…", "Confirmed. Waiting for the other PC…")));
                    decision.TrySetResult(true);
                }, true);
                var reject = ActionButton(Loc.T("不相同", "Does not match"), () => decision.TrySetResult(false));
                reject.Style = (Style)FindResource("SettingsOutlineButton");
                reject.Margin = new Thickness(8, 0, 0, 0);
                buttons.Children.Add(accept);
                buttons.Children.Add(reject);
                body.Children.Add(buttons);
            });
            return await decision.Task.WaitAsync(token).ConfigureAwait(false);
        }

        show.Click += async (_, _) => await SelectAsync(true);
        enter.Click += async (_, _) => await SelectAsync(false);
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
