"""Configuration-driven, incremental desktop-pet action import (Python 3.11+)."""
import argparse
import contextlib
import copy
import hashlib
import html
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from pathlib import Path

from pet_video_converter import run, write_json
from pet_color import validate_profile, recolor_video
from pet_anchor import validate_anchor_profile, correct_anchor_video
from pet_calibration import load_policy, signature, manual_options, calibrate, trim_video, NeedsCalibration
from pet_quality import validate_rebuild, rebuild

TOOL_ROOT = Path(__file__).resolve().parents[1]
PARAMETERS = {'fps', 'width', 'height', 'crf', 'lossless', 'margin', 'bboxAlpha',
              'targetHeight', 'feetY', 'referenceStart', 'contentRegion', 'crop', 'frameRange', 'colorCorrection','anchorCorrection'}
DEFAULTS = dict(fps=24, width=640, height=640, crf=40, lossless=False, margin=.08,
                bboxAlpha=64, targetHeight=209*640/384, feetY=576, referenceStart=None,
                contentRegion=None, crop=None, frameRange=None)


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def fingerprint(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, ensure_ascii=False,
                                    separators=(',', ':'), allow_nan=False).encode()).hexdigest()


def resolve(root, value):
    return (root / value).resolve()


def inside(path, directory):
    if not path.is_relative_to(directory.resolve()):
        raise ValueError(f'Path must stay inside {directory}: {path}')
    return path


def keys(value, allowed, label):
    if not isinstance(value, dict) or value.keys() - allowed:
        raise ValueError(f'Invalid or unknown fields in {label}')


def recipe(defaults, action):
    keys(defaults, PARAMETERS, 'defaults')
    keys(action.get('conversion', {}), PARAMETERS, 'conversion')
    result = DEFAULTS | defaults | action.get('conversion', {})
    if result.get('colorCorrection') is not None:
        result['colorCorrection'] = validate_profile(result['colorCorrection'])
    else:
        result.pop('colorCorrection', None)  # Preserve existing reviewed-recipe fingerprints.
    if result.get('anchorCorrection') is not None:
        result['anchorCorrection']=validate_anchor_profile(result['anchorCorrection'])
    else:
        result.pop('anchorCorrection',None)
    for key, value in result.items():
        if isinstance(value, float) and not math.isfinite(value):
            raise ValueError(f'{key} must be finite')
    for key, low, high in [('fps', 1, 60), ('width', 16, 640), ('height', 16, 640),
                           ('crf', 0, 63), ('bboxAlpha', 1, 255)]:
        if type(result[key]) is not int or not low <= result[key] <= high:
            raise ValueError(f'Invalid {key}')
    if result['width'] % 2 or result['height'] % 2 or type(result['lossless']) is not bool:
        raise ValueError('Canvas dimensions must be even; lossless must be boolean')
    if not 0 <= result['margin'] <= .2 or not 0 < result['targetHeight'] <= max(result['width'], result['height']):
        raise ValueError('Invalid margin or standing height')
    if not result['height'] * result['margin'] < result['feetY'] < result['height'] * (1-result['margin']):
        raise ValueError('Ground line must be inside the safe canvas')
    if result['referenceStart'] is not None and result['referenceStart'] < 0:
        raise ValueError('Reference time must be nonnegative')
    for key in ('crop', 'contentRegion'):
        value = result[key]
        if value is not None and (not isinstance(value, list) or len(value) != 4 or
                                  any(type(n) is not int for n in value)):
            raise ValueError(f'{key} must be [x,y,width,height]')
    span = result['frameRange']
    if span is not None and (not isinstance(span, list) or len(span) != 2 or
                            any(type(n) is not int for n in span) or not 0 <= span[0] < span[1]):
        raise ValueError('frameRange must be [first,last), zero based, at output fps')
    return result


@contextlib.contextmanager
def import_lock(path):
    # OS releases the lock even if a process crashes; the small lock file can remain.
    with path.open('a+b') as stream:
        stream.seek(0); stream.write(b'0'); stream.flush(); stream.seek(0)
        if os.name == 'nt':
            import msvcrt
            try:
                msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
            except OSError:
                raise ValueError('Another action import is running') from None
        else:
            import fcntl
            fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            yield
        finally:
            if os.name == 'nt':
                stream.seek(0); msvcrt.locking(stream.fileno(), msvcrt.LK_UNLCK, 1)


def verify_video(video, clip, ffmpeg, preview):
    width, height, count, fps = [clip[k] for k in ('VideoWidth', 'VideoHeight', 'VideoFrameCount', 'FrameRate')]
    if any(type(n) is not int for n in (width, height, count)) or not (16 <= width <= 640 and 16 <= height <= 640):
        raise ValueError('Unsupported video dimensions')
    if not 1 <= count <= 3600 or not 1 <= fps <= 60:
        raise ValueError('Clip exceeds the supported duration/frame-rate limit')
    if not 0 < video.stat().st_size <= 64*1024*1024:
        raise ValueError('Video exceeds the 64 MiB source limit')
    base = [str(ffmpeg), '-nostdin', '-hide_banner', '-c:v', 'libvpx-vp9', '-i', str(video), '-map', '0:v:0']
    probe = run(base + ['-vf', 'showinfo', '-frames:v', str(count+1), '-an', '-f', 'null', '-'],
                text=True, encoding='utf-8', errors='replace')
    info = re.search(r'Stream[^\n]*Video: vp9[^\n]*? (\d+)x(\d+)', probe.stderr)
    # Check actual timestamps rather than inferred average fps: short WebM files
    # can report 24.08/24.39 fps due to their millisecond timestamp precision.
    times = [float(n) for n in re.findall(r'\bn:\s*\d+\s+pts:\s*-?\d+\s+pts_time:([\d.e+\-]+)', probe.stderr)]
    timing_ok = len(times) == count and all(abs(t-times[0]-i/fps) <= .0011 for i,t in enumerate(times))
    if not info or tuple(map(int, info.groups())) != (width, height) or not timing_ok:
        streams = [line.strip() for line in probe.stderr.splitlines() if 'Stream' in line and 'Video:' in line]
        raise ValueError('Video dimensions/frame rate differ from skin.json: ' + ' / '.join(streams))
    process = subprocess.Popen(base + ['-loglevel', 'error', '-fps_mode', 'passthrough', '-an',
                                      '-pix_fmt', 'bgra', '-f', 'rawvideo', 'pipe:1'],
                               stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    decoded = 0
    try:
        while frame := process.stdout.read(width*height*4):
            if len(frame) != width*height*4 or decoded >= count:
                raise ValueError('Decoded frame count differs from skin.json')
            alpha = frame[3::4]
            if min(alpha) >= 12 or max(alpha) <= 200:
                raise ValueError(f'Frame {decoded} lost transparency or visible content')
            # A visible pixel at a canvas edge signals a clipped character/effect.
            border = alpha[:width] + alpha[-width:] + alpha[::width] + alpha[width-1::width]
            if max(border) >= 64:
                raise ValueError(f'Visible content touches the canvas edge at frame {decoded}')
            decoded += 1
        if process.wait(timeout=30) or decoded != count:
            raise ValueError('Video decoder failed or frame count differs')
    finally:
        process.stdout.close()
        if process.poll() is None:
            process.kill(); process.wait()
    indices = sorted({0, count//2, count-1})
    selection = '+'.join(f'eq(n\\,{n})' for n in indices)
    run(base + ['-loglevel', 'error', '-vf', f'select={selection},tile=3x1:nb_frames={len(indices)}',
                '-frames:v', '1', '-update', '1', '-y', str(preview)])
    return dict(decodedFrames=decoded, frameTimingVerified=True, allFramesAlphaVerified=True, clearCanvasEdges=True,
                previewFrames=indices, decodedBytes=width*height*count*4)


def converter_arguments(source, destination, action, options, ffmpeg):
    args = [sys.executable, str(TOOL_ROOT/'scripts/import-alpha-video.py'), str(source),
            '--output', str(destination), '--state', action['key'], '--ffmpeg', str(ffmpeg),
            '--action-label', action['label'], '--preview-group', action['group'], '--normalization', 'standing']
    mappings = {'fps':'fps', 'width':'width', 'height':'height', 'crf':'crf', 'margin':'margin',
                'bboxAlpha':'bbox-alpha', 'targetHeight':'target-height', 'feetY':'feet-y',
                'referenceStart':'reference-start', 'crop':'crop', 'contentRegion':'content-region'}
    for key, flag in mappings.items():
        value = options[key]
        if value is not None:
            args += ['--'+flag, ','.join(map(str, value)) if isinstance(value, list) else str(value)]
    if options['lossless']:
        args += ['--lossless']
    if not action['loop']:
        args += ['--once']
    if options['frameRange']:
        args += ['--start-frame', str(options['frameRange'][0]), '--end-frame', str(options['frameRange'][1])]
    if options.get('colorCorrection'):
        profile_path = destination/'color-profile.json'
        write_json(profile_path, options['colorCorrection'])
        args += ['--color-profile', str(profile_path)]
    return args


def publish_file(source, destination):
    if destination.is_file() and digest(source) == digest(destination):
        return
    temporary = destination.with_name(destination.name + '.tmp')
    try:
        shutil.copyfile(source, temporary)
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)


def normalize_for_calibration(root, source, source_hash, action, options, skin, ffmpeg, report_dir):
    # Full-video normalization is shared by segments and retained independently
    # of color/anchor fitting. A failed measurement does not repeat MOV conversion.
    normalized_options={k:v for k,v in options.items() if k not in ('colorCorrection','anchorCorrection')}
    normalized_options.update(frameRange=None,lossless=True)
    key=fingerprint(dict(source=source_hash,options=normalized_options,
        tool=[digest(TOOL_ROOT/'scripts/pet_video_converter.py'),digest(ffmpeg)]))
    entry=root/'output/.cache/animation-normalized'/key
    entry.mkdir(parents=True,exist_ok=True)
    receipt_path=entry/'receipt.json';video=entry/'video.webm'
    try:receipt=read_json(receipt_path)
    except (OSError,ValueError):receipt=None
    if not isinstance(receipt,dict) or not video.is_file() or digest(video)!=receipt.get('videoSha256'):
        with tempfile.TemporaryDirectory(prefix='.stage-',dir=entry) as temporary:
            stage=Path(temporary);write_json(stage/'skin.json',skin)
            result=subprocess.run(converter_arguments(source,stage,action,normalized_options,ffmpeg),
                capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=600)
            (report_dir/(action['key']+'.log')).write_text(result.stdout+'\n'+result.stderr,encoding='utf-8')
            if result.returncode:raise ValueError(f'{action["key"]}: normalization failed. See {report_dir/(action["key"]+".log")}\n{result.stderr[-1000:]}')
            clip=read_json(stage/'skin.json')['Clips'][action['key']];generated=stage/clip['Video']
            conversion=read_json(generated.with_suffix('.conversion.json'))
            if abs(conversion['actualStandingHeight']-options['targetHeight'])>2 or abs(conversion['actualGroundY']-options['feetY'])>1:
                raise ValueError(f'{action["key"]}: rounded standing calibration exceeds tolerance')
            validation=verify_video(generated,clip,ffmpeg,stage/'preview.png')
            receipt=dict(clip=clip,conversion=conversion,validation=validation,videoSha256=digest(generated))
            publish_file(generated,video);write_json(receipt_path,receipt)
    return video,copy.deepcopy(receipt['clip']),copy.deepcopy(receipt['conversion'])


def import_actions(root, config_path, report_dir, ffmpeg, verify_all=False):
    config = read_json(config_path)
    keys(config, {'schemaVersion','baseSkin','outputSkin','name','defaults','actions','ignoredSources','calibration','qualityProfiles'}, 'config')
    if config.get('schemaVersion') != 1 or not isinstance(config.get('actions'), list) or not config['actions']:
        raise ValueError('Expected schemaVersion 1 and a nonempty actions list')
    base_dir = resolve(root, config['baseSkin'])
    destination = inside(resolve(root, config['outputSkin']), root/'assets/imported')
    if destination == (root/'assets/imported').resolve() or base_dir == destination or base_dir.is_relative_to(destination):
        raise ValueError('Output must be a separate subfolder of assets/imported')
    skin = read_json(base_dir/'skin.json')
    skin['Name'] = config.get('name', skin['Name'])
    reference=load_policy(root,config['calibration'],base_dir,skin) if config.get('calibration') else None
    profiles = read_json(resolve(root, config['qualityProfiles'])) if config.get('qualityProfiles') else {'version':1,'actions':{}}
    if profiles.get('version') != 1 or not isinstance(profiles.get('actions'), dict):
        raise ValueError('Invalid frozen quality profiles')
    planned, seen, registered = [], set(), set()
    hashes = {}
    def file_hash(path):
        if path not in hashes:
            hashes[path] = digest(path)
        return hashes[path]
    for action in config['actions']:
        keys(action, {'key','label','group','loop','source','conversion','reviewed','alignment','colorFromReviewed','anchorFromReviewed','reviewedFinal','calibration'}, 'action')
        manual_options(action.get('calibration',{}))
        if type(action.get('reviewedFinal',False)) is not bool:
            raise ValueError('reviewedFinal must be boolean')
        if action.get('reviewedFinal') and (action.get('colorFromReviewed') or action.get('anchorFromReviewed')):
            raise ValueError('A final reviewed video must not receive the correction twice')
        if type(action.get('colorFromReviewed',False)) is not bool or action.get('colorFromReviewed') and not action.get('conversion',{}).get('colorCorrection'):
            raise ValueError('colorFromReviewed requires a colorCorrection profile')
        if type(action.get('anchorFromReviewed',False)) is not bool or action.get('anchorFromReviewed') and not action.get('conversion',{}).get('anchorCorrection'):
            raise ValueError('anchorFromReviewed requires an anchorCorrection profile')
        if action.get('anchorFromReviewed') and action.get('colorFromReviewed'):
            raise ValueError('Anchor correction must use a reviewed color base in a single geometry pass')
        key = action['key']
        if not re.fullmatch(r'[^\W\d][\w-]{0,63}', key, re.UNICODE) or key.casefold() in seen:
            raise ValueError('Action keys must be safe and unique (case insensitive)')
        seen.add(key.casefold())
        if action['group'] not in ('Idle','ClickInteraction','Walking') or type(action['loop']) is not bool:
            raise ValueError(f'Invalid group or loop for {key}')
        if not isinstance(action['label'], str) or not action['label'].strip() or len(action['label']) > 40 or any(ord(c)<32 or 127<=ord(c)<=159 for c in action['label']):
            raise ValueError(f'Invalid label for {key}')
        options = recipe(config.get('defaults', {}), action)
        frozen = profiles['actions'].get(key)
        if 'anchorCorrection' in options and not (action.get('anchorFromReviewed') or action.get('reviewedFinal')):
            raise ValueError('anchorCorrection requires anchorFromReviewed or an approved final video')
        ratio = options['targetHeight']/max(options['width'], options['height'])
        if not math.isclose(skin.get('StandingHeightRatio', -1), ratio, abs_tol=1e-6):
            raise ValueError('All actions must use the base skin standing-height ratio')
        source = resolve(root, action['source']) if action.get('source') else None
        if source:
            registered.add(source)
        source_hash = file_hash(source) if source and source.is_file() else None
        if frozen:
            if not source or not source.is_file():
                raise ValueError(f'{key}: quality rebuild requires the original MOV')
            validate_rebuild(root, frozen, source_hash, options, file_hash, action.get('calibration'))
        reviewed = action.get('reviewed', {})
        keys(reviewed, {'videoSha256','sourceSha256','recipeSha256'}, 'reviewed')
        base_clip = skin['Clips'].get(key)
        approval_options = dict(options)
        if action.get('colorFromReviewed'):
            approval_options.pop('colorCorrection',None)
        if action.get('anchorFromReviewed'):
            approval_options.pop('anchorCorrection',None)
        approved = bool(reviewed and base_clip and reviewed.get('recipeSha256') == fingerprint(approval_options) and
                        (source is None or source_hash == reviewed.get('sourceSha256')))
        video = inside((base_dir/base_clip['Video']).resolve(), base_dir) if approved else None
        approved = approved and video.is_file() and file_hash(video) == reviewed['videoSha256']
        if approved and action.get('calibration'):
            raise ValueError(f'{key}: manual calibration is only for new candidates; an approved video must not be corrected twice')
        if action.get('colorFromReviewed') and not approved:
            raise ValueError(f'{key}: colorFromReviewed needs a matching reviewed source and unchanged layout recipe')
        if action.get('anchorFromReviewed') and not approved:
            raise ValueError(f'{key}: anchorFromReviewed needs a matching reviewed source and base recipe')
        if action.get('reviewedFinal') and not approved and not frozen:
            raise ValueError(f'{key}: final correction changed; review a separate result before updating the approved video')
        if not approved and (source is None or not source.is_file()):
            raise ValueError(f'{key}: reviewed asset cannot be reused; provide an existing source video')
        if reference and key==reference['policy']['referenceAction'] and not approved and not frozen:
            raise ValueError('The automatic calibration reference must remain reviewed; review a new baseline separately')
        planned.append((action, options, source, source_hash, video if approved else None))
    ignored = config.get('ignoredSources', {})
    if not isinstance(ignored, dict) or any(not isinstance(reason, str) or not reason.strip() for reason in ignored.values()):
        raise ValueError('ignoredSources must map files to reasons')
    ignored_paths = {resolve(root, name) for name in ignored}
    pending = [str(p.relative_to(root)) for p in sorted((root/'animation').glob('*'))
               if p.suffix.lower() in ('.mov','.webm','.mp4') and p.resolve() not in registered | ignored_paths]
    if pending:
        return dict(status='needs_configuration', pendingSources=pending, outputSkin=str(destination),
                    actions=[], message='Add each new source to animation/actions.json before importing'), 2
    tool_hash = fingerprint([digest(Path(__file__)), digest(TOOL_ROOT/'scripts/pet_video_converter.py'),
                             digest(TOOL_ROOT/'scripts/pet_color.py'),
                             digest(TOOL_ROOT/'scripts/pet_anchor.py'),digest(TOOL_ROOT/'scripts/pet_calibration.py'),
                             digest(TOOL_ROOT/'scripts/pet_preview.py'),digest(TOOL_ROOT/'scripts/pet_quality.py'),digest(ffmpeg)])
    cache_root = root/'output/.cache/animation-import'
    cache_root.mkdir(parents=True, exist_ok=True)
    items, published, total_bytes = [], [], 0
    for action, options, source, source_hash, reviewed_video in planned:
        key = action['key']
        frozen = profiles['actions'].get(key)
        processing = ('quality-rebuild' if frozen else 'final-reviewed' if action.get('reviewedFinal') else
                      'anchor-reviewed' if action.get('anchorFromReviewed') else
                      'color-reviewed' if action.get('colorFromReviewed') else
                      'reviewed' if reviewed_video else 'calibrated-convert' if reference else 'convert')
        cache_key = fingerprint(dict(tool=tool_hash, source=source_hash, recipe=options, processing=processing,
                                     calibration=signature(reference,action.get('calibration',{})) if reference and not reviewed_video else None,
                                     reviewedVideo=file_hash(reviewed_video) if reviewed_video else None))
        if frozen:
            cache_key = fingerprint({'cache':cache_key, 'frozen':frozen})
        entry = cache_root/cache_key
        receipt_path = entry/'receipt.json'
        try:
            receipt = read_json(receipt_path) if receipt_path.is_file() else None
            if not isinstance(receipt, dict) or not {'clip','conversion','validation','videoSha256','previewSha256'} <= receipt.keys():
                receipt = None
        except (OSError, ValueError):
            receipt = None
        cached_video, preview = entry/'video.webm', entry/'preview.png'
        hit = bool(receipt and cached_video.is_file() and preview.is_file() and
                   receipt['videoSha256'] == digest(cached_video) and receipt['previewSha256'] == digest(preview))
        if hit and 'calibrationArtifacts' in receipt:
            hit=all((entry/name).is_file() and digest(entry/name)==sha for name,sha in receipt['calibrationArtifacts'].items())
        mode = 'cache'
        if not hit:
            with tempfile.TemporaryDirectory(prefix='.stage-', dir=cache_root) as temporary:
                stage = Path(temporary)
                if frozen:
                    clip, conversion = rebuild(source, stage/'video.webm', frozen, options, ffmpeg)
                    mode = 'rebuilt'
                elif reviewed_video and action.get('anchorFromReviewed'):
                    clip=copy.deepcopy(skin['Clips'][key])
                    geometry=correct_anchor_video(reviewed_video,stage/'video.webm',options['anchorCorrection'],
                        clip['VideoWidth'],clip['VideoHeight'],ffmpeg)
                    conversion=dict(kind='anchor-corrected-reviewed',sourceSha256=source_hash,
                        reviewedVideoSha256=file_hash(reviewed_video),anchorCorrection=options['anchorCorrection'],
                        originalVideo=str(reviewed_video),colorCurvesNotReapplied=True,**geometry)
                    mode='anchor-corrected'
                elif reviewed_video and action.get('colorFromReviewed'):
                    recolor_video(reviewed_video, stage/'video.webm', options['colorCorrection'],ffmpeg)
                    clip = copy.deepcopy(skin['Clips'][key])
                    conversion = dict(kind='color-corrected-reviewed', sourceSha256=source_hash,
                                      reviewedVideoSha256=file_hash(reviewed_video), colorCorrection=options['colorCorrection'],
                                      originalVideo=str(reviewed_video),lossless=True,geometryPreserved=True,
                                      decodedAlphaExactlyPreserved=True)
                    mode = 'color-corrected'
                elif reviewed_video:
                    shutil.copyfile(reviewed_video, stage/'video.webm')
                    clip = copy.deepcopy(skin['Clips'][key])
                    conversion = dict(kind='reviewed', sourceSha256=source_hash, recipe=options,
                                      originalVideo=str(reviewed_video))
                    mode = 'reviewed'
                elif reference:
                    normalized,clip,conversion=normalize_for_calibration(root,source,source_hash,action,options,skin,ffmpeg,report_dir)
                    calibration=calibrate(normalized,stage/'video.webm',reference,options,action.get('calibration',{}),key,report_dir,ffmpeg)
                    if options['frameRange']:
                        trim_video(stage/'video.webm',stage/'segment.webm',options['frameRange'],ffmpeg)
                        (stage/'segment.webm').replace(stage/'video.webm')
                        clip['VideoFrameCount']=options['frameRange'][1]-options['frameRange'][0]
                    if not options['lossless']:
                        # Normalization/color/anchors use lossless intermediates;
                        # the configured CRF is applied exactly once to the final output.
                        run([str(ffmpeg), '-nostdin', '-hide_banner', '-loglevel', 'error', '-y',
                             '-c:v', 'libvpx-vp9', '-i', str(stage/'video.webm'), '-map', '0:v:0',
                             '-an', '-fps_mode', 'passthrough', '-c:v', 'libvpx-vp9', '-pix_fmt', 'yuva420p',
                             '-auto-alt-ref', '0', '-deadline', 'good', '-cpu-used', '2', '-row-mt', '1',
                             '-threads', '2', '-crf', str(options['crf']), '-b:v', '0', str(stage/'final.webm')], timeout=600)
                        (stage/'final.webm').replace(stage/'video.webm')
                    conversion.update(kind='calibrated-convert',calibration=calibration,frameRange=options['frameRange'])
                    conversion.update(recipe=options, crf=None if options['lossless'] else options['crf'], lossless=options['lossless'])
                    mode='calibrated'
                else:
                    # Seed only the mandatory Idle metadata; never alter the live pack during conversion.
                    write_json(stage/'skin.json', skin)
                    result = subprocess.run(converter_arguments(source, stage, action, options, ffmpeg),
                                            capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=600)
                    (report_dir/(key+'.log')).write_text(result.stdout+'\n'+result.stderr, encoding='utf-8')
                    if result.returncode:
                        raise ValueError(f'{key}: conversion failed. See {report_dir/(key+".log")}\n{result.stderr[-1000:]}')
                    clip = read_json(stage/'skin.json')['Clips'][key]
                    generated = stage/clip['Video']
                    conversion = read_json(generated.with_suffix('.conversion.json'))
                    if abs(conversion['actualStandingHeight']-options['targetHeight']) > 2 or abs(conversion['actualGroundY']-options['feetY']) > 1:
                        raise ValueError(f'{key}: rounded standing calibration exceeds tolerance')
                    generated.replace(stage/'video.webm')
                    mode = 'converted'
                validation = verify_video(stage/'video.webm', clip, ffmpeg, stage/'preview.png')
                receipt = dict(clip=clip, conversion=conversion, validation=validation,
                               videoSha256=digest(stage/'video.webm'), previewSha256=digest(stage/'preview.png'))
                entry.mkdir(parents=True, exist_ok=True)
                if mode=='calibrated':
                    names=[key+'.calibration.json',key+'-measurement.png',key+'-comparison.mp4']
                    receipt['calibrationArtifacts']={name:digest(report_dir/name) for name in names}
                    for name in names:publish_file(report_dir/name,entry/name)
                publish_file(stage/'video.webm', cached_video)
                publish_file(stage/'preview.png', preview)
                write_json(receipt_path, receipt)
        elif verify_all:
            receipt['validation'] = verify_video(cached_video, receipt['clip'], ffmpeg, preview)
            receipt['previewSha256'] = digest(preview)
            write_json(receipt_path, receipt)
        for name in receipt.get('calibrationArtifacts',{}):publish_file(entry/name,report_dir/name)
        video_fields = {name: receipt['clip'][name] for name in (
            'VideoWidth', 'VideoHeight', 'VideoFrameCount', 'FrameRate', 'AnchorX', 'AnchorY', 'DisableProceduralMotion')}
        clip = copy.deepcopy(skin['Clips'].get(key, {})) | video_fields
        # Preserve authored per-clip alignment while replacing the video fields.
        clip.update(DisplayName=action['label'], PreviewGroup=action['group'], Loop=action['loop'])
        if 'alignment' in action:
            clip['Alignment'] = action['alignment']
        video_name = key.lower()+'-'+receipt['videoSha256'][:16]+'.webm'
        clip['Video'] = video_name
        skin['Clips'][key] = clip
        published.append((cached_video, video_name))
        publish_file(preview, report_dir/(key+'.png'))
        write_json(report_dir/(key+'.conversion.json'), receipt['conversion'] | {'validation':receipt['validation']})
        items.append(dict(key=key, label=action['label'], group=action['group'], mode=mode,
                          calibrated='calibrationArtifacts' in receipt,
                          video=video_name,canvas=[clip['VideoWidth'],clip['VideoHeight']],
                          crf=receipt['conversion'].get('crf'),bytes=cached_video.stat().st_size,
                          frames=clip['VideoFrameCount'], videoSha256=receipt['videoSha256']))
    if 'Idle' not in skin['Clips']:
        raise ValueError('An Idle action is required')
    # Include any base clips omitted from the action list, preserving the base pack as a whole.
    for key, clip in skin['Clips'].items():
        total_bytes += clip['VideoWidth']*clip['VideoHeight']*clip['VideoFrameCount']*4
        if key.casefold() not in seen:
            original = inside((base_dir/clip['Video']).resolve(), base_dir)
            verify_video(original, clip, ffmpeg, report_dir/(key+'.png'))
            name = key.lower()+'-'+digest(original)[:16]+'.webm'
            clip['Video'] = name; published.append((original, name))
    # Playback buffers only current/pending actions; decodedMiB is informational,
    # not an allocation or an on-disk whole-library cache limit.
    destination.mkdir(parents=True, exist_ok=True)
    for video, name in published:
        publish_file(video, destination/name)
    manifest = destination/'skin.json'
    changed = not manifest.is_file() or read_json(manifest) != skin
    if changed:
        if manifest.is_file():
            publish_file(manifest, destination/'skin.previous.json')
        write_json(manifest, skin)  # Commit last: referenced assets are ready before the player sees them.
    rows = ''.join(f'<article><h2>{html.escape(a["label"])} · {html.escape(a["key"])}</h2>'
                   f'<p>{a["frames"]} 帧 · {a["group"]} · {a["mode"]} · 首帧 / 中间帧 / 末帧</p>'
                   f'<p>{a["canvas"][0]}×{a["canvas"][1]} · CRF {a["crf"] if a["crf"] is not None else "审核源/无损"} · {a["bytes"]/2**20:.3f} MiB</p>'
                   f'<video controls loop muted playsinline preload="metadata" src="{html.escape(Path(os.path.relpath(destination/a["video"],report_dir)).as_posix())}" style="width:640px;max-width:100%"></video>'
                   f'<img src="{a["key"]}.png">'+
                   (f'<p>待机参考 / 基础转换 / 色调与锚点校准 · 完整动作</p><video controls loop src="{a["key"]}-comparison.mp4" style="width:100%;max-width:1152px"></video>'
                    f'<p><a href="{a["key"]}.calibration.json">测量与固定校准参数</a></p><img src="{a["key"]}-measurement.png">' if a['calibrated'] else '')+'</article>' for a in items)
    (report_dir/'preview.html').write_text('<!doctype html><meta charset="utf-8"><title>动作导入预览</title>'
        '<style>body{font:16px system-ui;margin:32px;background:#eee}article{background:white;padding:20px;margin:20px 0}'
        'img{width:100%;max-width:1152px;background:repeating-conic-gradient(#ccc 0% 25%,#fff 0% 50%) 0/20px 20px}</style>'
        '<h1>动作导入预览</h1><p>检查角色大小、脚底位置和特效；实际播放请使用桌宠的动作预览菜单。</p>'+rows, encoding='utf-8')
    return dict(status='ok', outputSkin=str(destination), manifestChanged=changed, actions=items,
                pendingSources=[], decodedMiB=round(total_bytes/1024/1024, 1),
                converted=sum(a['mode'] in ('converted','calibrated','color-corrected','anchor-corrected','rebuilt') for a in items),
                reused=sum(a['mode'] not in ('converted','calibrated','color-corrected','anchor-corrected','rebuilt') for a in items), preview=str(report_dir/'preview.html')), 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=TOOL_ROOT)
    parser.add_argument('--config', default='animation/actions.json')
    parser.add_argument('--ffmpeg', type=Path, default=TOOL_ROOT/'runtime/ffmpeg.exe')
    parser.add_argument('--verify-all', action='store_true', help='Decode all clips again, including cache hits')
    args = parser.parse_args()
    root = args.root.resolve()
    report_dir = root/'output/animation-import'
    report_dir.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    with import_lock(report_dir/'import.lock'):
        try:
            report, code = import_actions(root, resolve(root, args.config), report_dir, args.ffmpeg.resolve(), args.verify_all)
        except NeedsCalibration as exc:
            report,code=dict(status='needs_calibration',action=exc.key,error=str(exc),measurement=exc.measurement,
                             actions=[],message='Inspect the standing measurement; set comparable referenceStart or hash-bound manual anchors'),3
        except (ValueError, OSError, KeyError, TypeError, subprocess.TimeoutExpired) as exc:
            report, code = dict(status='failed', error=str(exc), actions=[]), 1
        report['seconds'] = round(time.monotonic()-started, 2)
        write_json(report_dir/'last-run.json', report)
    # Keep chat output small: details and sample frames live in files.
    summary = {k:v for k,v in report.items() if k not in ('actions',)}
    summary['report'] = str(report_dir/'last-run.json')
    print(json.dumps(summary, ensure_ascii=False))
    return code


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (ValueError, OSError) as exc:
        raise SystemExit(str(exc))
