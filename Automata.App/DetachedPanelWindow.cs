using System.Windows;
using System.Windows.Media;

namespace Automata.App;

/// <summary>
/// The sidebar, living in its own window.
/// <para>
/// It holds the <b>same</b> WebView2 the docked layout holds — reparented, never recreated. A
/// second panel instance would be a second copy of the tree, the selection, the run log and the
/// recorder's state, and the two would disagree the moment either was touched. Everything this
/// window knows about the panel is therefore "where to put it", which is the whole class.
/// </para>
/// <para>
/// Closing it docks rather than destroys: the panel is the only way to drive this app, and a
/// window whose close button loses the entire UI is a trap. <see cref="OnPanelClosing"/> is what
/// the owner uses to intercept that.
/// </para>
/// </summary>
internal sealed class DetachedPanelWindow : Window
{
    public DetachedPanelWindow(UIElement panel, Brush ground)
    {
        Title = "Automata — Build";
        Background = ground;
        Content = panel;
        MinWidth = 340;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    /// <summary>Releases the panel so it can be put back, without disposing it along with this
    /// window. Content must be cleared BEFORE the close, or WPF tears the child down with it.</summary>
    public UIElement? Release()
    {
        var panel = Content as UIElement;
        Content = null;
        return panel;
    }
}
