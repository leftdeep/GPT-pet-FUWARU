"""Rebuild existing actions from MOV with frozen layout/corrections and one final encode."""
import copy
import math
from pathlib import Path

from pet_anchor import anchor_matrix
from pet_color import write_cube
from pet_video_converter import run


def validate_rebuild(root, entry, source_hash, options, digest, manual=None):
    geometry = entry['geometry']
    if source_hash != geometry['sourceSha256']:
        raise ValueError('Quality rebuild source changed; recalibrate a separate candidate')
    if (manual or {}) != entry.get('manualCalibration', {}):
        raise ValueError('Quality rebuild manual anchors changed; create a separate candidate')
    baseline = (root / entry['baselineVideo']).resolve()
    if not baseline.is_relative_to((root / 'assets').resolve()) or not baseline.is_file() or digest(baseline) != entry['baselineVideoSha256']:
        raise ValueError('Quality rebuild baseline changed or is unavailable')
    original = entry['originalRecipe']
    scale = options['width'] / original['width']
    if not math.isclose(options['height'] / original['height'], scale):
        raise ValueError('Quality rebuild must preserve the canvas aspect ratio')
    unchanged = set(options) - {'width', 'height', 'crf', 'lossless', 'targetHeight', 'feetY'}
    if any(options[key] != original.get(key) for key in unchanged):
        raise ValueError('Quality rebuild layout/correction changed; create a separate candidate')
    if any(not math.isclose(options[key], original[key] * scale, abs_tol=1e-6) for key in ('targetHeight', 'feetY')):
        raise ValueError('Quality rebuild standing height and feet must scale with the canvas')
    return scale


def rebuild(source, destination, entry, options, ffmpeg):
    g = entry['geometry']
    scale = options['width'] / entry['originalRecipe']['width']
    width, height = options['width'], options['height']
    x, y, cw, ch = g['crop']
    sw, sh = [max(2, round(value * scale / 2) * 2) for value in g['scaled']]
    left, top = [round(value * scale) for value in g['padding']]
    if left < 0 or top < 0 or left + sw > width or top + sh > height:
        raise ValueError('Quality rebuild placement would crop visible content')
    vf = (f'fps={options["fps"]},format=rgba,crop={cw}:{ch}:{x}:{y},format=gbrap,'
          f'premultiply=inplace=1,scale={sw}:{sh}:flags=lanczos,unpremultiply=inplace=1,'
          f'format=rgba,pad={width}:{height}:{left}:{top}:color=black@0,setsar=1')
    if g['frameRange']:
        start, end = g['frameRange']
        vf += f',trim=start_frame={start}:end_frame={end},setpts=PTS-STARTPTS'
    cube = destination.parent / 'quality-color.cube'
    try:
        if g['colorCorrection']:
            write_cube(cube, g['colorCorrection'])
            vf += f',format=gbrap,lut3d=file={cube.name}:interp=tetrahedral'
        if g['anchorCorrection']:
            a, b, c, d, e, f = anchor_matrix(g['anchorCorrection'])
            c *= scale
            f *= scale
            corners = [(a*xx+b*yy+c, d*xx+e*yy+f) for xx, yy in ((0,0), (width,0), (0,height), (width,height))]
            coordinates = ':'.join(f'{axis}{i}={point[j]:.12g}' for i, point in enumerate(corners) for j, axis in enumerate('xy'))
            vf += (',format=gbrap,premultiply=inplace=1,perspective=' + coordinates +
                   ':sense=destination:interpolation=cubic:eval=init,unpremultiply=inplace=1')
        compression = ['-lossless', '1'] if options['lossless'] else ['-crf', str(options['crf']), '-b:v', '0']
        run([str(ffmpeg), '-nostdin', '-hide_banner', '-loglevel', 'error', '-y', '-i', str(source),
             '-map', '0:v:0', '-vf', vf, '-an', '-fps_mode', 'passthrough', '-c:v', 'libvpx-vp9',
             '-pix_fmt', 'yuva420p', '-auto-alt-ref', '0', '-deadline', 'good', '-cpu-used', '2',
             '-row-mt', '1', '-threads', '2', *compression, str(destination)], timeout=600, cwd=destination.parent)
    finally:
        cube.unlink(missing_ok=True)
    clip = copy.deepcopy(entry['clip'])
    clip.update(VideoWidth=width, VideoHeight=height, FrameRate=options['fps'])
    report = dict(kind='quality-rebuild', sourceSha256=g['sourceSha256'], recipe=options,
                  baselineVideoSha256=entry['baselineVideoSha256'], frozenGeometry=g,
                  canvas=[width, height], crop=g['crop'], scaledContent=[sw, sh], padding=[left, top],
                  actualStandingHeight=g['standing']['height']*sh/ch,
                  actualGroundY=top+(g['standing']['feetY']-y)*sh/ch,
                  crf=None if options['lossless'] else options['crf'], lossless=options['lossless'],
                  colorCorrection=g['colorCorrection'], anchorCorrection=g['anchorCorrection'],
                  finalLossyEncodes=0 if options['lossless'] else 1,
                  frameRange=g['frameRange'], frameCount=clip['VideoFrameCount'], frameRate=options['fps'],
                  outputBytes=destination.stat().st_size)
    return clip, report
