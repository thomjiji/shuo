using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Shuo;

public sealed class OverlayDragHandle : UserControl
{
    public OverlayDragHandle()
    {
        IsTabStop = false;
        Content = new Border { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    }
    public InputSystemCursorShape CursorShape
    {
        get;
        set
        {
            field = value;
            ProtectedCursor = InputSystemCursor.Create(value);
        }
    }
}
