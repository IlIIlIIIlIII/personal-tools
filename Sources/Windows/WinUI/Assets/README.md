# Personal Tools icon

The shared source is `Resources/PersonalTools.svg`: warm-white controls on an
olive tile, using the Ubuntu app's olive, charcoal and warm-white palette.
`Resources/PersonalTools-symbolic.svg` is the simplified 16 px monochrome mark
used by the Linux sidebar, top panel and app menu. Functional clipboard icons
remain distinct from the app identity.

Rebuild PNG, ICO, ICNS and Linux SVG assets from the repository root:

```sh
/usr/bin/python3 Scripts/generate-app-icons.py
```

The generator requires PyGObject/Rsvg, pycairo and Pillow. Windows ICO contains
16, 24, 32, 48, 64, 128 and 256 px images. The PNG is 1024 px with transparent
outer margins. `Scripts/package-windows-icon.ps1` can also repack the PNG on
Windows.
