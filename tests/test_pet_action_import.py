"""End-to-end batch-import checks, using small synthetic transparent MOVs."""
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT/'scripts'))
from import_pet_actions import digest, fingerprint, recipe

FFMPEG = ROOT/'runtime/ffmpeg.exe'
ENV = os.environ | {'PYTHONIOENCODING':'utf-8'}


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')


def load(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def movie(path, frames=12, opaque=False, color=80):
    raw = []
    for frame in range(frames):
        image = bytearray(bytes((0, 0, 0, 255 if opaque else 0))*64*64)
        for y in range(16, 40):
            for x in range(24, 40):
                image[(y*64+x)*4:(y*64+x+1)*4] = bytes((color+frame*5, 180, 220, 255))
        raw.append(image)
    result = subprocess.run([str(FFMPEG), '-nostdin','-hide_banner','-loglevel','error',
        '-f','rawvideo','-pix_fmt','rgba','-s','64x64','-r','24','-i','pipe:0',
        '-c:v','prores_ks','-profile:v','4','-pix_fmt','yuva444p10le','-y',str(path)],
        input=b''.join(raw), capture_output=True)
    assert result.returncode == 0, result.stderr


def main():
    with tempfile.TemporaryDirectory(prefix='pet-batch-test-') as folder:
        root = Path(folder)
        (root/'animation').mkdir()
        base = root/'assets/base'
        base.mkdir(parents=True)
        source = root/'animation/standing.mov'
        movie(source)
        result = subprocess.run([sys.executable,str(ROOT/'scripts/import-alpha-video.py'),str(source),
            '--output',str(base),'--width','64','--height','64','--target-height','24','--feet-y','48',
            '--reference-start','0','--crf','24'],capture_output=True,env=ENV)
        assert result.returncode == 0, result.stderr
        original = load(base/'skin.json')
        original['Clips']['Idle']['Alignment'] = {'Scale':.99, 'OffsetX':.001}
        original['Transitions'] = {'DurationSeconds':.2, 'IdleEntryFrame':1}
        original['WalkingSequence'] = {'EndMoveFrames':2,'SpeedAt240':55}
        save(base/'skin.json',original)
        reviewed_video = base/original['Clips']['Idle']['Video']
        original_hash = digest(reviewed_video)
        defaults = dict(width=64,height=64,targetHeight=24,feetY=48,referenceStart=0)
        idle = dict(key='Idle',label='待机',group='Idle',loop=True,source='animation/standing.mov')
        idle['reviewed'] = dict(videoSha256=original_hash,sourceSha256=digest(source),
                                recipeSha256=fingerprint(recipe(defaults,idle)))
        actions = [idle,dict(key='PlayBall',label='玩球',group='ClickInteraction',loop=False,source='animation/standing.mov')]
        for key,span in [('WalkStart',[0,4]),('Walking',[4,8]),('WalkEnd',[8,12])]:
            actions.append(dict(key=key,label=key,group='Walking',loop=key=='Walking',
                                source='animation/standing.mov',conversion={'frameRange':span}))
        config = dict(schemaVersion=1,baseSkin='assets/base',outputSkin='assets/imported/test',
                      defaults=defaults,actions=actions)
        config_file = root/'animation/actions.json'
        manifest = root/'assets/imported/test/skin.json'
        report_file = root/'output/animation-import/last-run.json'
        def invoke(expected=0, *extra):
            save(config_file,config)
            result = subprocess.run([sys.executable,str(ROOT/'scripts/import_pet_actions.py'),
                '--root',str(root),'--ffmpeg',str(FFMPEG),*extra],capture_output=True,env=ENV)
            assert result.returncode == expected, result.stdout.decode('utf-8') + result.stderr.decode('utf-8')
            return load(report_file)
        first = invoke()
        assert first['converted']==4 and first['reused']==1
        skin = load(manifest)
        assert skin['Clips']['Idle']['Alignment']==original['Clips']['Idle']['Alignment']
        assert skin['Transitions']==original['Transitions'] and skin['WalkingSequence']==original['WalkingSequence']
        assert digest(manifest.parent/skin['Clips']['Idle']['Video'])==original_hash
        assert not skin['Clips']['PlayBall']['Loop'] and skin['Clips']['PlayBall']['PreviewGroup']=='ClickInteraction'
        segments = [load(root/f'output/animation-import/{key}.conversion.json') for key in ('WalkStart','Walking','WalkEnd')]
        assert all(r['frameCount']==4 and r['previewGroup']=='Walking' for r in segments)
        assert len({json.dumps([r['crop'],r['scaledContent'],r['padding']]) for r in segments})==1
        assert [r['frameRange'] for r in segments]==[[0,4],[4,8],[8,12]]
        print('PASS: approved bytes, authored alignment/transitions, click grouping, direct source segments and shared normalization')
        timestamp = manifest.stat().st_mtime_ns
        second = invoke()
        assert second['converted']==0 and second['reused']==5 and not second['manifestChanged']
        assert manifest.stat().st_mtime_ns==timestamp
        actions[1]['label']='玩球新版'
        renamed = invoke()
        assert renamed['converted']==0 and renamed['manifestChanged']
        assert load(manifest)['Clips']['PlayBall']['DisplayName']=='玩球新版'
        print('PASS: unchanged import performs no encoding/manifest write; menu-only edits reuse the encoded video')
        actions[1]['conversion']={'crf':20}
        changed = invoke()
        assert changed['converted']==1 and changed['reused']==4
        # A damaged cache file must never be trusted merely because its receipt exists.
        entries = list((root/'output/.cache/animation-import').glob('*/receipt.json'))
        entry = next(p.parent for p in entries if load(p)['conversion'].get('state')=='PlayBall' and load(p)['conversion'].get('crf')==20)
        (entry/'video.webm').write_bytes(b'broken')
        repaired = invoke()
        assert repaired['converted']==1 and repaired['reused']==4
        (entry/'receipt.json').write_text('{invalid json', encoding='utf-8')
        assert invoke()['converted']==1
        verified = invoke(0,'--verify-all')
        assert verified['converted']==0
        print('PASS: encoding-parameter invalidation, cache tamper repair and explicit full verification')
        before = manifest.read_bytes()
        movie(root/'animation/new.mov')
        pending = invoke(2)
        assert pending['status']=='needs_configuration' and pending['pendingSources']==['animation\\new.mov' if os.name=='nt' else 'animation/new.mov']
        assert manifest.read_bytes()==before
        config['ignoredSources']={'animation/new.mov':'暂不启用'}
        assert invoke()['status']=='ok'
        movie(root/'animation/opaque.mov',opaque=True)
        actions.append(dict(key='Happy',label='开心',group='ClickInteraction',loop=False,source='animation/opaque.mov'))
        failed = invoke(1)
        assert failed['status']=='failed' and manifest.read_bytes()==before and digest(reviewed_video)==original_hash
        assert (root/'output/animation-import/Happy.log').is_file()
        actions.pop(); config['ignoredSources']['animation/opaque.mov']='失败测试样例'
        actions.append(dict(key='idle',label='重复',group='Idle',loop=True,source='animation/standing.mov'))
        assert invoke(1)['status']=='failed' and manifest.read_bytes()==before
        actions.pop()
        config['outputSkin']='assets/base'
        assert invoke(1)['status']=='failed' and manifest.read_bytes()==before
        config['outputSkin']='assets/imported/test'
        print('PASS: unregistered sources, opaque inputs, case collisions and unsafe destinations leave the installed manifest untouched')
        # A source edit invalidates every affected recipe, including the approved passthrough.
        movie(source,color=100)
        refreshed = invoke()
        assert refreshed['converted']==5 and digest(reviewed_video)==original_hash
        assert load(manifest)['Clips']['Idle']['Alignment']==original['Clips']['Idle']['Alignment']
        print('PASS: changed source invalidates all affected outputs while preserving the original reviewed pack')


if __name__=='__main__':
    main()
