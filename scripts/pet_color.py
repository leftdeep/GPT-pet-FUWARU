"""Fixed RGB correction with chroma protection; no spatial or alpha changes."""
import math
import hashlib
import subprocess
import tempfile
from pathlib import Path


def validate_profile(profile):
    allowed = {'version', 'curves', 'strength', 'chromaProtection'}
    if not isinstance(profile, dict) or profile.keys()-allowed or profile.get('version') != 1:
        raise ValueError('Color profile must use version 1 and known fields')
    curves = profile.get('curves')
    if not isinstance(curves, dict) or set(curves) != {'r', 'g', 'b'}:
        raise ValueError('Color profile requires r/g/b curves')
    for channel, points in curves.items():
        if not isinstance(points, list) or not 2 <= len(points) <= 16:
            raise ValueError(f'Invalid {channel} curve')
        previous = [-1, -1]
        for point in points:
            if not isinstance(point, list) or len(point) != 2 or any(
                    type(n) not in (int, float) or not math.isfinite(n) or not 0 <= n <= 255 for n in point):
                raise ValueError('Curve points must contain finite RGB values in 0..255')
            if point[0] <= previous[0] or point[1] < previous[1]:
                raise ValueError('Color curves must be monotonic')
            previous = point
        if points[0] != [0, 0] or points[-1] != [255, 255]:
            raise ValueError('Color curves must preserve black and white endpoints')
    strength = profile.get('strength', 1)
    chroma = profile.get('chromaProtection', [40, 85])
    if type(strength) not in (int, float) or not math.isfinite(strength) or not 0 <= strength <= 1:
        raise ValueError('Color strength must be finite and in 0..1')
    if not isinstance(chroma, list) or len(chroma) != 2 or any(
            type(n) not in (int, float) or not math.isfinite(n) for n in chroma) or not 0 <= chroma[0] < chroma[1] <= 255:
        raise ValueError('Chroma protection must be two increasing values in 0..255')
    return {'version':1, 'curves':curves, 'strength':strength, 'chromaProtection':chroma}


def curve_value(value, points):
    for left, right in zip(points, points[1:]):
        if value <= right[0]:
            t = (value-left[0])/(right[0]-left[0])
            return left[1]+t*(right[1]-left[1])
    return points[-1][1]


def transform_rgb(rgb, profile):
    low, high = profile['chromaProtection']
    chroma = max(rgb)-min(rgb)
    amount = min(1, max(0, (high-chroma)/(high-low)))
    # Smooth falloff prevents a color discontinuity at the protection boundary.
    weight = profile['strength'] * amount**2 * (3-2*amount)
    return tuple(value + weight*(curve_value(value, profile['curves'][channel])-value)
                 for channel, value in zip('rgb', rgb))


def write_cube(path, profile, size=33):
    profile = validate_profile(profile)
    if type(size) is not int or not 2 <= size <= 65:
        raise ValueError('LUT size must be 2..65')
    with path.open('w', encoding='ascii', newline='\n') as stream:
        stream.write(f'TITLE "Pet color match"\nLUT_3D_SIZE {size}\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n')
        for blue in range(size):
            for green in range(size):
                for red in range(size):  # .cube format: red index changes fastest.
                    rgb = (red*255/(size-1), green*255/(size-1), blue*255/(size-1))
                    corrected = transform_rgb(rgb, profile)
                    stream.write(' '.join(f'{v/255:.8f}' for v in corrected)+'\n')


def recolor_video(source, destination, profile, ffmpeg):
    """Correct an already calibrated WebM; preserve frame geometry and alpha.

    VP9 lossless prevents a second lossy quantization of the reviewed footage.
    RGB/YUV chroma sampling still applies; callers verify the decoded result.
    """
    destination.parent.mkdir(parents=True,exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.color-',dir=destination.parent) as folder:
        working=Path(folder)
        write_cube(working/'color.cube',profile)
        command=[str(ffmpeg.resolve()),'-nostdin','-hide_banner','-loglevel','error','-y',
                 '-c:v','libvpx-vp9','-i',str(source.resolve()),'-map','0:v:0','-an',
                 '-vf','format=gbrap,lut3d=file=color.cube:interp=tetrahedral',
                 '-fps_mode','passthrough','-c:v','libvpx-vp9','-pix_fmt','yuva420p',
                 '-lossless','1','-auto-alt-ref','0','-deadline','good','-cpu-used','2',
                 '-row-mt','1','-threads','4',str(destination.resolve())]
        result=subprocess.run(command,capture_output=True,cwd=working,timeout=180)
        if result.returncode:
            destination.unlink(missing_ok=True)
            raise ValueError('Color correction failed: '+result.stderr.decode('utf-8',errors='replace')[-1200:])
        alpha_checks=[]
        for path in (source,destination):
            decoded=subprocess.run([str(ffmpeg.resolve()),'-nostdin','-hide_banner','-loglevel','error',
                '-c:v','libvpx-vp9','-i',str(path.resolve()),'-vf','alphaextract','-fps_mode','passthrough',
                '-pix_fmt','gray','-an','-f','rawvideo','pipe:1'],capture_output=True,timeout=90)
            if decoded.returncode or not decoded.stdout:
                destination.unlink(missing_ok=True)
                raise ValueError('Cannot verify corrected alpha')
            alpha_checks.append((len(decoded.stdout),hashlib.sha256(decoded.stdout).hexdigest()))
        if alpha_checks[0]!=alpha_checks[1]:
            destination.unlink(missing_ok=True)
            raise ValueError('Color correction changed decoded alpha')
