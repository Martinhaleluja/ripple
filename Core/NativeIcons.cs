using System;

namespace Ripple.Core;

/// <summary>Lets the UI free an icon handle without opening up the rest of Native.</summary>
public static class NativeIcons
{
    public static void Destroy(IntPtr icon) => Native.DestroyIcon(icon);
}
