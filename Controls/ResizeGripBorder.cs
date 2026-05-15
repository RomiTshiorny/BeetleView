using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace BeetleView.Controls;

// Tiny Grid subclass that shows the horizontal-resize (SizeWestEast) cursor
// when the pointer is over it. WinUI 3 exposes the cursor API via the
// protected UIElement.ProtectedCursor property, so a subclass is the
// standard way to opt a non-Control element into a custom cursor. Border
// is sealed in WinUI 3, hence Grid.
internal sealed class ResizeGripBorder : Grid
{
    public ResizeGripBorder()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
