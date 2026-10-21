#!/usr/bin/env python3
"""Create same-size reference/native comparison evidence, never a substitute UI."""
import argparse,json
from pathlib import Path
from PIL import Image,ImageDraw

root=Path(__file__).resolve().parents[2]
p=argparse.ArgumentParser();p.add_argument('--app',type=Path,required=True);p.add_argument('--shell',type=Path);p.add_argument('--output',type=Path,default=root/'build/linux/design-comparisons');a=p.parse_args()
a.output.mkdir(parents=True,exist_ok=True)
references=root/'docs/design/ubuntu-yaru-2026-10-03/images'
records=[]
for ref in sorted(references.glob('*.png')):
    source=a.app/ref.name
    if not source.exists() and a.shell:source=a.shell/ref.name
    if not source.exists():continue
    original=Image.open(ref).convert('RGB');actual=Image.open(source).convert('RGB')
    normalized=original.resize((1488,1056),Image.Resampling.LANCZOS)
    if actual.size!=(1488,1056):raise ValueError(f'Native capture has wrong size: {source} {actual.size}')
    canvas=Image.new('RGB',(2992,1096),'#151515');canvas.paste(normalized,(0,40));canvas.paste(actual,(1504,40))
    draw=ImageDraw.Draw(canvas);draw.text((12,12),'APPROVED REFERENCE / '+ref.stem,fill='white');draw.text((1516,12),'NATIVE GTK / GNOME CAPTURE',fill='white')
    canvas.save(a.output/ref.name)
    # Top-left typography and major alignment at native pixel resolution.
    crop=(244,80,988,520);focused=Image.new('RGB',(1504,480),'#151515');focused.paste(normalized.crop(crop),(0,40));focused.paste(actual.crop(crop),(760,40));focused.save(a.output/f'{ref.stem}-detail.png')
    records.append({'scene':ref.stem,'reference':str(ref),'referencePixels':original.size,'native':str(source.resolve()),'nativePixels':actual.size,'logicalViewport':[1488,1056],'scale':1,'comparison':str((a.output/ref.name).resolve()),'focus':str((a.output/f'{ref.stem}-detail.png').resolve())})
(a.output/'evidence.json').write_text(json.dumps(records,indent=2)+'\n')
print(f'{len(records)} normalized native/reference comparisons written to {a.output}')
