using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace PersonalTools;

// Base desktop acrylic: Thin let bright windows behind the panel wash dark-theme
// cards to mid-gray and drop caption contrast below 2:1. Retain the default XAML
// configuration so Windows owns theme, activation and accessibility policy.
internal sealed class ClipboardAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? controller;
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        if (!DesktopAcrylicController.IsSupported()) return;
        controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
        controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, root));
        controller.AddSystemBackdropTarget(target);
    }
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        controller?.RemoveSystemBackdropTarget(target);
        controller?.Dispose(); controller = null;
        base.OnTargetDisconnected(target);
    }
}
