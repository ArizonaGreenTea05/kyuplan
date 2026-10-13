using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kyuplan.Client.ViewModels;
using Kyuplan.Contracts;

namespace Kyuplan.Client.WpfApp.Controls;

public partial class MemberCommentsView : UserControl
{
    public MemberCommentsView() => InitializeComponent();

    private MemberCommentsViewModel? ViewModel => DataContext as MemberCommentsViewModel;

    /// <summary>
    /// A style button: the view model puts the markers around what is selected in the box (or takes them off), and the same words are
    /// selected again, so that the next button works on them too.
    /// </summary>
    private void OnFormat(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || sender is not FrameworkElement { Tag: CommentStyle style }) return;

        var change = viewModel.ApplyFormat(style, Box.SelectionStart, Box.SelectionLength);
        Box.Focus();
        Box.Select(change.SelectionStart, change.SelectionLength);
    }

    /// <summary>Ctrl+Enter sends the comment (Enter alone is a new line).</summary>
    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;

        e.Handled = true;
        if (ViewModel?.SaveCommand.CanExecute(null) == true) ViewModel.SaveCommand.Execute(null);
    }
}
