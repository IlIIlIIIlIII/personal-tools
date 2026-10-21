#!/usr/bin/env python3
"""Create matched reference/native comparison inputs, not replacement UI images."""
import argparse
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[3]
parser = argparse.ArgumentParser()
parser.add_argument('--actual',default=str(root/'build/linux/shell-scenes-pointer-final'))
parser.add_argument('--output',default=str(root/'build/linux/shell-design-comparison'))
args=parser.parse_args()
output=Path(args.output);output.mkdir(parents=True,exist_ok=True)
for scene in ['02-shortcut','03-ai-search','14-quick-menu','17-ai-error']:
    reference=Image.open(root/f'docs/design/ubuntu-yaru-2026-10-03/images/{scene}.png').convert('RGB').resize((1488,1056),Image.Resampling.LANCZOS)
    actual=Image.open(Path(args.actual)/f'{scene}.png').convert('RGB').resize((1488,1056),Image.Resampling.LANCZOS)
    if scene=='14-quick-menu':
        crops=[('paired',(956,42,1464,1036),(980,34,1488,1028)),('controls',(980,248,1440,464),(1004,240,1464,456))]
    else:
        crops=[('paired',(24,542,1464,1030),(24,544,1464,1032)),('header',(24,542,920,672),(24,544,920,674))]
    for suffix,source_box,native_box in crops:
        left=reference.crop(source_box);right=actual.crop(native_box)
        canvas=Image.new('RGB',(left.width+right.width+24,max(left.height,right.height)+32),'#141414')
        canvas.paste(left,(0,32));canvas.paste(right,(left.width+24,32))
        draw=ImageDraw.Draw(canvas);draw.text((8,8),'APPROVED REFERENCE',fill='white');draw.text((left.width+32,8),'NATIVE GNOME 50',fill='white')
        canvas.save(output/f'{scene}-{suffix}.png')
print(output)
