"""Compare the release decoder's full alpha output with the existing decoder."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile


def decode(executable, video, clip):
    width, height = clip['VideoWidth'], clip['VideoHeight']
    ratio = min(1, 640 / max(width, height))
    width, height = int(width * ratio), int(height * ratio)
    frame_bytes = width * height * 4
    command = [str(executable), '-nostdin', '-hide_banner', '-loglevel', 'error',
               '-threads', '1', '-c:v', 'libvpx-vp9', '-i', str(video),
               '-map', '0:v:0', '-vf', f'scale={width}:{height}',
               '-frames:v', str(clip['VideoFrameCount'] + 1), '-fps_mode', 'passthrough',
               '-pix_fmt', 'bgra', '-an', '-f', 'rawvideo', 'pipe:1']
    digest = hashlib.sha256()
    total = 0
    first = bytearray()
    with tempfile.TemporaryFile() as errors:
        process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=errors)
        try:
            while chunk := process.stdout.read(65536):
                digest.update(chunk)
                total += len(chunk)
                if len(first) < frame_bytes:
                    first.extend(chunk[:frame_bytes - len(first)])
            code = process.wait(timeout=30)
            errors.seek(0)
            if code:
                raise RuntimeError(errors.read(4000).decode('utf8', errors='replace'))
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
            process.stdout.close()
    if total != clip['VideoFrameCount'] * frame_bytes:
        raise AssertionError(f'{video.name}: unexpected decoded frame count ({total} bytes)')
    if not any(alpha < 12 for alpha in first[3::4]) or not any(alpha > 200 for alpha in first[3::4]):
        raise AssertionError(f'{video.name}: missing transparent background or visible character')
    return digest.hexdigest(), total


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--decoder', type=Path, required=True)
    parser.add_argument('--baseline', type=Path, required=True)
    parser.add_argument('--report', type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    results = []
    for folder in ['assets/dragon-girl-0.40', 'assets/imported/dragon-girl-calibrated']:
        directory = root / folder
        manifest = json.loads((directory / 'skin.json').read_text(encoding='utf-8-sig'))
        for action, clip in manifest['Clips'].items():
            if 'Video' not in clip:
                continue
            source = directory / clip['Video']
            reference = decode(args.baseline.resolve(), source, clip)
            candidate = decode(args.decoder.resolve(), source, clip)
            if reference != candidate:
                raise AssertionError(f'{action}: release decoder changed decoded pixels')
            results.append({'action': action, 'video': str(source.relative_to(root)),
                            'frames': clip['VideoFrameCount'], 'sha256': candidate[0],
                            'identical': True})
            print(f'{action}: {clip["VideoFrameCount"]} alpha frames identical', flush=True)
    if len(results) != 19:
        raise AssertionError('Expected all 13 active segments and 6 fallback segments')
    report = {'passed': True, 'videos': len(results), 'results': results}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf8')
    print('All 19 videos preserve every decoded pixel.')


if __name__ == '__main__':
    main()
