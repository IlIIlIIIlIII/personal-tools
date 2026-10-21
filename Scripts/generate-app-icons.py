#!/usr/bin/env python3
"""Rebuild platform icons from Resources SVGs; requires Rsvg, pycairo, Pillow."""
from pathlib import Path
import shutil
import gi
import cairo
from PIL import Image

gi.require_version('Rsvg', '2.0')
from gi.repository import Rsvg

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'Resources/PersonalTools.svg'
SYMBOL = ROOT / 'Resources/PersonalTools-symbolic.svg'

def render(size):
    surface = cairo.ImageSurface(cairo.FORMAT_ARGB32, size, size)
    viewport = Rsvg.Rectangle()
    viewport.x = viewport.y = 0
    viewport.width = viewport.height = size
    Rsvg.Handle.new_from_file(str(SOURCE)).render_document(cairo.Context(surface), viewport)
    import io
    stream = io.BytesIO()
    surface.write_to_png(stream)
    stream.seek(0)
    return Image.open(stream).convert('RGBA')

if __name__ == '__main__':
    artwork = render(1024)
    artwork.save(ROOT / 'Resources/PersonalToolsIcon.png')
    artwork.save(ROOT / 'Resources/PersonalTools.icns', format='ICNS')
    windows = ROOT / 'Sources/Windows/WinUI/Assets'
    artwork.save(windows / 'PersonalTools.png')
    artwork.save(windows / 'PersonalTools.ico', format='ICO',
                 sizes=[(s, s) for s in (16, 24, 32, 48, 64, 128, 256)])
    packaging = ROOT / 'Sources/Linux/Packaging'
    shutil.copyfile(SOURCE, packaging / 'io.griplabs.PersonalTools.App.svg')
    shutil.copyfile(SYMBOL, packaging / 'io.griplabs.PersonalTools.App-symbolic.svg')
    for directory in ('Sources/Linux/App/assets', 'Sources/Linux/Shell/icons'):
        destination = ROOT / directory / 'personal-tools-symbolic.svg'
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(SYMBOL, destination)
