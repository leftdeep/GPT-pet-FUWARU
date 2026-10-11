"""Integration checks for transparent-video import; run with Python 3.11+."""
import json
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FFMPEG = ROOT/'runtime/ffmpeg.exe'


def main():
    with tempfile.TemporaryDirectory(prefix='pet-converter-test-') as folder:
        working = Path(folder)
        frames = []
        for offset in (8,24):
            raw = bytearray(64*64*4)
            for y in range(12,40):
                for x in range(offset,offset+16):
                    raw[(y*64+x)*4:(y*64+x)*4+4] = bytes((240,220,250,255))
            frames.append(raw)
        # A corner overlay must not shrink the character when an explicit content region is used.
        frames[0][(1*64+62)*4:(1*64+62)*4+4] = bytes((240,220,250,255))
        source = working/'透明样例.mov'
        encode = subprocess.run([str(FFMPEG),'-nostdin','-hide_banner','-loglevel','error',
            '-f','rawvideo','-pix_fmt','rgba','-s','64x64','-r','24','-i','pipe:0',
            '-c:v','prores_ks','-profile:v','4','-pix_fmt','yuva444p10le',str(source)],
            input=b''.join(frames),capture_output=True)
        assert encode.returncode == 0, encode.stderr
        output = working/'skin'
        def convert(path, *extra):
            return subprocess.run([sys.executable,str(ROOT/'scripts/import-alpha-video.py'),str(path),
                '--output',str(output),'--width','64','--height','64','--normalization','bounds',*extra],
                capture_output=True,text=True,encoding='utf-8',errors='replace')
        imported = convert(source)
        assert imported.returncode == 0, imported.stderr
        manifest = json.loads((output/'skin.json').read_text(encoding='utf-8'))
        clip = manifest['Clips']['Idle']
        assert clip['VideoFrameCount'] == 2 and clip['FrameRate'] == 24 and clip['Loop']
        report = json.loads((output/(Path(clip['Video']).stem+'.conversion.json')).read_text(encoding='utf-8'))
        bounds = report['visibleUnion']
        assert bounds[0] <= 8 and bounds[2] >= 40, 'Both moving poses must fit inside the shared crop'
        assert report['alphaVerified'] and report['decodedFrameCountVerified'] and report['crf'] == 40
        print('PASS: alpha, native frame timing, CRF output, and crop union across moving frames')
        once = convert(source,'--state','Happy','--once','--lossless')
        assert once.returncode == 0, once.stderr
        updated = json.loads((output/'skin.json').read_text(encoding='utf-8'))
        assert not updated['Clips']['Happy']['Loop'] and updated['Clips']['Idle'] == clip
        print('PASS: one-shot and lossless options preserve other actions')
        custom = convert(source, '--state', 'PlayBall', '--action-label', '玩球', '--preview-group', 'ClickInteraction', '--once')
        assert custom.returncode == 0, custom.stderr
        updated = json.loads((output/'skin.json').read_text(encoding='utf-8'))
        assert updated['Clips']['PlayBall']['DisplayName'] == '玩球' and not updated['Clips']['PlayBall']['Loop']
        assert updated['Clips']['PlayBall']['PreviewGroup'] == 'ClickInteraction'
        assert updated['Clips']['Idle'] == clip
        print('PASS: custom actions retain their menu label and existing animations')
        look = convert(source, '--state', 'IdleLook', '--action-label', '待机张望', '--preview-group', 'Idle',
            '--content-region', '4,4,52,48')
        assert look.returncode == 0, look.stderr
        updated = json.loads((output/'skin.json').read_text(encoding='utf-8'))
        look_clip = updated['Clips']['IdleLook']
        assert look_clip['PreviewGroup'] == 'Idle' and look_clip['Loop'] and updated['Clips']['Idle'] == clip
        look_report = json.loads((output/(Path(look_clip['Video']).stem+'.conversion.json')).read_text(encoding='utf-8'))
        assert look_report['visibleUnion'][2] == 40 and look_report['premultipliedAlphaScaling']
        assert look_report['crop'][0] + look_report['crop'][2] < 62
        print('PASS: alternate idle grouping and explicit region preserve the character while excluding the corner overlay')
        before = (output/'skin.json').read_bytes()
        unsafe = convert(source, '--state', '../PlayBall')
        assert unsafe.returncode != 0 and (output/'skin.json').read_bytes() == before
        print('PASS: unsafe custom names cannot escape the animation directory')
        before = (output/'skin.json').read_bytes()
        cut = convert(source,'--crop','24,12,16,28')
        assert cut.returncode != 0 and (output/'skin.json').read_bytes() == before
        print('PASS: a crop cutting the animation is rejected without replacing the skin')
        opaque = working/'opaque.mov'
        opaque_pixels = bytes((220,50,60,255))*64*64
        encode = subprocess.run([str(FFMPEG),'-nostdin','-hide_banner','-loglevel','error',
            '-f','rawvideo','-pix_fmt','rgba','-s','64x64','-r','24','-i','pipe:0',
            '-c:v','prores_ks','-profile:v','4','-pix_fmt','yuva444p10le',str(opaque)],
            input=opaque_pixels,capture_output=True)
        assert encode.returncode == 0, encode.stderr
        rejected = convert(opaque)
        assert rejected.returncode != 0 and (output/'skin.json').read_bytes() == before
        print('PASS: opaque MOV is rejected before transparent padding can disguise it')


if __name__ == '__main__':
    main()
