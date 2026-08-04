using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace AES_Controls.Helpers;

/// <summary>
/// Ensures only one <see cref="ContextMenu"/> is open at a time when menus are
/// opened programmatically (composition controls bypass Avalonia's light-dismiss).
/// </summary>
public static class ContextMenuHelper
{
    public static void OpenExclusive(ContextMenu menu, Control placementTarget)
    {
        CloseOpenContextMenus(placementTarget, except: menu);
        menu.Open(placementTarget);
    }

    public static void CloseOpenContextMenus(Visual? from, ContextMenu? except = null)
    {
        var root = TopLevel.GetTopLevel(from) as Visual ?? from;
        if (root == null)
            return;

        CloseOpenMenusInTree(root, except);
    }

    private static void CloseOpenMenusInTree(Visual visual, ContextMenu? except)
    {
        if (visual is Control { ContextMenu: { IsOpen: true } menu } &&
            !ReferenceEquals(menu, except))
        {
            menu.Close();
        }

        foreach (var child in visual.GetVisualChildren())
            CloseOpenMenusInTree(child, except);
    }
}
