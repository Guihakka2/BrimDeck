using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using BrimDeck.Core;
using BrimDeck.Core.ClipboardSync;

namespace BrimDeck;

public sealed partial class SettingsWindow
{
    internal const int ClipboardSyncSettingsPage = 18;
    private bool _syncWatching;
    private bool _clipboardConnectorOpen;
    private TextBlock? _weTypeSyncStatus, _doubaoSyncStatus, _weTypeSyncName;
    private System.Windows.Threading.DispatcherTimer? _syncNameTimer;
    private bool _syncNameRefreshing;
    private void SyncNote(string text)
    {
        var note = TextLine(text, 12, TextSecondary); note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 0, 0, 20); Target.Children.Add(note);
    }
    private void ClipboardSyncSettings()
    {
        if (!_syncWatching)
        {
            _syncWatching = true; _app.ClipboardSync.Changed += RefreshSyncStatus;
            Closed += (_, _) => { _app.ClipboardSync.Changed -= RefreshSyncStatus; _syncNameTimer?.Stop(); };
        }
        SyncNote(Loc.T("与局域网内的 BrimDeck 设备或手机输入法同步复制的文字。", "Sync copied text with BrimDeck on your local network or with a phone keyboard."));
        var name = TextLine(_app.ClipboardSync.Accounts.DeviceName, 12, TextSecondary);
        var rename = ActionButton(Loc.T("修改名称", "Rename"), EditSyncDeviceName);
        var nameRow = Row(Loc.T("本机名称", "This device"), rename, name);
        nameRow.Margin = new Thickness(0, 0, 0, 18); Target.Children.Add(nameRow);
        AddLanSync(); AddSyncProvider(true); AddSyncProvider(false); RefreshSyncStatus();
        _syncNameTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(15), System.Windows.Threading.DispatcherPriority.Background, async (_, _) => await RefreshWeTypeNameAsync(), Dispatcher);
        _ = RefreshWeTypeNameAsync();
        SyncNote(Loc.T("仅同步新复制的文字，单条最多 256 KB。图片、文件和历史记录暂不支持。", "Newly copied text only, up to 256 KB per item. Pictures, files and history are not supported."));
    }
    private void AddSyncProvider(bool doubao)
    {
        var service = _app.ClipboardSync;
        var panel = new StackPanel();
        var card = new Border { Child = panel, CornerRadius = new CornerRadius(12), Padding = new Thickness(22, 18, 22, 14),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 16) };
        card.SetResourceReference(Border.BackgroundProperty, "SettingsSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        Target.Children.Add(card);
        var heading = TextLine(doubao ? Loc.T("豆包输入法", "Doubao IME") : Loc.T("微信输入法", "WeType"), 16, TextPrimary, FontWeights.SemiBold);
        panel.Children.Add(heading);
        var description = TextLine(doubao ? Loc.T("使用与手机相同的豆包账号", "Use the same Doubao account as your phone") : Loc.T("通过手机上的配对码连接", "Connect using a pairing code from your phone"), 12, TextSecondary);
        description.Margin = new Thickness(0, 6, 0, 16); description.TextWrapping = TextWrapping.Wrap; panel.Children.Add(description);
        var state = TextLine("", 12, TextSecondary); state.TextWrapping = TextWrapping.Wrap;
        if (doubao) _doubaoSyncStatus = state; else _weTypeSyncStatus = state;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var connect = ActionButton(doubao ? Loc.T("登录豆包", "Sign in to Doubao") : Loc.T("配对设备", "Pair device"), () => { }, primary: true);
        connect.MinWidth = 96; connect.Padding = new Thickness(16, 8, 16, 8);
        AutomationProperties.SetAutomationId(connect, doubao ? "clipboard-sync-doubao-connect" : "clipboard-sync-wetype-connect");
        connect.IsEnabled = !service.Connected(doubao);
        buttons.Children.Add(connect);
        var disconnect = ActionButton(Loc.T("断开连接", "Disconnect"), () =>
        {
            Change(s => { if (doubao) s.DoubaoClipboardSync = false; else s.WeTypeClipboardSync = false; });
            service.Disconnect(doubao); ShowPage(ClipboardSyncSettingsPage);
        });
        disconnect.Style = (Style)FindResource("SettingsOutlineButton");
        disconnect.Padding = new Thickness(16, 8, 16, 8);
        disconnect.Visibility = service.Connected(doubao) ? Visibility.Visible : Visibility.Collapsed;
        connect.Visibility = service.Connected(doubao) ? Visibility.Collapsed : Visibility.Visible;
        buttons.Children.Add(disconnect);
        var connection = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(buttons, Dock.Right); connection.Children.Add(buttons);
        state.VerticalAlignment = VerticalAlignment.Center; connection.Children.Add(state); panel.Children.Add(connection);
        if (doubao)
        {
            connect.Click += async (_, _) => await OpenClipboardConnectorAsync(true);
            var pending = TextLine(Loc.T("在系统浏览器的独立窗口登录，完成后自动连接。", "Sign in in a separate system-browser window. Connection is completed automatically."), 12, TextSecondary);
            pending.TextWrapping = TextWrapping.Wrap; pending.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(pending);
            if (service.Connected(true)) AddDoubaoCapabilityCheck(panel);

        }
        else
        {
            _weTypeSyncName = TextLine(service.Accounts.WeType.DisplayName, 12, TextSecondary);
            _weTypeSyncName.TextWrapping = TextWrapping.Wrap; _weTypeSyncName.Margin = new Thickness(0, 0, 0, 12);
            if (service.Connected(false)) panel.Children.Add(_weTypeSyncName);
            connect.Click += async (_, _) => await OpenClipboardConnectorAsync(false);
        }
        var divider = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 8) };
        divider.SetResourceReference(Border.BackgroundProperty, "SettingsBorder"); panel.Children.Add(divider);
        panel.Children.Add(Toggle(Loc.T("自动同步文字", "Automatically sync text"), doubao ? S.DoubaoClipboardSync : S.WeTypeClipboardSync,
            value => { Change(s => { if (doubao) s.DoubaoClipboardSync = value; else s.WeTypeClipboardSync = value; }); RefreshSyncStatus(); },
            enabled: !service.TestMode && service.Connected(doubao), help: Loc.T("开启后上传新复制的文字，并将收到的文字写入本机剪贴板。不记录的应用和隐私标记同样适用。", "Upload new copies and write received text to this PC's clipboard. Excluded apps and privacy markers apply.")));
        if (service.TestMode)
        {
            var test = ActionButton(Loc.T("测试标记", "Test marker"), () => ShowSyncMarkerTest(doubao));
            test.IsEnabled = service.Connected(doubao);
            panel.Children.Add(test);
        }
    }
    private void AddDoubaoCapabilityCheck(StackPanel panel)
    {
        var check = ActionButton(Loc.T("检查同步权限", "Check sync permission"), () => { });
        check.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(check);
        var result = TextLine("", 12, TextSecondary);
        result.TextWrapping = TextWrapping.Wrap;
        result.Margin = new Thickness(0, 6, 0, 6);
        panel.Children.Add(result);
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            result.Text = Loc.T("正在检查设备同步权限…", "Checking device sync permission…");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
            void Cancel(object? sender, EventArgs args) => stop.Cancel();
            Closed += Cancel;
            try
            {
                var capability = await _app.ClipboardSync.CheckDoubaoCapabilityAsync(stop.Token);
                result.Text = capability.CanSync
                    ? Loc.T("服务端已允许本机同步。", "The server allows this device to sync.")
                    : ClipboardSyncService.DoubaoUnavailableMessage + $" (sync_enabled={capability.SyncState}, is_support_copy={capability.SupportsCopy})";
            }
            catch (OperationCanceledException) { }
            catch (DoubaoRequestException ex)
            { result.Text = Loc.T("权限检查被拒绝，错误码：", "Permission check refused. Error code: ") + ex.Code; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { result.Text = Loc.T("无法检查权限，请稍后重试。", "Could not check permission. Try again."); }
            finally
            {
                Closed -= Cancel;
                check.IsEnabled = _app.ClipboardSync.Connected(true);
            }
        };
    }
    private async Task OpenClipboardConnectorAsync(bool doubao)
    {
        if (_clipboardConnectorOpen) return;
        _clipboardConnectorOpen = true;
        try
        {
            if (doubao)
                await ShowDoubaoLoginAsync((status, token) => Native.DoubaoBrowserLogin.SignInAsync(_app.ClipboardSync, status, token));
            else await ShowWeTypePairingAsync();
        }
        finally
        {
            _clipboardConnectorOpen = false;
            if (IsVisible && _page == ClipboardSyncSettingsPage) ShowPage(_page);
        }
    }
    private async Task RefreshWeTypeNameAsync()
    {
        if (_page != ClipboardSyncSettingsPage || _syncNameRefreshing || !_app.ClipboardSync.Connected(false)) return;
        _syncNameRefreshing = true;
        try { await _app.ClipboardSync.RefreshWeTypeNameAsync(); RefreshSyncStatus(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        finally { _syncNameRefreshing = false; }
    }
    private void RefreshSyncStatus()
    {
        if (_page != ClipboardSyncSettingsPage) return;
        RefreshLanSyncStatus();
        if (_weTypeSyncName is not null) _weTypeSyncName.Text = Loc.T("设备名称：", "Device name: ") + _app.ClipboardSync.Accounts.WeType.DisplayName;
        foreach (var (doubao, text) in new[] { (false, _weTypeSyncStatus), (true, _doubaoSyncStatus) })
        {
            if (text is null) continue;
            bool connected = _app.ClipboardSync.Connected(doubao), enabled = doubao ? S.DoubaoClipboardSync : S.WeTypeClipboardSync;
            if (!connected)
                text.Text = Loc.T("未连接", "Not connected");
            else if (enabled && !_app.ClipboardSync.TestMode)
                text.Text = _app.ClipboardSync.Status(doubao);
            else if (doubao && _app.ClipboardSync.DoubaoCapability is { CanSync: false })
                text.Text = ClipboardSyncService.DoubaoUnavailableMessage;
            else if (doubao && _app.ClipboardSync.DoubaoCapability is null)
                text.Text = Loc.T("已登录 · 尚未检查同步权限", "Signed in · Sync permission not checked");
            else
                text.Text = Loc.T("已连接 · 同步已暂停", "Connected · Sync paused");
        }
    }
}
