"""Encode a small WebP fallback from the already cleaned APNG master."""
import argparse
import json
from pathlib import Path
from PIL import Image
HERE=Path(__file__).resolve().parent
REPO=HERE.parents[3]
parser=argparse.ArgumentParser()
parser.add_argument('--apng',type=Path,default=REPO/'snapshot/punky_loading_clean.apng')
parser.add_argument('--output',type=Path,default=REPO/'snapshot/punky_loading_clean_preview.webp')
args=parser.parse_args()
im=Image.open(args.apng); frames=[];durations=[]
for i in range(im.n_frames):
 im.seek(i);frames.append(im.convert('RGBA').copy());durations.append(int(im.info['duration']))
selection=list(range(0,len(frames),3))
small=[frames[i].resize((192,192),Image.Resampling.LANCZOS) for i in selection]
duration=[sum(durations[i:i+3]) for i in selection]
for quality in (36,32):
 small[0].save(args.output,format='WEBP',save_all=True,append_images=small[1:],duration=duration,loop=0,quality=quality,alpha_quality=85,method=4,minimize_size=False)
 print(f'Small WebP quality {quality}: {args.output.stat().st_size} bytes',flush=True)
 if args.output.stat().st_size<450000:break
metadata={'path':str(args.output),'width':192,'height':192,'frames':len(small),'bytes':args.output.stat().st_size,'fps':8,'duration_ms':sum(duration),'mode':'RGBA','quality':quality,'alpha_quality':85}
(HERE/'preview-webp-metadata.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
