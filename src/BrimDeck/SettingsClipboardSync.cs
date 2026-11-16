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
        var name = TextLine(_app.ClipboardSync.Accounts.DeviceName, 12, TextSecondary);
        var rename = ActionButton(Loc.T("修改名称", "Rename"), EditSyncDeviceName);
        var nameRow = Row(Loc.T("本机名称", "This device"), rename, name);
        nameRow.Margin = new Thickness(0, 0, 0, 18); Target.Children.Add(nameRow);
        AddLanSync(); AddSyncProvider(true); AddSyncProvider(false); RefreshSyncStatus();
        _syncNameTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(15), System.Windows.Threading.DispatcherPriority.Background, async (_, _) => await RefreshWeTypeNameAsync(), Dispatcher);
        _ = RefreshWeTypeNameAsync();
        SyncNote(Loc.T("同步连接期间新复制的内容。文字单条最多 256 KiB，图片仅支持局域网同步。", "Sync content copied while connected. Text is limited to 256 KiB per item. Images sync over LAN only."));
    }
    private StackPanel AddSyncSection(string title, string detail, FrameworkElement action, out TextBlock status)
    {
        var content = new StackPanel();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        labels.Children.Add(TextLine(title, 16, TextPrimary, FontWeights.SemiBold));
        var description = TextLine(detail, 12, TextSecondary);
        description.TextWrapping = TextWrapping.Wrap;
        description.Margin = new Thickness(0, 4, 0, 0);
        labels.Children.Add(description);
        status = TextLine("", 12, TextSecondary);
        status.Margin = new Thickness(0, 8, 0, 0);
        status.TextWrapping = TextWrapping.Wrap;
        labels.Children.Add(status);
        header.Children.Add(labels);
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 1);
        header.Children.Add(action);
        content.Children.Add(header);
        var card = new Border
        {
            Child = content, CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 16, 18, 16),
            BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12)
        };
        card.SetResourceReference(Border.BackgroundProperty, "SettingsSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "SettingsBorder");
        Target.Children.Add(card);
        return content;
    }

    private static Border SyncDivider()
    {
        var divider = new Border { Height = 1, Margin = new Thickness(0, 12, 0, 8) };
        divider.SetResourceReference(Border.BackgroundProperty, "SettingsBorder");
        return divider;
    }

    private void AddSyncProvider(bool doubao)
    {
        var service = _app.ClipboardSync;
        bool connected = service.Connected(doubao);
        var action = ActionButton(connected ? Loc.T("断开连接", "Disconnect")
            : doubao ? Loc.T("登录豆包", "Sign in to Doubao") : Loc.T("配对设备", "Pair device"), () => { });
        action.Style = (Style)FindResource("SettingsOutlineButton");
        AutomationProperties.SetAutomationId(action, doubao ? "clipboard-sync-doubao-connect" : "clipboard-sync-wetype-connect");
        action.Click += async (_, _) =>
        {
            if (!connected) { await OpenClipboardConnectorAsync(doubao); return; }
            Change(s => { if (doubao) s.DoubaoClipboardSync = false; else s.WeTypeClipboardSync = false; });
            service.Disconnect(doubao);
            ShowPage(ClipboardSyncSettingsPage);
        };
        var panel = AddSyncSection(doubao ? Loc.T("豆包输入法", "Doubao IME") : Loc.T("微信输入法", "WeType"),
            doubao ? Loc.T("在独立浏览器窗口登录豆包账号。", "Sign in to the same Doubao account in a separate browser window.")
                : Loc.T("通过微信输入法的六位配对码连接。", "Connect with a six-digit pairing code from WeType."),
            action, out var status);
        if (doubao) _doubaoSyncStatus = status; else _weTypeSyncStatus = status;
        if (!connected) return;
        panel.Children.Add(SyncDivider());
        if (!doubao)
        {
            _weTypeSyncName = TextLine(service.Accounts.WeType.DisplayName, 12, TextSecondary);
            _weTypeSyncName.TextWrapping = TextWrapping.Wrap;
            panel.Children.Add(_weTypeSyncName);
        }
        panel.Children.Add(Toggle(Loc.T("自动同步文字", "Automatically sync text"), doubao ? S.DoubaoClipboardSync : S.WeTypeClipboardSync,
            value => { Change(s => { if (doubao) s.DoubaoClipboardSync = value; else s.WeTypeClipboardSync = value; }); RefreshSyncStatus(); },
            enabled: !service.TestMode, help: Loc.T("仅同步新复制的文字，沿用剪贴板的排除应用和隐私设置。", "Sync new copied text with your clipboard app exclusions and privacy settings.")));
        if (doubao) AddDoubaoCapabilityCheck(panel);
        if (service.TestMode)
            panel.Children.Add(ActionButton(Loc.T("测试标记", "Test marker"), () => ShowSyncMarkerTest(doubao)));
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
