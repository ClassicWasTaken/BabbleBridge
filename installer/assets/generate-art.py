# Copyright (c) 2026 Frame Bridge contributors.
# SPDX-License-Identifier: Apache-2.0

"""Generate original Frame Bridge artwork. Requires Python3 and Pillow.

The headset mark, closed spline motif, layout and colors below are original
Frame Bridge assets. No Project Babble logo or website drawing is used.
IBM Plex Sans is an unmodified OFL1.1 font; its license stays with the app.
"""
from pathlib import Path
import math
import random
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
BRAND = HERE.parents[1] / 'src' / 'BabbleFrameBridge.App' / 'Assets' / 'Brand'
WIDTH, HEIGHT = 328, 628
ANCHORS = [(-.07,.60),(.16,.19),(.43,.37),(.74,.82),(1.07,.53),(.83,.12),(.57,.49),(.23,.86)]

def headset(image, bounds, color='#ECFAF1', badge=False):
    x, y, size = bounds
    drawing = ImageDraw.Draw(image)
    scale = size / 100
    point = lambda p: (x+p[0]*scale, y+p[1]*scale)
    if badge:
        drawing.rounded_rectangle((x,y,x+size,y+size), radius=size*.22, fill='#2C6A59')
    width = max(1, round(size*.047))
    # An original generic visor, nose bridge, side temples and top strap.
    # It is not a traced hardware silhouette or a Valve/Steam/Project Babble logo.
    def stroke(start, commands):
        current=start; points=[point(current)]
        for command in commands:
            if len(command)==3:
                control_a,control_b,end=command
                for i in range(1,33):
                    t=i/32; s=1-t
                    points.append(point((s**3*current[0]+3*s*s*t*control_a[0]+3*s*t*t*control_b[0]+t**3*end[0],
                                         s**3*current[1]+3*s*s*t*control_a[1]+3*s*t*t*control_b[1]+t**3*end[1])))
                current=end
            else:
                current=command; points.append(point(current))
        drawing.line(points,fill=color,width=width)
        for p in points:
            radius=width/2
            drawing.ellipse((p[0]-radius,p[1]-radius,p[0]+radius,p[1]+radius),fill=color)
    stroke((25,32), [((18,32),(14,37),(14,44)),(14,59),((14,67),(21,72),(29,72)),
                     (38,72),((43,72),(44,65),(50,65)),((56,65),(57,72),(62,72)),
                     (71,72),((79,72),(86,67),(86,59)),(86,44),((86,37),(82,32),(75,32)),(25,32)])
    stroke((14,42), [((5,42),(5,62),(14,62))])
    stroke((86,42), [((95,42),(95,62),(86,62))])
    stroke((37,32), [((37,19),(63,19),(63,32))])
    for center in ((31,47),(69,47)):
        px,py=point(center); radius=size*.026
        drawing.ellipse((px-radius,py-radius,px+radius,py+radius),fill=color)

def spline(u):
    u=(u%1)*len(ANCHORS); n=int(u); t=u-n
    a,b,c,d=[ANCHORS[(n+offset)%len(ANCHORS)] for offset in (-1,0,1,2)]
    result=[]
    for i in range(2):
        result.append(.5*(2*b[i]+(-a[i]+c[i])*t+(2*a[i]-5*b[i]+4*c[i]-d[i])*t*t
                           +(-a[i]+3*b[i]-3*c[i]+d[i])*t*t*t))
    return result

def font(weight, size):
    return ImageFont.truetype(str(BRAND/f'IBMPlexSans-{weight}.ttf'),size)

def main():
    BRAND.mkdir(parents=True,exist_ok=True)
    logo=Image.new('RGBA',(1024,1024))
    headset(logo,(0,0,1024),badge=True)
    logo=logo.resize((256,256),Image.Resampling.LANCZOS)
    logo.save(BRAND/'bridge-logo.png',optimize=True)
    logo.save(BRAND.parent/'Bridge.ico',sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
    (BRAND/'bridge-logo.svg').write_text('''<!-- Copyright (c) 2026 Frame Bridge contributors. SPDX-License-Identifier: Apache-2.0 -->
<!-- Original generic VR headset mark. Generated from installer/assets/generate-art.py. -->
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">
 <rect width="100" height="100" rx="22" fill="#2C6A59"/>
 <g fill="none" stroke="#ECFAF1" stroke-width="4.7" stroke-linecap="round" stroke-linejoin="round">
  <path d="M25 32C18 32 14 37 14 44V59C14 67 21 72 29 72H38C43 72 44 65 50 65C56 65 57 72 62 72H71C79 72 86 67 86 59V44C86 37 82 32 75 32ZM14 42C5 42 5 62 14 62M86 42C95 42 95 62 86 62M37 32C37 19 63 19 63 32"/>
 </g>
 <g fill="#ECFAF1"><circle cx="31" cy="47" r="2.6"/><circle cx="69" cy="47" r="2.6"/></g>
</svg>
''',encoding='utf-8')
    panel=Image.new('RGBA',(WIDTH,HEIGHT))
    pixels=panel.load()
    for y in range(HEIGHT):
        for x in range(WIDTH):
            t=min(1,max(0,.74*y/HEIGHT+.24*x/WIDTH))
            top=(42,101,84); bottom=(15,34,27)
            pixels[x,y]=tuple(round(a+(b-a)*t) for a,b in zip(top,bottom))+(255,)
    flow=Image.new('RGBA',panel.size); draw=ImageDraw.Draw(flow); rng=random.Random(0x4652414D)
    for _ in range(690):
        u=rng.random(); px,py=spline(u)
        qx,qy=spline(u+.0001); length=max(.001,math.hypot(qx-px,qy-py))
        lane=rng.gauss(0,14)
        x=px*WIDTH-(qy-py)/length*lane; y=300+py*230+(qx-px)/length*lane
        radius=1+rng.random()**1.9*3.1
        alpha=int(rng.uniform(13,49)*max(0,min(1,(y-295)/45,(565-y)/45)))
        color=(125,205,184) if rng.random()>.12 else (219,174,183)
        draw.ellipse((x-radius,y-radius,x+radius,y+radius),fill=(*color,alpha))
    panel=Image.alpha_composite(panel,flow)
    panel.alpha_composite(logo.resize((100,100),Image.Resampling.LANCZOS),(32,39))
    text=ImageDraw.Draw(panel)
    text.text((30,171),'Frame Bridge',font=font('BoldItalic',36),fill='#F2FCF5')
    text.multiline_text((32,230),'Steam Frame tracking.\nYour module, your setup.',font=font('Regular',16),fill='#C7DFD0',spacing=6)
    text.line((32,294,86,294),fill='#80BDA6',width=2)
    text.line((32,553,296,553),fill='#385D4A',width=1)
    text.text((32,573),'Independent desktop bridge',font=font('Medium',14),fill='#C6DECE')
    text.text((32,596),'Module ZIP supplied separately',font=font('Regular',12),fill='#A8C7B3')
    panel.convert('RGB').save(HERE/'wizard-panel.png',optimize=True)
    print('Generated original headset logo, icon, SVG, and installer panel.')

if __name__=='__main__':
    main()
