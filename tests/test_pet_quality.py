"""Quality rebuild preserves frozen geometry/metadata and rejects stale sources."""
import copy
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT/'scripts'))
from import_pet_actions import digest, recipe, fingerprint, read_json, write_json
from test_pet_action_import import movie
FFMPEG = ROOT/'runtime/ffmpeg.exe'
ENV = os.environ | {'PYTHONIOENCODING':'utf-8'}


def main():
    with tempfile.TemporaryDirectory(prefix='pet-quality-test-') as folder:
        root = Path(folder)
        (root/'animation').mkdir()
        base = root/'assets/base'
        base.mkdir(parents=True)
        source = root/'animation/idle.mov'
        movie(source)
        default_output = root/'assets/default-quality'
        default_run = subprocess.run([sys.executable,str(ROOT/'scripts/import-alpha-video.py'),str(source),
            '--output',str(default_output),'--reference-start','0','--crop','24,16,16,24','--margin','0.04'],capture_output=True,env=ENV)
        assert default_run.returncode == 0, default_run.stderr
        default_clip = read_json(default_output/'skin.json')['Clips']['Idle']
        default_report = read_json((default_output/default_clip['Video']).with_suffix('.conversion.json'))
        assert [default_clip['VideoWidth'],default_clip['VideoHeight']] == [640,640]
        assert default_report['crf'] == 40 and abs(default_report['targetStandingHeight']-209*640/384)<1e-6
        result = subprocess.run([sys.executable, str(ROOT/'scripts/import-alpha-video.py'), str(source),
            '--output',str(base),'--width','64','--height','64','--target-height','24','--feet-y','48',
            '--reference-start','0','--crf','24'],capture_output=True,env=ENV)
        assert result.returncode == 0, result.stderr
        skin = read_json(base/'skin.json')
        clip = skin['Clips']['Idle']
        clip['Alignment'] = {'Scale':.99,'OffsetX':.01,'LeftOffsetX':.02}
        skin['WalkingSequence'] = {'EndMoveFrames':2,'SpeedAt240':55}
        skin['Transitions'] = {'DurationSeconds':.2,'IdleEntryFrame':1}
        write_json(base/'skin.json',skin)
        baseline = base/clip['Video']
        report = read_json(baseline.with_suffix('.conversion.json'))
        original = dict(width=64,height=64,crf=24,targetHeight=24,feetY=48,referenceStart=0)
        action = dict(key='Idle',label='待机',group='Idle',loop=True,source='animation/idle.mov',reviewedFinal=True)
        old_recipe = recipe(original,action)
        action['reviewed'] = dict(videoSha256=digest(baseline),sourceSha256=digest(source),recipeSha256=fingerprint(old_recipe))
        geometry = dict(sourceSha256=digest(source),crop=report['crop'],scaled=report['scaledContent'],
                        padding=report['padding'],standing=report['standing'],frameRange=None,
                        colorCorrection=None,anchorCorrection=None)
        profiles = dict(version=1,actions={'Idle':dict(geometry=geometry,originalRecipe=old_recipe,
            baselineVideo=str(baseline.relative_to(root)),baselineVideoSha256=digest(baseline),clip=copy.deepcopy(clip))})
        config = dict(schemaVersion=1,baseSkin='assets/base',outputSkin='assets/imported/quality',
                      qualityProfiles='animation/quality.json',actions=[action],
                      defaults=original|dict(width=640,height=640,crf=40,targetHeight=240,feetY=480))
        manifest = root/'assets/imported/quality/skin.json'
        def invoke(code=0):
            write_json(root/'animation/quality.json',profiles)
            write_json(root/'animation/actions.json',config)
            run = subprocess.run([sys.executable,str(ROOT/'scripts/import_pet_actions.py'),'--root',str(root),
                '--ffmpeg',str(FFMPEG)],capture_output=True,env=ENV)
            assert run.returncode == code, run.stdout.decode('utf-8') + run.stderr.decode('utf-8')
            return read_json(root/'output/animation-import/last-run.json')
        assert invoke()['converted'] == 1
        after = read_json(manifest)
        upgraded = after['Clips']['Idle']
        assert [upgraded['VideoWidth'],upgraded['VideoHeight'],upgraded['VideoFrameCount'],upgraded['FrameRate']] == [640,640,12,24]
        assert upgraded['Alignment'] == clip['Alignment'] and after['Transitions'] == skin['Transitions']
        assert after['WalkingSequence'] == skin['WalkingSequence']
        conversion = read_json(root/'output/animation-import/Idle.conversion.json')
        assert conversion['crf'] == 40 and conversion['finalLossyEncodes'] == 1
        assert conversion['validation']['allFramesAlphaVerified']
        safe = manifest.read_bytes()
        assert invoke()['converted'] == 0 and manifest.read_bytes() == safe
        config['defaults']['referenceStart'] = .1
        assert invoke(1)['status'] == 'failed' and manifest.read_bytes() == safe
        config['defaults']['referenceStart'] = 0
        action['calibration'] = {'normalizedVideoSha256':'a'*64,'feetContacts':[[28,47],[36,47]]}
        assert invoke(1)['status'] == 'failed' and manifest.read_bytes() == safe
        action.pop('calibration')
        profiles['actions']['Idle']['baselineVideoSha256'] = '0'*64
        assert invoke(1)['status'] == 'failed' and manifest.read_bytes() == safe
        profiles['actions']['Idle']['baselineVideoSha256'] = digest(baseline)
        movie(source,color=100)
        assert invoke(1)['status'] == 'failed' and manifest.read_bytes() == safe
        print('PASS: 640/CRF40 rebuild, reviewedFinal preservation, author metadata, one final encode, cache and atomic stale-source rejection')


if __name__ == '__main__':
    main()
