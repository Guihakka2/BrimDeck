using System.IO;
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
    private void ShowSyncMarkerTest(bool doubao)
    {
        var panel = new StackPanel();
        var dialog = SyncDialog(Loc.T("测试同步标记", "Test sync marker"), panel);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
        var token = stop.Token;
        dialog.Closed += (_, _) => stop.Cancel();
        var note = TextLine(Loc.T("请输入双方约定的 BrimDeck-Sync-Test- 标记，后接 32 位 GUID。接收结果只显示是否匹配。", "Enter the agreed BrimDeck-Sync-Test- marker followed by a 32-character GUID. The receive result only reports a match."), 12, TextSecondary);
        note.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(note);
        var input = new TextBox { Style = (Style)FindResource("SettingsField"), MaxLength = 51 };
        AutomationProperties.SetName(input, Loc.T("测试标记", "Test marker"));
        panel.Children.Add(input);
        var result = TextLine("", 12, TextSecondary);
        result.TextWrapping = TextWrapping.Wrap;
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (bool send in new[] { true, false })
        {
            var button = ActionButton(send ? Loc.T("发送标记", "Send marker") : Loc.T("检查接收", "Check received marker"), () => { });
            button.Click += async (_, _) =>
            {
                actions.IsEnabled = false;
                try
                {
                    bool matched = await _app.ClipboardSync.TestMarkerAsync(doubao, input.Text, send, token);
                    if (token.IsCancellationRequested) return;
                    result.Text = send ? Loc.T("服务端已确认发送请求。", "The server acknowledged the send request.")
                        : matched ? Loc.T("已收到匹配的标记。", "Received a matching marker.")
                        : Loc.T("最新内容与标记不匹配。", "The latest content did not match the marker.");
                }
                catch (Exception ex) when (token.IsCancellationRequested && ex is not OutOfMemoryException) { }
                catch (ArgumentException) { result.Text = Loc.T("测试标记格式无效。", "Invalid marker format."); }
                catch (DoubaoDeviceUnavailableException ex)
                {
                    result.Text = ClipboardSyncService.DoubaoUnavailableMessage + $" (sync_enabled={ex.SyncState}, is_support_copy={ex.SupportsCopy})";
                }
                catch (DoubaoRequestException ex) { result.Text = Loc.T("豆包请求错误码 ", "Doubao error code ") + ex.Code; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { result.Text = Loc.T("测试请求失败。", "Test request failed."); }
                finally { if (!token.IsCancellationRequested) actions.IsEnabled = true; }
            };
            actions.Children.Add(button);
        }
        panel.Children.Add(actions);
        panel.Children.Add(result);
        dialog.ShowDialog();
    }

    private Window SyncDialog(string title, StackPanel panel, double width = 460)
    {
        var dialog = new Window { Owner = this, Title = title, Width = width, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, FontFamily = SettingsFont(UiFontRole.Panel), Background = UI.Brush(Surface), Foreground = UI.Brush(TextPrimary) };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BrimDeck;component/SettingsResources.xaml", UriKind.Relative) });
        _palette.Apply(dialog.Resources); Native.SettingsChrome.Apply(dialog);
        dialog.SourceInitialized += (_, _) => Native.SettingsChrome.SetTheme(dialog, _palette.Dark);
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);
        var layout = new DockPanel { Margin = new Thickness(28) };
        var close = ActionButton(Loc.T("关闭", "Close"), dialog.Close);
        close.Style = (Style)FindResource("SettingsOutlineButton");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 16, 0, 0);
        DockPanel.SetDock(close, Dock.Bottom);
        layout.Children.Add(close);
        layout.Children.Add(panel);
        dialog.Content = layout;
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            dialog.Close();
        };
        return dialog;
    }
    private void EditSyncDeviceName()
    {
        var panel = new StackPanel(); var dialog = SyncDialog(Loc.T("本机设备名称", "Device name"), panel);
        panel.Children.Add(TextLine(Loc.T("本机设备名称", "Device name"), 18, TextPrimary, FontWeights.SemiBold));
        var note = TextLine(Loc.T("用于新连接的设备识别。已连接设备的在线改名规则仍在核实。", "Identifies this device for new connections. Renaming connected devices is still being verified."), 12, TextSecondary);
        note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 12, 0, 18); panel.Children.Add(note);
        var input = new TextBox { Style = (Style)FindResource("SettingsField"), Text = _app.ClipboardSync.Accounts.DeviceName, MaxLength = 48 };
        AutomationProperties.SetName(input, Loc.T("本机设备名称", "Device name")); panel.Children.Add(input);
        bool connected = _app.ClipboardSync.Connected(false) || _app.ClipboardSync.Connected(true);
        input.IsEnabled = !connected;
        if (connected) { var hint = TextLine(Loc.T("当前已有连接，暂不修改已连接设备的名称。", "This device is connected. Renaming is not yet available."), 12, TextSecondary); hint.Margin = new Thickness(0, 12, 0, 0); hint.TextWrapping = TextWrapping.Wrap; panel.Children.Add(hint); }
        var save = ActionButton(Loc.T("保存", "Save"), () => { _app.ClipboardSync.RenameDevice(input.Text); dialog.Close(); ShowPage(ClipboardSyncSettingsPage); }, true);
        save.IsEnabled = !connected; save.HorizontalAlignment = HorizontalAlignment.Right; save.Margin = new Thickness(0, 20, 0, 0);
        input.TextChanged += (_, _) => save.IsEnabled = !connected && input.Text.Trim().Length > 0 && !input.Text.Any(char.IsControl);
        panel.Children.Add(save); dialog.ShowDialog();
    }
    private async Task ShowDoubaoLoginAsync(Func<Action<string>, CancellationToken, Task> signIn)
    {
        var panel = new StackPanel();
        var dialog = SyncDialog(Loc.T("登录豆包", "Sign in to Doubao"), panel);
        var status = TextLine("", 12, TextSecondary);
        status.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(status);
        var retry = ActionButton(Loc.T("重试登录", "Try again"), () => { }, true);
        retry.Margin = new Thickness(0, 16, 0, 0);
        retry.Visibility = Visibility.Collapsed;
        panel.Children.Add(retry);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
        var token = stop.Token;
        Task active = Task.CompletedTask;
        dialog.Closed += (_, _) => stop.Cancel();

        async Task SignInAsync()
        {
            retry.Visibility = Visibility.Collapsed;
            status.Text = Loc.T("正在打开浏览器登录窗口…", "Opening the browser sign-in window…");
            try
            {
                await signIn(message => { if (!token.IsCancellationRequested) status.Text = message; }, token);
                if (!token.IsCancellationRequested) dialog.Close();
            }
            catch (Exception ex) when (token.IsCancellationRequested && ex is not OutOfMemoryException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                status.Text = ex is Native.DoubaoSignInWindowClosedException or InvalidDataException or TimeoutException or InvalidOperationException
                    ? ex.Message
                    : Loc.T("登录连接失败，请检查网络后重试。", "Sign-in failed. Check the network and retry.");
                retry.Visibility = Visibility.Visible;
            }
        }

        void StartSignIn()
        {
            if (active.IsCompleted && !token.IsCancellationRequested) active = SignInAsync();
        }
        dialog.ContentRendered += (_, _) => StartSignIn();
        retry.Click += (_, _) => StartSignIn();
        dialog.ShowDialog();
        await active;
    }

    private async Task ShowWeTypePairingAsync()
    {
        var service = _app.ClipboardSync;
        service.Accounts.WeType.DeviceName = service.Accounts.DeviceName;
        var panel = new StackPanel(); var dialog = SyncDialog(Loc.T("连接微信输入法", "Connect WeType"), panel, 480);
        var work = new StackPanel();
        CancellationTokenSource? attempt = null;
        Task active = Task.CompletedTask;
        bool closed = false, switching = false;
        panel.Children.Add(TextLine(Loc.T("连接微信输入法", "Connect WeType"), 20, TextPrimary, FontWeights.SemiBold));
        var device = TextLine(service.Accounts.DeviceName, 12, TextSecondary); device.Margin = new Thickness(0, 8, 0, 22); panel.Children.Add(device);
        var notice = TextLine(Loc.T(
            "配对成功后，微信输入法可能自动开启个人词库、常用语、AI 录音笔和剪贴板同步。请在微信输入法中检查并关闭不需要的项目。",
            "After pairing, WeType may enable sync for the personal dictionary, phrases, AI voice recorder, and clipboard. Review these options in WeType and turn off any you do not want."), 12, TextSecondary);
        notice.TextWrapping = TextWrapping.Wrap;
        notice.Margin = new Thickness(0, 0, 0, 16);
        panel.Children.Add(notice);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 22) };
        var join = ActionButton(Loc.T("输入配对码", "Enter code"), () => { });
        var issue = ActionButton(Loc.T("显示本机配对码", "Show this device's code"), () => { });
        join.Style = issue.Style = (Style)FindResource("SettingsOutlineButton"); issue.Margin = new Thickness(8, 0, 0, 0);
        modes.Children.Add(join); modes.Children.Add(issue); panel.Children.Add(modes); panel.Children.Add(work);
        dialog.Closed += (_, _) =>
        {
            closed = true;
            attempt?.Cancel();
        };
        async Task RunAttemptAsync(Func<CancellationToken, Task> run)
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
            attempt = stop;
            try { await run(stop.Token); }
            finally { attempt = null; }
        }
        async Task SwitchModeAsync(bool showCode)
        {
            if (closed || switching) return;
            switching = true;
            join.IsEnabled = issue.IsEnabled = false;
            try
            {
                if (attempt is not null)
                {
                    work.Children.Clear();
                    Note(Loc.T("正在取消配对…", "Cancelling pairing…"));
                    attempt.Cancel();
                    await active;
                }
                if (closed || service.Connected(false)) return;
                if (!showCode) InputMode();
                else
                {
                    join.IsEnabled = true;
                    active = RunAttemptAsync(ShowCodeAsync);
                }
            }
            finally
            {
                switching = false;
                if (!closed)
                {
                    join.IsEnabled = true;
                    issue.IsEnabled = attempt is null || !showCode;
                }
            }
        }
        void Note(string value) { var n = TextLine(value, 12, TextSecondary); n.TextWrapping = TextWrapping.Wrap; n.Margin = new Thickness(0, 0, 0, 18); work.Children.Add(n); }
        void InputMode()
        {
            work.Children.Clear();
            Note(Loc.T("打开另一台设备的微信输入法，输入它显示的六位配对码。", "Enter the six-digit code shown in WeType on the other device."));
            var digits = new TextBox[6]; var cells = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var status = TextLine("", 12, TextSecondary); status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 18, 0, 0);
            var submit = ActionButton(Loc.T("配对设备", "Pair device"), () => { }, true); submit.IsEnabled = false;
            submit.HorizontalAlignment = HorizontalAlignment.Stretch; submit.Margin = new Thickness(0, 20, 0, 0);
            for (int i = 0; i < 6; i++)
            {
                int at = i; var digit = new TextBox { Style = (Style)FindResource("SettingsField"), Width = 48, Height = 56, FontSize = 24,
                    MaxLength = 1, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0), Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0) };
                digits[i] = digit; AutomationProperties.SetName(digit, Loc.T($"配对码第 {i + 1} 位", $"Pairing digit {i + 1}")); cells.Children.Add(digit);
                digit.PreviewTextInput += (_, e) => e.Handled = e.Text.Any(c => c is < '0' or > '9');
                digit.TextChanged += (_, _) => { submit.IsEnabled = attempt is null && digits.All(d => d is not null && d.Text.Length == 1 && char.IsAsciiDigit(d.Text[0])); if (digit.Text.Length == 1 && at < 5) digits[at + 1]?.Focus(); };
                digit.GotKeyboardFocus += (_, _) => digit.SelectAll();
                digit.PreviewKeyDown += (_, e) => { if (e.Key == Key.Back && digit.Text.Length == 0 && at > 0) { digits[at - 1].Focus(); digits[at - 1].SelectAll(); e.Handled = true; } };
                DataObject.AddPastingHandler(digit, (_, e) => { e.CancelCommand(); if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted) return; pasted = pasted.Trim(); if (pasted.Length != 6 || pasted.Any(c => c is < '0' or > '9')) return; for (int j = 0; j < 6; j++) digits[j].Text = pasted[j].ToString(); digits[5].Focus(); });
            }
            work.Children.Add(cells); work.Children.Add(status); work.Children.Add(submit);
            submit.Click += (_, _) =>
            {
                if (attempt is not null || switching) return;
                active = RunAttemptAsync(async token =>
                {
                    submit.IsEnabled = cells.IsEnabled = false;
                    status.Text = Loc.T("请在另一台设备上确认连接…", "Confirm the connection on the other device…");
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(60));
                    try
                    {
                        await service.PairWeTypeAsync(string.Concat(digits.Select(d => d.Text)), deadline.Token);
                        if (!closed) dialog.Close();
                    }
                    catch (Exception ex) when (token.IsCancellationRequested && ex is not OutOfMemoryException) { }
                    catch (OperationCanceledException)
                    {
                        status.Text = Loc.T("配对等待超时，请重试。", "Pairing timed out. Try again.");
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        status.Text = Loc.T("配对失败，请检查配对码后重试。", "Pairing failed. Check the code and retry.");
                    }
                    finally
                    {
                        if (!closed && !switching) submit.IsEnabled = cells.IsEnabled = true;
                    }
                });
            };
            digits[0].Focus();
        }
        join.Click += async (_, _) => await SwitchModeAsync(false);
        issue.Click += async (_, _) => await SwitchModeAsync(true);
        async Task ShowCodeAsync(CancellationToken token)
        {
            work.Children.Clear();
            Note(Loc.T("在另一台设备的微信输入法中输入此配对码。", "Enter this code in WeType on another device."));
            var codeLabel = TextLine("···", 32, TextPrimary, FontWeights.SemiBold); codeLabel.HorizontalAlignment = HorizontalAlignment.Center; work.Children.Add(codeLabel);
            var status = TextLine(Loc.T("正在获取配对码…", "Requesting code…"), 12, TextSecondary); status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 18, 0, 12); work.Children.Add(status);
            using var client = new WeTypeSyncClient(service.Accounts.WeType, service.Save);
            string? code = null;
            Button? confirm = null;
            using var expiry = CancellationTokenSource.CreateLinkedTokenSource(token);
            expiry.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                code = await client.CreatePairingCodeAsync(expiry.Token);
                token.ThrowIfCancellationRequested();
                codeLabel.Text = string.Join("  ", code.ToCharArray());
                var deadline = DateTime.UtcNow.AddSeconds(60);
                expiry.CancelAfter(TimeSpan.FromSeconds(60));
                while (DateTime.UtcNow < deadline)
                {
                    status.Text = Loc.T("等待设备添加 · 剩余 ", "Waiting for device · ") + Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalSeconds) + Loc.T(" 秒", " s");
                    var peer = await client.PollPairingCodeAsync(code, expiry.Token);
                    expiry.Token.ThrowIfCancellationRequested();
                    if (peer is not null)
                    {
                        status.Text = Loc.T("请求连接的设备：", "Connection requested by: ") + peer.Value.Name;
                        confirm = ActionButton(Loc.T("确认连接", "Confirm connection"), () => { }, true); work.Children.Add(confirm);
                        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        confirm.Click += (_, _) => { confirm.IsEnabled = false; accepted.TrySetResult(true); };
                        await accepted.Task.WaitAsync(expiry.Token);
                        expiry.Token.ThrowIfCancellationRequested();
                        status.Text = Loc.T("正在确认连接…", "Confirming connection…");
                        await client.ConfirmPairingAsync(peer.Value.BindId, peer.Value.Name, expiry.Token);
                        if (!closed) dialog.Close();
                        return;
                    }
                    await Task.Delay(1500, expiry.Token);
                }
                status.Text = Loc.T("配对码已过期，请重新获取。", "Code expired. Request a new code.");
            }
            catch (Exception ex) when (token.IsCancellationRequested && ex is not OutOfMemoryException) { }
            catch (OperationCanceledException)
            {
                status.Text = code is null
                    ? Loc.T("获取配对码超时，请重试。", "Requesting a code timed out. Try again.")
                    : Loc.T("配对码已过期，请重新获取。", "Code expired. Request a new code.");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = Loc.T("未能完成配对，请重试。", "Pairing could not be completed. Retry."); }
            finally
            {
                if (confirm is not null) work.Children.Remove(confirm);
                codeLabel.Visibility = Visibility.Collapsed;
                if (code is not null && !service.Connected(false))
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await client.ReleasePairingCodeAsync(code, cleanup.Token); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { }
                }
                if (!closed && !switching) issue.IsEnabled = true;
            }
        }
        InputMode();
        dialog.ShowDialog();
        await active;
    }
}
