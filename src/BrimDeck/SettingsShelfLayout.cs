using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using BrimDeck.Core;

namespace BrimDeck;

// "页面布局", first in the clipboard's group ("中转站"): how the clipboard and file transfer page shows its two columns, chosen on
// cards with a diagram of each arrangement (ShelfArrangementPreview). Under the cards stand the settings of the arrangement
// chosen, and only those, as on the collapsed view's page: side by side, how the width is shared between the columns, which
// dragging the divider on the panel also sets; stacked, the further ways of bringing the other card forward
// (MainWindow.ShelfArrange.cs).
public partial class SettingsWindow
{
    internal const int ShelfLayoutSettingsPage = 17;

    private static string ArrangementName(ShelfArrangement arrangement) =>
        arrangement == ShelfArrangement.Split ? Loc.T("并排", "Side by side") : Loc.T("叠放", "Stacked");

    private void ShelfLayoutSettings()
    {
        SectionTitle(Loc.T("排列方式", "Arrangement"));
        // Two cards over the whole row, as the collapsed style's three are, with the same room for the focus ring. Their diagrams
        // are wider than those, so the cards are no taller (ShelfArrangementPreview).
        var cards = new UniformGrid { Columns = 2, Margin = new Thickness(-6, 0, -6, 24) };
        var options = new Grid();
        // The settings of an arrangement: side by side the widths, stacked the ways of switching. Chosen on its card, they take the
        // place of the other's, as the collapsed style's options do.
        void Show(ShelfArrangement arrangement, ShelfArrangement? previous)
        {
            var incoming = Collect(() =>
            {
                if (arrangement == ShelfArrangement.Split)
                    Group(ColumnWidths());
                else
                {
                    SectionTitle(Loc.T("切换方式", "Switching"), help: () => Loc.T(
                        "单击后面一张露出的侧条、前面一张的标题，或再次单击页面切换按钮上的当前页图标，都可以切换。以下三种方式可以另外开启。",
                        "A click on the side strip of the card behind, on the title of the card in front or once more on the page's button switches cards. The three ways below can be turned on as well."));
                    Group(Toggle(Loc.T("指针停在侧条上时切换", "Switch when the pointer rests on the side strip"), S.ShelfHoverSwitch, value => Change(s => s.ShelfHoverSwitch = value)),
                        Toggle(Loc.T("按下鼠标中键切换", "Switch with a middle click"), S.ShelfMiddleSwitch, value => Change(s => s.ShelfMiddleSwitch = value)),
                        Toggle(Loc.T("触控板双指横滑或 Shift + 滚轮切换", "Switch with a two-finger sideways swipe or Shift + wheel"), S.ShelfSwipeSwitch, value => Change(s => s.ShelfSwipeSwitch = value)));
                }
            });
            SwapOptions(options, incoming, previous is { } from && from != arrangement ? (arrangement > from ? 1 : -1) : 0);
        }
        foreach (var arrangement in Enum.GetValues<ShelfArrangement>())
        {
            // The diagrams stand still, whatever the pointer does and whatever the widths are set to: they only say what the
            // arrangement is.
            var card = new RadioButton
            {
                Style = (Style)FindResource("SettingsChoice"), Content = ShelfArrangementPreview.Create(arrangement, _palette),
                Margin = new Thickness(6, 3, 6, 0), Tag = ArrangementName(arrangement), GroupName = "ShelfArrangement", IsChecked = S.ShelfArrangement == arrangement
            };
            AutomationProperties.SetName(card, ArrangementName(arrangement));
            card.Checked += (_, _) =>
            {
                var previous = S.ShelfArrangement;
                if (previous != arrangement) Change(s => s.ShelfArrangement = arrangement);
                Show(arrangement, previous);
            };
            cards.Children.Add(card);
        }
        Target.Children.Add(cards); Target.Children.Add(options);
        Show(S.ShelfArrangement, null);
    }

    // "两栏宽度": a slider that stands for the divider, with the clipboard's name and share at its left and the file transfer's at
    // its right. Moved to the right it widens the clipboard, as the divider does on the panel. The names stand outside the
    // slider, so neither is ever cut, however narrow its column.
    private Slider? _shareSlider;
    private bool _shareFollowing;
    // The divider was dragged on the panel: the slider follows it.
    internal void ShelfShareChanged()
    {
        if (!IsLoaded || _page != ShelfLayoutSettingsPage || _shareSlider is not { IsLoaded: true } slider) return;
        _shareFollowing = true;
        try { slider.Value = Math.Clamp(100 - ShelfLayout.ClampShare(S.ShelfFileShare), slider.Minimum, slider.Maximum); }
        finally { _shareFollowing = false; }
    }
    private FrameworkElement ColumnWidths()
    {
        double least = 100 - ShelfLayout.MaxShare, most = 100 - ShelfLayout.MinShare;
        var slider = new Slider
        {
            Style = (Style)FindResource("SettingsSlider"), Minimum = least, Maximum = most, Value = Math.Clamp(100 - ShelfLayout.ClampShare(S.ShelfFileShare), least, most),
            SmallChange = 1, LargeChange = 5, IsMoveToPointEnabled = true, Width = 190,
            Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center
        };
        TextBlock Number(TextAlignment align)
        {
            var number = TextLine("", 12, TextPrimary, FontWeights.SemiBold);
            number.MinWidth = 32; number.TextAlignment = align; number.VerticalAlignment = VerticalAlignment.Center;
            Typography.SetNumeralAlignment(number, FontNumeralAlignment.Tabular);
            return number;
        }
        TextBlock Name(string text) { var name = TextLine(text, 12, TextSecondary); name.VerticalAlignment = VerticalAlignment.Center; return name; }
        var clipsNumber = Number(TextAlignment.Right); var filesNumber = Number(TextAlignment.Left);
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var part in new UIElement[] { Name(Loc.T("剪贴板", "Clipboard")), clipsNumber, slider, filesNumber, Name(Loc.T("文件中转", "File transfer")) }) line.Children.Add(part);
        void Show()
        {
            double clips = slider.Value;
            clipsNumber.Text = clips.ToString("0", CultureInfo.InvariantCulture) + "%";
            filesNumber.Text = (100 - clips).ToString("0", CultureInfo.InvariantCulture) + "%";
            AutomationProperties.SetName(slider, Loc.T($"两栏宽度：剪贴板 {clips:0}%，文件中转 {100 - clips:0}%", $"Column widths: clipboard {clips:0}%, file transfer {100 - clips:0}%"));
        }
        Show();
        _shareSlider = slider;
        KeepPreview(slider);
        // The panel opens on the page while the widths are set here, as it does for a page's size, so the change is seen. While the
        // thumb is dragged the panel's columns follow it directly, as they follow the divider, and nothing is saved until it is
        // let go: saving applies all the settings again, which is too slow to do for every step of a drag.
        bool dragging = false;
        double Share() => Math.Round(100 - slider.Value, 1);
        slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) =>
        {
            dragging = true;
            if (!DeckPages.Shown(S, DeckPage.Clipboard)) return;
            _preview = true; _app.PreviewDeck.Preview(true); _app.PreviewDeck.SelectPage(DeckPage.Clipboard, remember: false);
        }));
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            dragging = false;
            double share = Share();
            if (Math.Abs(share - S.ShelfFileShare) >= .05) Change(s => s.ShelfFileShare = share);
            _app.PreviewDeck.PreviewShelfShare(null);
        }));
        slider.ValueChanged += (_, _) =>
        {
            Show();
            // Following the panel's divider, the share is already saved: it is not written back.
            if (_shareFollowing) return;
            double share = Share();
            if (dragging) _app.PreviewDeck.PreviewShelfShare(share);
            else if (Math.Abs(share - S.ShelfFileShare) >= .05) Resize(s => s.ShelfFileShare = share, DeckPage.Clipboard);
        };
        return Row(Loc.T("两栏宽度", "Column widths"), line, help: () => Loc.T(
            "在面板上拖动剪贴板与文件中转之间的分隔线，也能调整两栏宽度。双击分隔线，两栏宽度恢复为剪贴板 60%、文件中转 40%。",
            "Dragging the divider between the clipboard and the file transfer on the panel sets the widths as well. A double-click on the divider gives back 60% for the clipboard and 40% for the file transfer."));
    }
}
