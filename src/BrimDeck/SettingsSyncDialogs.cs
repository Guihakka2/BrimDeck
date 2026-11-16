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
    private Window SyncDialog(string title, StackPanel panel, double width = 460)
    {
        var dialog = new Window { Owner = this, Title = title, Width = width, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, FontFamily = SettingsFont(UiFontRole.Panel), Background = UI.Brush(Surface), Foreground = UI.Brush(TextPrimary) };
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/BrimDeck;component/SettingsResources.xaml", UriKind.Relative) });
        _palette.Apply(dialog.Resources); Native.SettingsChrome.Apply(dialog);
        dialog.SourceInitialized += (_, _) => Native.SettingsChrome.SetTheme(dialog, _palette.Dark);
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Display);
        panel.Margin = new Thickness(28); dialog.Content = panel; return dialog;
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
    private void ShowWeTypePairing()
    {
        var service = _app.ClipboardSync;
        service.Accounts.WeType.DeviceName = service.Accounts.DeviceName;
        var panel = new StackPanel(); var dialog = SyncDialog(Loc.T("连接微信输入法", "Connect WeType"), panel, 480);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_app.Lifetime.Token);
        var work = new StackPanel(); bool busy = false;
        panel.Children.Add(TextLine(Loc.T("连接微信输入法", "Connect WeType"), 20, TextPrimary, FontWeights.SemiBold));
        var device = TextLine(service.Accounts.DeviceName, 12, TextSecondary); device.Margin = new Thickness(0, 8, 0, 22); panel.Children.Add(device);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 22) };
        var join = ActionButton(Loc.T("输入配对码", "Enter code"), () => { });
        var issue = ActionButton(Loc.T("显示本机配对码", "Show this device's code"), () => { });
        join.Style = issue.Style = (Style)FindResource("SettingsOutlineButton"); issue.Margin = new Thickness(8, 0, 0, 0);
        modes.Children.Add(join); modes.Children.Add(issue); panel.Children.Add(modes); panel.Children.Add(work);
        dialog.Closed += (_, _) => stop.Cancel();
        void Note(string value) { var n = TextLine(value, 12, TextSecondary); n.TextWrapping = TextWrapping.Wrap; n.Margin = new Thickness(0, 0, 0, 18); work.Children.Add(n); }
        void InputMode()
        {
            if (busy) return; work.Children.Clear();
            Note(Loc.T("在手机微信输入法中打开跨设备功能，将显示的六位配对码输入下方。", "Open cross-device settings in WeType on your phone and enter its six-digit code."));
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
                digit.TextChanged += (_, _) => { submit.IsEnabled = !busy && digits.All(d => d is not null && d.Text.Length == 1 && char.IsAsciiDigit(d.Text[0])); if (digit.Text.Length == 1 && at < 5) digits[at + 1]?.Focus(); };
                digit.GotKeyboardFocus += (_, _) => digit.SelectAll();
                digit.PreviewKeyDown += (_, e) => { if (e.Key == Key.Back && digit.Text.Length == 0 && at > 0) { digits[at - 1].Focus(); digits[at - 1].SelectAll(); e.Handled = true; } };
                DataObject.AddPastingHandler(digit, (_, e) => { e.CancelCommand(); if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted) return; pasted = pasted.Trim(); if (pasted.Length != 6 || pasted.Any(c => c is < '0' or > '9')) return; for (int j = 0; j < 6; j++) digits[j].Text = pasted[j].ToString(); digits[5].Focus(); });
            }
            work.Children.Add(cells); work.Children.Add(status); work.Children.Add(submit);
            submit.Click += async (_, _) =>
            {
                busy = true; join.IsEnabled = issue.IsEnabled = submit.IsEnabled = cells.IsEnabled = false;
                status.Text = Loc.T("请在手机上确认连接…", "Confirm the connection on your phone…");
                try { await service.PairWeTypeAsync(string.Concat(digits.Select(d => d.Text)), stop.Token); if (!stop.IsCancellationRequested) dialog.Close(); }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = Loc.T("配对失败，请检查配对码后重试。", "Pairing failed. Check the code and retry."); }
                finally { busy = false; join.IsEnabled = issue.IsEnabled = submit.IsEnabled = cells.IsEnabled = true; }
            };
            digits[0].Focus();
        }
        join.Click += (_, _) => InputMode();
        issue.Click += async (_, _) =>
        {
            if (busy) return; busy = true; join.IsEnabled = issue.IsEnabled = false; work.Children.Clear();
            Note(Loc.T("在另一台设备的微信输入法中输入此配对码。", "Enter this code in WeType on another device."));
            var codeLabel = TextLine("···", 32, TextPrimary, FontWeights.SemiBold); codeLabel.HorizontalAlignment = HorizontalAlignment.Center; work.Children.Add(codeLabel);
            var status = TextLine(Loc.T("正在获取配对码…", "Requesting code…"), 12, TextSecondary); status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(0, 18, 0, 12); work.Children.Add(status);
            using var client = new WeTypeSyncClient(service.Accounts.WeType, service.Save); string? code = null;
            try
            {
                code = await client.CreatePairingCodeAsync(stop.Token); codeLabel.Text = string.Join("  ", code.ToCharArray());
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (DateTime.UtcNow < deadline)
                {
                    status.Text = Loc.T("等待设备添加 · 剩余 ", "Waiting for device · ") + Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalSeconds) + Loc.T(" 秒", " s");
                    var peer = await client.PollPairingCodeAsync(code, stop.Token);
                    if (peer is not null)
                    {
                        status.Text = Loc.T("请求连接的设备：", "Connection requested by: ") + peer.Value.Name;
                        var confirm = ActionButton(Loc.T("确认连接", "Confirm connection"), () => { }, true); work.Children.Add(confirm);
                        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        confirm.Click += (_, _) => { confirm.IsEnabled = false; accepted.TrySetResult(true); };
                        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); expiry.CancelAfter(deadline - DateTime.UtcNow > TimeSpan.Zero ? deadline - DateTime.UtcNow : TimeSpan.FromMilliseconds(1));
                        await accepted.Task.WaitAsync(expiry.Token);
                        await client.ConfirmPairingAsync(peer.Value.BindId, peer.Value.Name, stop.Token);
                        if (!stop.IsCancellationRequested) dialog.Close(); return;
                    }
                    await Task.Delay(1500, stop.Token);
                }
                status.Text = Loc.T("配对码已过期，请重新获取。", "Code expired. Request a new code.");
            }
            catch (OperationCanceledException) { if (!stop.IsCancellationRequested) status.Text = Loc.T("配对码已过期，请重新获取。", "Code expired. Request a new code."); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { status.Text = Loc.T("未能完成配对，请重试。", "Pairing could not be completed. Retry."); }
            finally
            {
                if (code is not null && !service.Connected(false)) { using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)); try { await client.ReleasePairingCodeAsync(code, cleanup.Token); } catch { } }
                busy = false; join.IsEnabled = issue.IsEnabled = true;
            }
        };
        InputMode(); dialog.ShowDialog();
    }
}
