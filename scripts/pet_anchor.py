"""One fixed, premultiplied affine correction around reviewed feet/head anchors.

Only horizontal scale/shear and a constant ground offset are permitted. There is
no per-frame tracking, vertical scaling or automatic face deformation.
"""
import hashlib
import math
import re
import subprocess
from pathlib import Path


def validate_anchor_profile(profile):
    fields={'version','scaleX','sourceFeet','targetFeet','sourceHead','targetHead'}
    if not isinstance(profile,dict) or set(profile)!=fields or type(profile['version']) is not int or profile['version']!=1:
        raise ValueError('Anchor profile requires version 1 and all reviewed anchor fields')
    result=dict(profile)
    s=profile['scaleX']
    if type(s) not in (float,int) or not math.isfinite(s) or not .9<=s<=1.12:
        raise ValueError('Anchor horizontal scale must be in 0.9..1.12')
    for key in fields-{'version','scaleX'}:
        point=profile[key]
        if not isinstance(point,list) or len(point)!=2 or any(type(v) not in (float,int) or not math.isfinite(v) or v<0 for v in point):
            raise ValueError('Anchor coordinates must be finite [x,y] points')
        result[key]=list(point)
    sf,tf,sh,th=[result[k] for k in ('sourceFeet','targetFeet','sourceHead','targetHead')]
    if sf[1]-sh[1]<70 or tf[1]-th[1]<70:
        raise ValueError('Head must be well above the feet')
    if abs(tf[0]-sf[0])>8 or abs(tf[1]-sf[1])>2 or abs(th[0]-sh[0])>8:
        raise ValueError('Anchor correction exceeds the small position adjustment limits')
    shear=(th[0]-tf[0]-s*(sh[0]-sf[0]))/(sh[1]-sf[1])
    if abs(shear)>.06:
        raise ValueError('Anchor shear would change the pose too much')
    return result


def anchor_matrix(profile):
    p=validate_anchor_profile(profile)
    sf,tf,sh,th=[p[k] for k in ('sourceFeet','targetFeet','sourceHead','targetHead')]
    s=p['scaleX'];k=(th[0]-tf[0]-s*(sh[0]-sf[0]))/(sh[1]-sf[1])
    return [s,k,tf[0]-s*sf[0]-k*sf[1],0,1,tf[1]-sf[1]]


def transform_point(point,matrix):
    x,y=point;a,b,c,d,e,f=matrix
    return [a*x+b*y+c,d*x+e*y+f]


def anchor_filter(profile,width,height,coordinate_scale=1):
    m=anchor_matrix(profile)
    m[2] *= coordinate_scale
    m[5] *= coordinate_scale
    for key in ('sourceFeet','targetFeet','sourceHead','targetHead'):
        if not (profile[key][0]*coordinate_scale<width and profile[key][1]*coordinate_scale<height):
            raise ValueError('Anchor is outside the canvas')
    corners=[transform_point(point,m) for point in ((0,0),(width,0),(0,height),(width,height))]
    coordinates=':'.join(f'{axis}{i}={point[j]:.12g}' for i,point in enumerate(corners) for j,axis in enumerate('xy'))
    # Convert once; the alpha verification below explicitly includes the same
    # final format negotiation used by the encoder. Matte RGB stays premultiplied.
    return ('format=gbrap,premultiply=inplace=1,perspective='+coordinates+
            ':sense=destination:interpolation=cubic:eval=init,unpremultiply=inplace=1,format=yuva420p')


def correct_anchor_video(source,destination,profile,width,height,ffmpeg,coordinate_scale=1):
    source,destination,ffmpeg=map(lambda p:Path(p).resolve(),(source,destination,ffmpeg))
    filters=anchor_filter(profile,width,height,coordinate_scale);matrix=anchor_matrix(profile)
    matrix[2] *= coordinate_scale
    matrix[5] *= coordinate_scale
    base=[str(ffmpeg),'-nostdin','-hide_banner','-loglevel','error','-c:v','libvpx-vp9','-i',str(source),'-map','0:v:0']
    scan=subprocess.run(base+['-vf','format=rgba,alphaextract,bbox=min_val=8','-loglevel','info','-an','-f','null','-'],
        capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=90)
    boxes=[tuple(map(int,m)) for m in re.findall(r'x1:(\d+) x2:(\d+) y1:(\d+) y2:(\d+)',scan.stderr)]
    if scan.returncode or not boxes:raise ValueError('Cannot measure complete action bounds')
    points=[transform_point((x,y),matrix) for x0,x1,y0,y1 in boxes
            for x in (x0,x1+1) for y in (y0,y1+1)]
    bounds=[min(p[0] for p in points),min(p[1] for p in points),max(p[0] for p in points),max(p[1] for p in points)]
    if bounds[0]<3 or bounds[1]<3 or bounds[2]>width-3 or bounds[3]>height-3:
        raise ValueError('Anchor correction would clip the character or an effect')
    destination.parent.mkdir(parents=True,exist_ok=True)
    result=subprocess.run(base+['-vf',filters,'-an','-fps_mode','passthrough','-c:v','libvpx-vp9','-pix_fmt','yuva420p',
        '-lossless','1','-auto-alt-ref','0','-deadline','good','-cpu-used','2','-row-mt','1','-threads','4','-y',str(destination)],
        capture_output=True,timeout=180)
    if result.returncode:
        destination.unlink(missing_ok=True)
        raise ValueError('Anchor correction failed: '+result.stderr.decode('utf-8',errors='replace')[-1200:])
    checks=[]
    for path,vf in ((source,filters+',alphaextract'),(destination,'alphaextract')):
        decoded=subprocess.run([str(ffmpeg),'-nostdin','-hide_banner','-loglevel','error','-c:v','libvpx-vp9',
            '-i',str(path),'-vf',vf,'-fps_mode','passthrough','-an','-pix_fmt','gray','-f','rawvideo','pipe:1'],
            capture_output=True,timeout=90)
        if decoded.returncode or not decoded.stdout:
            destination.unlink(missing_ok=True);raise ValueError('Cannot verify anchor-corrected alpha')
        checks.append((len(decoded.stdout),hashlib.sha256(decoded.stdout).hexdigest()))
    if checks[0]!=checks[1]:
        destination.unlink(missing_ok=True);raise ValueError('Encoding changed the planned anchor alpha transform')
    return dict(matrix=matrix,scaleY=1,constantGroundOffsetPixels=matrix[5],
        headVerticalResidualPixels=transform_point([v*coordinate_scale for v in profile['sourceHead']],matrix)[1]-profile['targetHead'][1]*coordinate_scale,
        transformedBounds=[round(v,3) for v in bounds],lossless=True,perFrameGroundPinning=False,
        decodedAlphaMatchesPlannedTransform=True)
