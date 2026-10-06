import cv2,json,numpy as np
from pathlib import Path
from PIL import Image,ImageDraw
src=Path(r'D:\mytools\mySQLPunk\snapshot\punky_loading.mp4')
out=Path(r'D:\mytools\mySQLPunk\docs\design\report-export\animation')
cap=cv2.VideoCapture(str(src)); n=int(cap.get(cv2.CAP_PROP_FRAME_COUNT)); fps=cap.get(cv2.CAP_PROP_FPS)
frames=[]
while True:
 ok,bgr=cap.read()
 if not ok:break
 frames.append(cv2.cvtColor(bgr,cv2.COLOR_BGR2RGB))
cap.release()
idx=np.linspace(0,len(frames)-1,6,dtype=int)
canvas=Image.new('RGB',(3*360,2*390),'#343840');d=ImageDraw.Draw(canvas)
stats=[]
for k,i in enumerate(idx):
 a=frames[i];m=a.max(2); border=np.concatenate((a[:10].reshape(-1,3),a[-10:].reshape(-1,3),a[:,:10].reshape(-1,3),a[:,-10:].reshape(-1,3)))
 stats.append({'frame':int(i),'time':round(i/fps,3),'pixel_max_percentiles':np.percentile(m,[0,25,40,50,75,90,99]).tolist(),'black_fraction':{str(t):float((m<=t).mean()) for t in [0,2,4,8,12,16,24,32]},'border_max_percentiles':np.percentile(border.max(1),[50,90,95,99,100]).tolist()})
 im=Image.fromarray(a); im.thumbnail((350,350));x=(k%3)*360+5;y=(k//3)*390+30; canvas.paste(im,(x,y));d.text((x,y-22),f'Frame {i} / {i/fps:.2f}s',fill='white')
canvas.save(out/'source-frames.png')
metadata={'width':int(frames[0].shape[1]),'height':int(frames[0].shape[0]),'fps':fps,'frames':len(frames),'stats':stats}
(out/'source-analysis.json').write_text(json.dumps(metadata,indent=2),encoding='utf-8')
print(json.dumps(metadata,indent=2))
