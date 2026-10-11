"""Fit one conservative color/head/sole correction for a new normalized clip.

The small template search applies only to a declared, comparable standing pose.
It is not a semantic pose detector. Uncertain measurements need hash-bound manual
annotations; nothing is fitted to the moving frames or pinned to the floor.
"""
import hashlib
import json
from pathlib import Path

from pet_anchor import validate_anchor_profile, correct_anchor_video, anchor_matrix, transform_point
from pet_color import recolor_video
from pet_video_converter import run, write_json


class NeedsCalibration(ValueError):
    def __init__(self, key, message, measurement=None):
        super().__init__(f'{key}: {message}')
        self.key, self.measurement = key, measurement


def load_policy(root, value, base_dir, skin):
    """No NumPy import on a reviewed/cache-only run; reference bytes are gated."""
    path = (root/value).resolve()
    policy = json.loads(path.read_text(encoding='utf-8-sig'))
    if set(policy) != {'version','referenceAction','referenceAnnotations','colorMaxShift'} or policy['version'] != 1:
        raise ValueError('Invalid automatic calibration policy')
    if type(policy['colorMaxShift']) not in (int,float) or not 0 < policy['colorMaxShift'] <= 14:
        raise ValueError('Automatic color shifts must be in 0..14')
    annotations = json.loads((root/policy['referenceAnnotations']).read_text(encoding='utf-8-sig'))
    key = policy['referenceAction']
    reference = annotations['actions'][key]
    clip = skin['Clips'][key]
    video = (base_dir/clip['Video']).resolve()
    if not video.is_relative_to(base_dir) or not video.is_file():
        raise ValueError('Calibration reference must be inside the base skin')
    with video.open('rb') as stream:
        video_hash = hashlib.file_digest(stream,'sha256').hexdigest()
    if video_hash != reference['reviewedColorVideoSha256']:
        raise ValueError('Calibration reference changed; review the standing annotations first')
    if annotations['canvas'] != [384,384] or [clip['VideoWidth'],clip['VideoHeight'],clip['FrameRate']] != [384,384,24]:
        raise ValueError('Automatic standing calibration currently requires the shared 384x384 / 24 fps canvas')
    window = annotations['standingWindowFrames']
    if type(window) is not int or not 3 <= window <= 10:
        raise ValueError('Standing window must contain 3..10 frames')
    return dict(policy=policy, video=video, videoSha256=video_hash,
                headRegion=annotations['headRegion'], feetContacts=reference['feetContacts'], window=window)


def signature(reference, manual):
    return {k:v for k,v in reference.items() if k != 'video'} | {'manual':manual}


def manual_options(value):
    allowed={'normalizedVideoSha256','feetContacts','headCenter','scaleX'}
    if not isinstance(value,dict) or set(value)-allowed:
        raise ValueError('Action calibration accepts only hash-bound feetContacts, headCenter and scaleX')
    if value and (not isinstance(value.get('normalizedVideoSha256'),str) or len(value['normalizedVideoSha256'])!=64):
        raise ValueError('Manual calibration requires normalizedVideoSha256 from the measurement report')
    return value


def head_measure(frames, roi):
    import numpy as np
    x,y,w,h=roi
    if min(x,y)<0 or min(w,h)<20 or x+w>384 or y+h>384:
        raise ValueError('Head measurement region is outside the canvas')
    masks=frames[:,y:y+h,x:x+w,3]>220
    if (masks.mean(axis=(1,2)) < .4).any():
        raise ValueError('Standing head silhouette is missing or occluded')
    centers=[]
    for mask in masks:
        yy,xx=np.nonzero(mask);centers.append([xx.mean()+x,yy.mean()+y])
    if np.ptp(centers,axis=0).max()>2:
        raise ValueError('Head moves too much in the standing reference window')
    widths=[]
    for row in masks.mean(axis=0):
        xs=np.flatnonzero(row>.8)
        if len(xs)>=20:
            if xs[0]==0 or xs[-1]==w-1:
                raise ValueError('Head silhouette exceeds the measurement region')
            widths.append(xs[-1]-xs[0]+1)
    if len(widths)<20:
        raise ValueError('Insufficient corresponding head rows for horizontal calibration')
    return np.mean(centers,axis=0).tolist(),float(np.median(widths))


def points(value):
    import numpy as np
    a=np.asarray(value,dtype=float)
    if a.shape!=(2,2) or not np.isfinite(a).all() or (a<0).any() or (a>=384).any() or not 6<=a[1,0]-a[0,0]<=32 or abs(a[0,1]-a[1,1])>2:
        raise ValueError('feetContacts must be two separated, level sole centers inside the canvas')
    return a


def match_feet(source, target, feet):
    import numpy as np
    # Match each reviewed sole patch locally. Alpha carries the contour; RGB
    # helps reject transparent tails/skirts and the neighbouring shoe.
    sm=source.astype(float).mean(axis=0)/255;tm=target.astype(float).mean(axis=0)/255
    found=[];quality=[]
    for fx,fy in points(feet):
        x,y=round(float(fx))-5,round(float(fy))-9
        template=tm[y:y+12,x:x+11]
        if template.shape!=(12,11,4) or (template[...,3]>.8).sum()<30:
            raise ValueError('Reviewed sole template is unavailable')
        candidates=[]
        for dy in range(-2,3):
            for dx in range(-8,9):
                patch=sm[y+dy:y+dy+12,x+dx:x+dx+11]
                if patch.shape!=template.shape:continue
                alpha=np.abs(patch[...,3]-template[...,3]).mean()
                mask=(patch[...,3]>.8)&(template[...,3]>.8)
                color=np.abs(patch[...,:3]-template[...,:3])[mask].mean() if mask.any() else 1
                a,b=patch[...,3]>.7,template[...,3]>.7
                iou=(a&b).sum()/max(1,(a|b).sum())
                candidates.append((float(.75*alpha+.25*color),dx,dy,float(iou)))
        best=min(candidates)
        distant=min(c[0] for c in candidates if abs(c[1]-best[1])>=3 or abs(c[2]-best[2])>=2)
        if best[0]>.16 or best[3]<.65 or abs(best[1])==8 or abs(best[2])==2 or distant-best[0]<.008:
            raise ValueError('Sole match is uncertain; annotate the two foot contacts in the measurement image')
        # Verify the same local patch exists in every reference frame.
        dx,dy=best[1:3]
        patches=source[:,y+dy:y+dy+12,x+dx:x+dx+11,3]>180
        if (patches.sum(axis=(1,2))<30).any():
            raise ValueError('A foot disappears during the standing reference window')
        centers=[np.array(np.nonzero(patch)).mean(axis=1) for patch in patches]
        if np.ptp(centers,axis=0).max()>2:
            raise ValueError('Feet move too much in the standing reference window')
        found.append([float(fx+dx),float(fy+dy)])
        quality.append(dict(cost=round(best[0],4),alphaIoU=round(best[3],4),ambiguityMargin=round(distant-best[0],4)))
    return points(found).tolist(),quality


def measurement_image(path, target, source, reference, head=None, feet=None):
    from PIL import Image,ImageDraw
    sheet=Image.new('RGB',(768,424),'#eeeeef')
    for col,frame in enumerate((target[0],source[0])):
        image=Image.fromarray(frame);background=Image.new('RGBA',image.size,'#eeeeef');background.alpha_composite(image)
        draw=ImageDraw.Draw(background)
        for p in reference['feetContacts']:
            x,y=p;draw.line((x-4,y,x+4,y),fill='#009bbe');draw.line((x,y-4,x,y+4),fill='#009bbe')
        x,y,w,h=reference['headRegion'];draw.rectangle((x,y,x+w,y+h),outline='#009bbe')
        if col:
            for x,y in ([head] if head is not None else [])+(feet or []):
                draw.ellipse((x-3,y-3,x+3,y+3),outline='#f57412')
        sheet.paste(background.convert('RGB'),(col*384,24))
    d=ImageDraw.Draw(sheet);d.text((8,6),'REFERENCE IDLE',fill='#273042');d.text((392,6),'NORMALIZED SOURCE - pixel coordinates',fill='#273042')
    # Coordinate grid makes manual annotation possible without another tool.
    for x in range(140,241,10):
        d.line((384+x,330,384+x,408),fill='#88aaaa',width=1);d.text((384+x,409),str(x),fill='#273042')
    sheet.save(path)


def calibrate(video,destination,reference,options,manual,key,report_dir,ffmpeg):
    try:
        import numpy as np
        import pet_preview as preview
        from PIL import Image  # Fail before encoding if the optional runtime is missing.
    except ImportError as exc:
        raise ValueError('New MOV calibration requires NumPy and Pillow; use the bundled Python runtime') from exc
    preview.FFMPEG=Path(ffmpeg)
    if options['width'] != options['height'] or options['width'] not in (384,640) or options['fps'] != 24:
        raise ValueError('New actions must use a shared 384 or 640 square / 24 fps canvas')
    # Fit in the reviewed 384 coordinate system, then scale the fixed transform
    # to the actual output. Never fit low-resolution anchors to raw 640 coordinates.
    coordinate_scale = options['width']/384
    normalized_hash=hashlib.sha256(video.read_bytes()).hexdigest()
    source=preview.frames(video,ffmpeg=ffmpeg,resize=coordinate_scale!=1);target=preview.frames(reference['video'],ffmpeg=ffmpeg)
    window=reference['window'];start=round((options['referenceStart'] or 0)*options['fps'])
    standing=source[start:start+window];target_standing=target[:window]
    measurement=report_dir/(key+'.calibration.json');picture=report_dir/(key+'-measurement.png')
    report=dict(normalizedVideoSha256=normalized_hash,normalizedVideo=str(video),referenceVideoSha256=reference['videoSha256'],
                referenceFrames=list(range(start,start+window)),referenceHeadRegion=reference['headRegion'],
                referenceFeetContacts=reference['feetContacts'],measurementImage=str(picture),
                measurementCanvas=[384,384],outputCanvas=[options['width'],options['height']],coordinateScale=coordinate_scale)
    try:
        if len(standing)!=window:raise ValueError('Standing referenceStart is outside the normalized full clip')
        measurement_image(picture,target_standing,standing,reference)
        if options['referenceStart'] is None:raise ValueError('Choose a clean comparable standing referenceStart before automatic calibration')
        manual_options(manual)
        if manual and manual['normalizedVideoSha256']!=normalized_hash:
            raise ValueError('Manual anchors belong to different normalized bytes; measure the new image')
        target_head,target_width=head_measure(target_standing,reference['headRegion'])
        if 'headCenter' in manual and 'scaleX' in manual:
            source_head,source_width=manual['headCenter'],None
        else:
            source_head,source_width=head_measure(standing,reference['headRegion'])
            if 'headCenter' in manual:source_head=manual['headCenter']
        if 'feetContacts' in manual:source_feet=points(manual['feetContacts']).tolist();quality='manual'
        else:source_feet,quality=match_feet(standing,target_standing,reference['feetContacts'])
        scale=manual['scaleX'] if 'scaleX' in manual else target_width/source_width
        if 'scaleX' not in manual and abs(scale-1)<.015:scale=1.
        if not .94<=scale<=1.08:raise ValueError('Horizontal size differs by more than the conservative 0.94..1.08 limit; review the reference pose')
        if abs(source_head[1]-target_head[1])>2:
            raise ValueError('Head height differs by more than 2 pixels; choose a comparable standing reference')
        anchor=validate_anchor_profile(dict(version=1,scaleX=scale,sourceHead=source_head,targetHead=target_head,
            sourceFeet=np.mean(source_feet,axis=0).tolist(),targetFeet=np.mean(points(reference['feetContacts']),axis=0).tolist()))
        if abs(source_head[1]+anchor['targetFeet'][1]-anchor['sourceFeet'][1]-target_head[1])>2:
            raise ValueError('Head and sole heights cannot both align with a fixed ground offset; choose a comparable pose')
        color=options.get('colorCorrection') or preview.fit_profile(standing,target_standing,reference['policy']['colorMaxShift'])
        report.update(status='fitted',headCenter=source_head,feetContacts=source_feet,soleMatching=quality,
                      sourceHeadWidth=source_width,targetHeadWidth=target_width,colorCorrection=color,anchorCorrection=anchor)
        measurement_image(picture,target_standing,standing,reference,source_head,source_feet)
        write_json(measurement,report)
    except (ValueError,TypeError,IndexError) as exc:
        report.update(status='needs_calibration',reason=str(exc));write_json(measurement,report)
        raise NeedsCalibration(key,str(exc),str(measurement)) from exc
    colored=destination.with_name('color.webm')
    recolor_video(video,colored,color,ffmpeg)
    geometry=correct_anchor_video(colored,destination,anchor,options['width'],options['height'],ffmpeg,coordinate_scale)
    after=preview.frames(destination,ffmpeg=ffmpeg,resize=coordinate_scale!=1)
    before_error=preview.color_error(standing,target_standing,preview.neutral_mask(standing))
    colored_standing=preview.frames(colored,ffmpeg=ffmpeg,resize=coordinate_scale!=1)[start:start+window]
    after_error=preview.color_error(colored_standing,target_standing,preview.neutral_mask(standing))
    span=options['frameRange'] or [0,len(source)]
    if span[1]>len(source):raise ValueError('Requested calibrated segment exceeds the full source')
    comparison=report_dir/(key+'-comparison.mp4')
    preview.encode_comparison(comparison,target,source[span[0]:span[1]],after[span[0]:span[1]])
    report.update(status='ok',colorQuantileErrorBefore=before_error,colorQuantileErrorAfter=after_error,
                  comparison=str(comparison),geometry=geometry,frameRange=span)
    write_json(measurement,report)
    colored.unlink()
    return report


def trim_video(source,destination,span,ffmpeg):
    run([str(ffmpeg),'-nostdin','-hide_banner','-loglevel','error','-c:v','libvpx-vp9','-i',str(source),
         '-vf',f'trim=start_frame={span[0]}:end_frame={span[1]},setpts=PTS-STARTPTS','-an','-fps_mode','passthrough',
         '-c:v','libvpx-vp9','-pix_fmt','yuva420p','-lossless','1','-auto-alt-ref','0','-threads','4','-y',str(destination)])
