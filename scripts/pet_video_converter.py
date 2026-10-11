"""Convert transparent MOV to VP9 Alpha WebM and a GPT pet skin (standard library + FFmpeg)."""
import argparse
import hashlib
import json
import math
import re
import statistics
import subprocess
import uuid
from pathlib import Path
from pet_color import validate_profile, write_cube

ROOT = Path(__file__).resolve().parents[1]


def run(command, timeout=180, **kwargs):
    result = subprocess.run(command, capture_output=True, timeout=timeout, **kwargs)
    if result.returncode:
        message = result.stderr.decode('utf-8', errors='replace') if isinstance(result.stderr, bytes) else result.stderr
        raise ValueError('FFmpeg failed: ' + message[-1800:])
    return result


def write_json(path, value):
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def analyze_video(source, ffmpeg, bbox_alpha=64, content_region=None):
    """Return source size and the union of visible pixels across every source frame."""
    decode = ['-c:v', 'libvpx-vp9'] if source.suffix.lower() == '.webm' else []
    region_filter = f'crop={content_region[2]}:{content_region[3]}:{content_region[0]}:{content_region[1]},' if content_region else ''
    scan = run([str(ffmpeg), '-nostdin', '-hide_banner', '-loglevel', 'info'] + decode +
        ['-i', str(source), '-map', '0:v:0', '-vf', f'format=rgba,{region_filter}alphaextract,bbox=min_val={bbox_alpha}',
         '-frames:v', '3601', '-an', '-f', 'null', '-'], text=True, encoding='utf-8', errors='replace')
    dimensions = re.search(r'Stream[^\n]*Video:[^\n]*? (\d+)x(\d+)(?:[ ,])', scan.stderr)
    if not dimensions:
        raise ValueError('Cannot read source dimensions')
    boxes = [tuple(map(int, m)) for m in re.findall(r'x1:(\d+) x2:(\d+) y1:(\d+) y2:(\d+)', scan.stderr)]
    boxes = [b for b in boxes if b[0] <= b[1] and b[2] <= b[3]]
    if not boxes:
        raise ValueError('No visible character pixels found')
    union = (min(b[0] for b in boxes), min(b[2] for b in boxes),
             max(b[1] for b in boxes) + 1, max(b[3] for b in boxes) + 1)
    size = tuple(map(int, dimensions.groups()))
    if content_region:
        x, y, width, height = content_region
        if x < 0 or y < 0 or width < 1 or height < 1 or x + width > size[0] or y + height > size[1]:
            raise ValueError('Content region must be inside the source canvas')
        if union[0] == 0 or union[2] == width:
            raise ValueError('Visible content touches the horizontal content-region edge; enlarge it')
        union = (union[0] + x, union[1] + y, union[2] + x, union[3] + y)
    return size, union


def measure_standing(source, ffmpeg, source_size, bbox_alpha=64, content_region=None, reference_start=None):
    """Measure clean standing poses, normally the first/last five frames, without motion effects."""
    decode = ['-c:v', 'libvpx-vp9'] if source.suffix.lower() == '.webm' else []
    region = content_region or (0, 0, *source_size)
    x, y, width, height = region
    frame_bytes = width * height
    windows = [('head', ['-ss', str(reference_start or 0)])]
    if reference_start is None:
        windows.append(('tail', ['-sseof', '-0.25']))
    samples = []
    for label, seek in windows:
        result = run([str(ffmpeg), '-nostdin', '-hide_banner', '-loglevel', 'error'] + seek + decode +
            ['-i', str(source), '-map', '0:v:0', '-vf',
             f'format=rgba,crop={width}:{height}:{x}:{y},alphaextract',
             '-frames:v', '5', '-pix_fmt', 'gray', '-f', 'rawvideo', 'pipe:1'])
        if len(result.stdout) % frame_bytes:
            raise ValueError('Incomplete standing reference frame')
        for offset in range(0, len(result.stdout), frame_bytes):
            alpha = result.stdout[offset:offset+frame_bytes]
            columns = [0] * width
            top, bottom = height, -1
            for index, value in enumerate(alpha):
                if value >= bbox_alpha:
                    row, column = divmod(index, width)
                    top, bottom = min(top, row), max(bottom, row)
                    columns[column] += 1
            if bottom < top:
                continue
            midpoint, cumulative = sum(columns)/2, 0
            for column, amount in enumerate(columns):
                cumulative += amount
                if cumulative >= midpoint:
                    center = x + column
                    break
            samples.append({'window': label, 'height': bottom-top+1, 'feetY': y+bottom,
                            'centerX': center, 'topY': y+top})
    if not samples:
        raise ValueError('No visible standing reference; use --reference-start with a clean standing moment')
    if reference_start is None:
        heads = [s['height'] for s in samples if s['window'] == 'head']
        tails = [s['height'] for s in samples if s['window'] == 'tail']
        if not heads or not tails or abs(statistics.mean(heads)-statistics.mean(tails))/statistics.mean(heads+tails) > 0.15:
            raise ValueError('Head/tail standing heights differ by more than 15%; use --reference-start')
    return {'height': statistics.mean(s['height'] for s in samples),
            'feetY': statistics.mean(s['feetY'] for s in samples),
            'centerX': statistics.mean(s['centerX'] for s in samples),
            'samples': samples, 'referenceStart': reference_start}


def safe_standing_height(crop, standing, canvas, margin, feet_y):
    """Maximum standing height that fits the full action; batch callers use the minimum for the library."""
    x, y, width, height = crop
    canvas_width, canvas_height = canvas
    distances = ((standing['centerX']-x, canvas_width/2-canvas_width*margin),
                 (x+width-standing['centerX'], canvas_width/2-canvas_width*margin),
                 (standing['feetY']-y, feet_y-canvas_height*margin),
                 (y+height-standing['feetY'], canvas_height*(1-margin)-feet_y))
    limits = [space/distance for distance, space in distances if distance > 0]
    return min(limits) * standing['height']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('input', type=Path)
    parser.add_argument('--output', type=Path, required=True, help='Custom skin folder; do not use the built-in skin')
    parser.add_argument('--state', default='Idle', help='Built-in state or custom action key, e.g. PlayBall')
    parser.add_argument('--action-label', help='Custom action name shown in the preview menu')
    parser.add_argument('--preview-group', choices=['Idle', 'ClickInteraction', 'Walking'], help='Action preview submenu')
    parser.add_argument('--start-frame', type=int, default=0, help='First output-fps frame, zero based; normalization still uses the full source')
    parser.add_argument('--end-frame', type=int, help='Exclusive last output-fps frame; encode the segment directly from the original source')
    parser.add_argument('--name', help='Name of a newly created skin')
    parser.add_argument('--once', action='store_true')
    parser.add_argument('--fps', type=int, default=24)
    parser.add_argument('--width', type=int, default=640)
    parser.add_argument('--height', type=int, default=640)
    parser.add_argument('--crf', type=int, default=40, help='VP9 quality: 0..63; smaller is clearer and larger (default: 40, like dsh-pet)')
    parser.add_argument('--lossless', action='store_true', help='Use VP9 lossless instead of CRF compression')
    parser.add_argument('--margin', type=float, default=0.08, help='Clear border as fraction of the canvas (default: 0.08)')
    parser.add_argument('--bbox-alpha', type=int, default=64, help='Alpha threshold for content bounds; ignores faint matte noise (default: 64)')
    parser.add_argument('--keep-canvas', action='store_true', help='Keep the source canvas instead of cropping transparent blank space')
    parser.add_argument('--crop', help='Shared source crop x,y,width,height; use the same crop for matching actions')
    parser.add_argument('--content-region', help='Explicit character region x,y,width,height; excludes source corner overlays from bounds')
    parser.add_argument('--normalization', choices=['standing', 'bounds'], default='standing', help='Use standing body height (default) or legacy full-action bounds')
    parser.add_argument('--target-height', type=float, help='Standing height in output pixels; same value for all matching actions (default: 209/384 of canvas height)')
    parser.add_argument('--feet-y', type=float, help='Standing ground line in output pixels (default: 90%% of canvas height)')
    parser.add_argument('--reference-start', type=float, help='Measure five clean standing frames at this source time instead of the head/tail frames')
    parser.add_argument('--color-profile', type=Path, help='Optional fixed RGB curves JSON; preserve alpha and protect saturated colors')
    parser.add_argument('--ffmpeg', type=Path, default=ROOT/'runtime/ffmpeg.exe')
    args = parser.parse_args()
    if args.start_frame < 0 or (args.end_frame is not None and args.end_frame <= args.start_frame):
        parser.error('Frame range must be nonnegative with end greater than start')
    if not 1 <= len(args.state) <= 64 or args.state[0].isdigit() or not all(c.isalnum() or c in '_-' for c in args.state):
        parser.error('State must be a safe action key (1..64 letters, digits, underscore or hyphen; not starting with a digit)')
    known = ['Idle','Walking','LyingDown','Sleeping','Waking','Happy','Thinking','Talking','Surprised','Dragging']
    args.state = next((key for key in known if key.casefold() == args.state.casefold()), args.state)
    if args.action_label is not None and (not args.action_label.strip() or len(args.action_label) > 40 or any(ord(c) < 32 or 127 <= ord(c) <= 159 for c in args.action_label)):
        parser.error('Action label must contain 1..40 visible characters')
    if not 1 <= args.fps <= 60 or any(n < 16 or n > 640 or n % 2 for n in [args.width,args.height]):
        parser.error('fps: 1..60; canvas dimensions: even integers 16..640')
    if not 0 <= args.crf <= 63 or not 0 <= args.margin <= 0.2 or not 1 <= args.bbox_alpha <= 255:
        parser.error('crf: 0..63; margin: 0..0.2; bbox-alpha: 1..255')
    if args.crop and args.keep_canvas:
        parser.error('--crop and --keep-canvas cannot be used together')
    for value in (args.target_height, args.feet_y, args.reference_start):
        if value is not None and (not math.isfinite(value) or value < 0):
            parser.error('Standing height, ground line and reference time must be finite and nonnegative')
    source, destination = args.input.resolve(), args.output.resolve()
    args.ffmpeg = args.ffmpeg.resolve()
    color_profile = validate_profile(json.loads(args.color_profile.read_text(encoding='utf-8-sig'))) if args.color_profile else None
    if not source.is_file() or not args.ffmpeg.is_file():
        parser.error('Source video or FFmpeg does not exist')
    if destination.parent == (ROOT/'assets').resolve() and destination.name.startswith('dragon-girl'):
        parser.error('Choose a separate custom skin folder')
    manifest = destination/'skin.json'
    skin = json.loads(manifest.read_text(encoding='utf-8-sig')) if manifest.exists() else {
        'Name': args.name or destination.name, 'DisplayScale': 0.96, 'Clips': {}}
    if args.state != 'Idle' and 'Idle' not in skin.get('Clips', {}):
        parser.error('Import an Idle clip first, then add other states to the same folder')
    base = [str(args.ffmpeg), '-nostdin', '-hide_banner', '-loglevel', 'error']
    decode = ['-c:v', 'libvpx-vp9'] if source.suffix.lower() == '.webm' else []
    # Check source transparency before adding transparent padding.
    probe = run(base+decode+['-i',str(source),'-map','0:v:0','-frames:v','1',
        '-vf','scale=384:-2','-pix_fmt','bgra','-f','rawvideo','pipe:1'], timeout=30)
    if not probe.stdout or not any(alpha < 12 for alpha in probe.stdout[3::4]):
        raise ValueError('Source has no transparent background. Export ProRes 4444 with Alpha first.')
    # One crop from the union of ALL frames prevents changing size and cutting wings/tails.
    content_region = None
    if args.content_region:
        try:
            content_region = tuple(map(int, args.content_region.split(',')))
            if len(content_region) != 4:
                raise ValueError()
        except ValueError:
            parser.error('--content-region must be x,y,width,height')
    (source_width, source_height), union = analyze_video(source, args.ffmpeg, args.bbox_alpha, content_region)
    if args.crop:
        try:
            x,y,crop_width,crop_height = map(int, args.crop.split(','))
        except ValueError:
            parser.error('--crop must be x,y,width,height')
    elif args.keep_canvas:
        x,y,crop_width,crop_height = 0,0,source_width,source_height
    else:
        x,y = max(0,union[0]-8), max(0,union[1]-8)
        crop_width, crop_height = min(source_width,union[2]+8)-x, min(source_height,union[3]+8)-y
    if x < 0 or y < 0 or crop_width < 1 or crop_height < 1 or x+crop_width > source_width or y+crop_height > source_height:
        parser.error('Crop must be inside the source canvas')
    if union[0] < x or union[1] < y or union[2] > x+crop_width or union[3] > y+crop_height:
        parser.error('Crop would cut visible animation content')
    margin_x, margin_y = round(args.width*args.margin), round(args.height*args.margin)
    standing = None
    if args.normalization == 'standing':
        standing = measure_standing(source, args.ffmpeg, (source_width,source_height), args.bbox_alpha, content_region, args.reference_start)
        target_height = args.target_height if args.target_height is not None else args.height * (209/384)
        feet_y = args.feet_y if args.feet_y is not None else args.height * 0.9
        if target_height <= 0 or not margin_y < feet_y < args.height-margin_y:
            parser.error('Standing height must be positive; feet-y must lie inside the safe canvas')
        safe_height = safe_standing_height((x,y,crop_width,crop_height), standing, (args.width,args.height), args.margin, feet_y)
        if target_height > safe_height:
            parser.error(f'Full action does not fit the requested standing height. Use a shared height <= {safe_height:.2f} for the whole library')
        ratio = target_height / standing['height']
        standing_ratio = target_height / max(args.width, args.height)
        if not 0.1 <= standing_ratio <= 1:
            parser.error('Standing height must be 10% to 100% of the larger canvas dimension')
        others = {state: clip for state, clip in skin.get('Clips', {}).items() if state != args.state}
        if others and (skin.get('StandingHeightRatio') is None or not math.isclose(skin['StandingHeightRatio'], standing_ratio, abs_tol=1e-6)):
            parser.error('All actions must share StandingHeightRatio; rebuild uncalibrated legacy packs in a new folder')
        scaled_width = max(2, round(crop_width*ratio/2)*2)
        scaled_height = max(2, round(crop_height*ratio/2)*2)
        left = round(args.width/2-(standing['centerX']-x)*scaled_width/crop_width)
        top = round(feet_y-(standing['feetY']-y)*scaled_height/crop_height)
    else:
        if skin.get('StandingHeightRatio') is not None and any(state != args.state for state in skin.get('Clips', {})):
            parser.error('Cannot mix legacy bounds sizing with calibrated standing actions')
        ratio = min((args.width-2*margin_x)/crop_width, (args.height-2*margin_y)/crop_height)
        scaled_width = max(2, int(crop_width*ratio)//2*2)
        scaled_height = max(2, int(crop_height*ratio)//2*2)
        left = (args.width-scaled_width)//2
        top = args.height-margin_y-scaled_height
        target_height = None
        feet_y = args.height-margin_y
    if left < 0 or top < 0 or left+scaled_width > args.width or top+scaled_height > args.height:
        parser.error('Rounded placement would crop the action; reduce the shared standing height slightly')
    # Scale RGB and alpha together in premultiplied form to avoid dark matte edges.
    filters = (f'fps={args.fps},format=rgba,crop={crop_width}:{crop_height}:{x}:{y},'
        f'format=gbrap,premultiply=inplace=1,scale={scaled_width}:{scaled_height}:flags=lanczos,'
        f'unpremultiply=inplace=1,format=rgba,'
        f'pad={args.width}:{args.height}:{left}:{top}:color=black@0,setsar=1')
    if args.start_frame or args.end_frame is not None:
        filters += f',trim=start_frame={args.start_frame}'
        if args.end_frame is not None:
            filters += f':end_frame={args.end_frame}'
        filters += ',setpts=PTS-STARTPTS'
    destination.mkdir(parents=True,exist_ok=True)
    name = f'{args.state.lower()}-{uuid.uuid4().hex[:10]}.webm'
    target = destination/name
    report_path = destination/(target.stem+'.conversion.json')
    cube_path = destination/(target.stem+'.cube')
    try:
        if color_profile:
            write_cube(cube_path, color_profile)
            filters += f',format=gbrap,lut3d=file={cube_path.name}:interp=tetrahedral,format=rgba'
        compression = ['-lossless','1'] if args.lossless else ['-crf',str(args.crf),'-b:v','0']
        encoded = run(base+decode+['-i',str(source),'-map','0:v:0','-vf',filters,'-an','-frames:v','3601',
            '-c:v','libvpx-vp9','-pix_fmt','yuva420p','-auto-alt-ref','0','-deadline','good',
            '-cpu-used','2','-row-mt','1','-threads','4']+compression+['-progress','pipe:1',str(target)],
            text=True, encoding='utf-8', errors='replace', cwd=destination)
        counts = re.findall(r'^frame=\s*(\d+)\s*$',encoded.stdout,re.MULTILINE)
        count = int(counts[-1]) if counts else 0
        if args.end_frame is not None and count != args.end_frame - args.start_frame:
            raise ValueError('Requested frame range exceeds the source; segment length differs')
        if not 1 <= count <= 3600:
            raise ValueError('Clip exceeds the supported 3600-frame limit')
        if not 0 < target.stat().st_size <= 64*1024*1024:
            raise ValueError('WebM exceeds the 64 MiB source limit')
        # Verify through the same VP9 decoder used by our WPF player.
        verify = subprocess.Popen(base+['-c:v','libvpx-vp9','-i',str(target),'-map','0:v:0',
            '-fps_mode','passthrough','-pix_fmt','bgra','-an','-f','rawvideo','pipe:1'],
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
        try:
            frame_bytes = args.width*args.height*4
            first = verify.stdout.read(frame_bytes)
            if len(first) != frame_bytes or not any(a < 12 for a in first[3::4]) or not any(a > 200 for a in first[3::4]):
                raise ValueError('Encoded video lost transparency or the character')
            total = len(first)
            while block := verify.stdout.read(1024*1024):
                total += len(block)
            if verify.wait(timeout=30) or total != count*frame_bytes:
                raise ValueError('Encoded video frame count differs from the manifest')
        finally:
            verify.stdout.close()
            if verify.poll() is None:
                verify.kill(); verify.wait()
        with source.open('rb') as stream:
            digest = hashlib.file_digest(stream,'sha256').hexdigest()
        report = {'source':str(source),'sourceSha256':digest,'sourceBytes':source.stat().st_size,
            'sourceDimensions':[source_width,source_height],'visibleUnion':list(union),'bboxAlpha':args.bbox_alpha,
            'crop':[x,y,crop_width,crop_height],'contentRegion':content_region,'canvas':[args.width,args.height],
            'scaledContent':[scaled_width,scaled_height],'padding':[left,top],
            'normalization':args.normalization,'standing':standing,'targetStandingHeight':target_height,
            'groundY':feet_y,'scale':ratio,
            'actualStandingHeight':standing['height']*scaled_height/crop_height if standing else None,
            'actualGroundY':top+(standing['feetY']-y)*scaled_height/crop_height if standing else feet_y,
            'codec':'VP9 Alpha','crf':None if args.lossless else args.crf,'lossless':args.lossless,
            'premultipliedAlphaScaling':True,'state':args.state,'displayName':args.action_label,
            'colorCorrection':color_profile,
            'previewGroup':args.preview_group or ('Idle' if args.state == 'Idle' else 'ClickInteraction'),
            'frameRate':args.fps,'frameCount':count,'seconds':count/args.fps,
            'frameRange':[args.start_frame,args.end_frame],
            'video':name,'outputBytes':target.stat().st_size,
            'compressionRatio':round(source.stat().st_size/target.stat().st_size,2),
            'alphaVerified':True,'decodedFrameCountVerified':True}
        write_json(report_path,report)
        skin.setdefault('Clips',{})[args.state] = {'Video':name,'VideoWidth':args.width,'VideoHeight':args.height,
            'VideoFrameCount':count,'FrameRate':args.fps,'Loop':not args.once,
            'AnchorX':0.5,'AnchorY':feet_y/args.height,'DisableProceduralMotion':True}
        if args.action_label:
            skin['Clips'][args.state]['DisplayName'] = args.action_label
        skin['Clips'][args.state]['PreviewGroup'] = report['previewGroup']
        if args.normalization == 'standing':
            skin['StandingHeightRatio'] = standing_ratio
        else:
            skin.pop('StandingHeightRatio', None)
        write_json(manifest,skin)
    except BaseException:
        target.unlink(missing_ok=True); report_path.unlink(missing_ok=True)
        raise
    finally:
        cube_path.unlink(missing_ok=True)
    print(json.dumps(report,ensure_ascii=False,indent=2))
    print(f'Skin ready: {manifest}')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, OSError, subprocess.TimeoutExpired) as exc:
        raise SystemExit(str(exc))
