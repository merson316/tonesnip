"""Turns the screenshots tools/readme-shots took into assets/readme: each window cut out at its border with rounded
corners, the snip screen cropped to its bar and selection. Usage: make-images.py <tonesnip-shots folder>"""
import os
import sys
from PIL import Image, ImageDraw
S = sys.argv[1].rstrip('/') + '/'
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'assets', 'readme') + '/'
def window(src, theme, dst, radius=8, width=None, framed=True):
    im=Image.open(S+src).convert('RGB')
    # The window's 1 px border sits 7 px in from the left, right and bottom of the shot (the resize frame and shadow
    # are outside it) and on its top edge; a popup such as the flyout fills its shot.
    box=(7,0,im.width-7,im.height-7) if framed else (0,0,im.width,im.height); im=im.crop(box)
    mask=Image.new('L',im.size,0); ImageDraw.Draw(mask).rounded_rectangle([0,0,im.width-1,im.height-1],radius=radius,fill=255)
    out=im.convert('RGBA'); out.putalpha(mask)
    if width and out.width>width: out=out.resize((width,round(out.height*width/out.width)),Image.LANCZOS)
    out.save(OUT+dst,optimize=True); print(dst,box,out.size)
def overlay(src, dst):
    # Centred on the monitor, where the bar is, and as wide as the selection overlay-shot.ps1 drags plus a margin.
    im=Image.open(S+src).convert('RGB'); c=im.width//2
    im=im.crop((c-1150,0,c+1150,1130))
    im=im.resize((1380,round(1130*1380/2300)),Image.LANCZOS)
    mask=Image.new('L',im.size,0); ImageDraw.Draw(mask).rounded_rectangle([0,0,im.width-1,im.height-1],radius=10,fill=255)
    out=im.convert('RGBA'); out.putalpha(mask); out.save(OUT+dst,optimize=True); print(dst,out.size)
for t in ('dark','light'): overlay(f'readme-overlay-{t}.png',f'snip-{t}.png')
window('readme-dark/screenshots/editor-hdr.png','dark','editor-dark.png')
window('readme-light/screenshots/editor-hdr.png','light','editor-light.png')
window('readme-dark/screenshots/flyout-grid.png','dark','recent-dark.png',framed=False)
window('readme-light/screenshots/flyout-grid.png','light','recent-light.png',framed=False)
window('readme-dark/screenshots/settings-tonemap.png','dark','settings-dark.png')
window('readme-light/screenshots/settings-tonemap.png','light','settings-light.png')
