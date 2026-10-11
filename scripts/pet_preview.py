"""Shared color fitting and complete-action previews; never install a skin."""
import numpy as np
import subprocess
from pathlib import Path
from pet_color import validate_profile
from pet_video_converter import run
ROOT=Path(__file__).resolve().parents[1]
FFMPEG=ROOT/'runtime/ffmpeg.exe'
FPS=24
QUANTILES=[.05,.2,.4,.6,.8,.95]

def frames(video, count=None, *, ffmpeg=None, width=384, height=384, resize=False):
    args = [str(ffmpeg or FFMPEG), '-nostdin', '-hide_banner', '-loglevel', 'error',
            '-c:v', 'libvpx-vp9', '-i', str(video), '-map', '0:v:0']
    if count is not None:
        args += ['-frames:v', str(count)]
    if resize:
        args += ['-vf', f'format=gbrap,premultiply=inplace=1,scale={width}:{height}:flags=lanczos,unpremultiply=inplace=1']
    data = run(args+['-fps_mode', 'passthrough', '-an', '-pix_fmt', 'rgba', '-f', 'rawvideo', 'pipe:1']).stdout
    if not data or len(data) % (width*height*4):
        raise ValueError('Decoded RGBA dimensions differ from the preview canvas')
    return np.frombuffer(data, np.uint8).reshape(-1,height,width,4)


def neutral_mask(reference):
    rgb = reference[...,:3].astype(np.int16)
    # Fit only opaque, moderately bright, low-chroma character colors. Excludes
    # transparent matte RGB, saturated eyes, blush and red heart effects.
    return ((reference[...,3]>=240) & (rgb.mean(axis=-1)>=100) & (np.ptp(rgb,axis=-1)<=50))


def fit_profile(source, target, max_shift=14):
    source_colors = source[...,:3][neutral_mask(source)]
    target_colors = target[...,:3][neutral_mask(target)]
    if min(len(source_colors),len(target_colors))<1000:
        raise ValueError('Insufficient neutral character pixels for color matching')
    curves = {}
    for index, channel in enumerate('rgb'):
        s = np.quantile(source_colors[:,index], QUANTILES)
        t = np.quantile(target_colors[:,index], QUANTILES)
        points = [[0,0]]
        for x,y in zip(s,t):
            x = round(float(x),3)
            y = round(float(x+np.clip(y-x,-max_shift,max_shift)),3)
            if x <= points[-1][0] or x >= 255:
                continue
            points.append([x, max(points[-1][1],min(255,y))])
        points.append([255,255]); curves[channel]=points
    return validate_profile(dict(version=1, curves=curves, strength=1, chromaProtection=[45,100]))


def color_error(pixels, target, mask):
    s = np.quantile(pixels[...,:3][mask],QUANTILES,axis=0)
    t = np.quantile(target[...,:3][neutral_mask(target)],QUANTILES,axis=0)
    return round(float(np.abs(s-t).mean()),3)


def smoothstep(x):
    return x*x*(3-2*x)


def dissolve(before, after, amount):
    # Same premultiplied-alpha principle and 0.2s timing as the WPF renderer.
    a,b = before.astype(np.float32)/255,after.astype(np.float32)/255
    alpha = a[...,3]*(1-amount)+b[...,3]*amount
    color = a[...,:3]*a[...,3:4]*(1-amount)+b[...,:3]*b[...,3:4]*amount
    rgb = np.divide(color,alpha[...,None],out=np.zeros_like(color),where=alpha[...,None]>0)
    return np.rint(np.concatenate([rgb,alpha[...,None]],axis=-1)*255).clip(0,255).astype(np.uint8)


def sequence_frame(idle, action, index, fade=5):
    if index < 24:
        return idle[index%len(idle)]
    local = index-24
    if local < len(action):
        return dissolve(idle[23%len(idle)],action[local],smoothstep(local/fade)) if local<fade else action[local]
    local -= len(action)
    return dissolve(action[-1],idle[local%len(idle)],smoothstep(local/fade)) if local<fade else idle[local%len(idle)]


def encode_comparison(path,idle,before,after):
    if len(before)!=len(after):
        raise ValueError('Correction changed clip length')
    y,x = np.indices((384,384))
    gray = np.where(((x//16+y//16)%2)==0,226,245).astype(np.float32)
    background = np.repeat(gray[...,None],3,axis=-1)
    args = [str(FFMPEG),'-nostdin','-hide_banner','-loglevel','error','-y',
            '-f','rawvideo','-pix_fmt','rgb24','-s','1152x384','-r','24','-i','pipe:0',
            '-an','-c:v','libx264','-preset','fast','-crf','17','-pix_fmt','yuv420p',
            '-movflags','+faststart',str(path)]
    process = subprocess.Popen(args,stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
    try:
        for index in range(len(before)+48):
            panels = [idle[index%len(idle)],sequence_frame(idle,before,index),sequence_frame(idle,after,index)]
            flattened=[]
            for panel in panels:
                alpha = panel[...,3:4].astype(np.float32)/255
                flattened.append(np.rint(panel[...,:3]*alpha+background*(1-alpha)).astype(np.uint8))
            process.stdin.write(np.concatenate(flattened,axis=1).tobytes())
        process.stdin.close()
        error = process.stderr.read().decode('utf-8',errors='replace')
        if process.wait(timeout=90):
            raise ValueError('Comparison encoding failed: '+error[-1000:])
    finally:
        process.stderr.close()
        if process.poll() is None:
            process.kill(); process.wait()


def extract_png(video, output, frame=0):
    run([str(FFMPEG),'-nostdin','-hide_banner','-loglevel','error','-c:v','libvpx-vp9','-i',str(video),
         '-vf',f'select=eq(n\\,{frame}),format=rgba','-frames:v','1','-update','1','-y',str(output)])

