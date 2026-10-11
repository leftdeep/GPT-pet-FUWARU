"""Regression: hearts and jumps must not change the standing size or the ground line."""
import json
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
FFMPEG = ROOT/'runtime/ffmpeg.exe'


def make_source(path, jump=False, tail_effect=False):
    frames = []
    for n in range(32):
        raw = bytearray(64*64*4)
        x,y,w,h = (24,32,16,28) if jump else (28,42,8,14)
        if jump and 8 <= n < 24:
            y = 16
        for row in range(y,y+h):
            for col in range(x,x+w):
                raw[(row*64+col)*4:(row*64+col)*4+4] = bytes((235,210,250,255))
        if jump and (8 <= n < 24 or tail_effect and n >= 24):
            for row in range(4,10):
                for col in range(29,35):
                    raw[(row*64+col)*4:(row*64+col)*4+4] = bytes((250,60,100,255))
        frames.append(raw)
    result = subprocess.run([str(FFMPEG),'-nostdin','-hide_banner','-loglevel','error',
        '-f','rawvideo','-pix_fmt','rgba','-s','64x64','-r','24','-i','pipe:0',
        '-c:v','prores_ks','-profile:v','4','-pix_fmt','yuva444p10le',str(path)],
        input=b''.join(frames),capture_output=True)
    assert result.returncode == 0, result.stderr


def main():
    with tempfile.TemporaryDirectory(prefix='standing-size-test-') as folder:
        work=Path(folder); output=work/'skin'
        idle,jump,dirty = work/'idle.mov',work/'jump.mov',work/'dirty-tail.mov'
        make_source(idle); make_source(jump,True); make_source(dirty,True,True)
        def convert(source,state,*extra):
            return subprocess.run([sys.executable,str(ROOT/'scripts/import-alpha-video.py'),str(source),
                '--output',str(output),'--state',state,'--width','128','--height','128',
                '--target-height','20','--feet-y','96',*extra],capture_output=True,text=True,encoding='utf-8',errors='replace')
        for source,state in ((idle,'Idle'),(jump,'Happy')):
            result=convert(source,state)
            assert result.returncode == 0,result.stderr
        skin=json.loads((output/'skin.json').read_text(encoding='utf-8'))
        assert skin['StandingHeightRatio'] == 20/128
        reports=[]
        for state in ('Idle','Happy'):
            video=output/skin['Clips'][state]['Video']
            reports.append(json.loads(video.with_suffix('.conversion.json').read_text(encoding='utf-8')))
        assert [r['standing']['height'] for r in reports] == [14,28]
        assert all(r['targetStandingHeight']==20 and r['normalization']=='standing' for r in reports)
        assert max(r['actualStandingHeight'] for r in reports)-min(r['actualStandingHeight'] for r in reports) < 2
        assert max(r['actualGroundY'] for r in reports)-min(r['actualGroundY'] for r in reports) < 1
        assert reports[1]['visibleUnion'][1] < reports[1]['standing']['samples'][0]['topY']
        print('PASS: different source body sizes normalize equally despite jumping and a heart')
        before=(output/'skin.json').read_bytes()
        result=convert(dirty,'Happy')
        assert result.returncode != 0 and (output/'skin.json').read_bytes()==before
        print('PASS: residual tail effects cannot silently become the standing-height reference')
        result=convert(dirty,'Happy','--reference-start','0')
        assert result.returncode == 0,result.stderr
        skin=json.loads((output/'skin.json').read_text(encoding='utf-8'))
        report=json.loads((output/skin['Clips']['Happy']['Video']).with_suffix('.conversion.json').read_text(encoding='utf-8'))
        assert report['standing']['height']==28 and report['targetStandingHeight']==20
        print('PASS: selecting a clean standing reference handles lingering effects correctly')
        before=(output/'skin.json').read_bytes()
        result=convert(jump,'Happy','--target-height','45')
        assert result.returncode != 0 and (output/'skin.json').read_bytes()==before
        print('PASS: oversized actions are rejected rather than secretly shrinking just that action')
        result=convert(jump,'Happy','--target-height','18')
        assert result.returncode != 0 and (output/'skin.json').read_bytes()==before
        print('PASS: importing a mismatched standing scale cannot corrupt a calibrated pack')


if __name__=='__main__':
    main()
