"""Color correction must improve neutral colors without moving or fading pixels."""
import copy
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'scripts'))
from pet_color import validate_profile, transform_rgb
from import_pet_actions import read_json, write_json, digest, recipe, fingerprint

FFMPEG=ROOT/'runtime/ffmpeg.exe'
ENV=os.environ|{'PYTHONIOENCODING':'utf-8'}
PROFILE=dict(version=1,curves={c:[[0,0],[128,140],[255,255]] for c in 'rgb'},
             strength=1,chromaProtection=[40,85])


def command(args,**kwargs):
    result=subprocess.run(args,capture_output=True,env=ENV,**kwargs)
    assert result.returncode==0,result.stderr.decode('utf-8',errors='replace')
    return result.stdout


def decode(video):
    return command([str(FFMPEG),'-nostdin','-hide_banner','-loglevel','error','-c:v','libvpx-vp9',
        '-i',str(video),'-fps_mode','passthrough','-pix_fmt','rgba','-an','-f','rawvideo','pipe:1'])


def main():
    p=validate_profile(PROFILE)
    assert all(a>b for a,b in zip(transform_rgb((140,132,151),p),(140,132,151)))
    assert transform_rgb((235,80,120),p)==(235,80,120)
    assert transform_rgb((0,0,0),p)==(0,0,0) and transform_rgb((255,255,255),p)==(255,255,255)
    for invalid in (PROFILE|{'strength':float('nan')},PROFILE|{'chromaProtection':[100,40]},
                    PROFILE|{'curves':{'r':[[0,0],[200,100],[100,200],[255,255]],'g':PROFILE['curves']['g'],'b':PROFILE['curves']['b']}}):
        try:validate_profile(invalid)
        except ValueError:pass
        else:raise AssertionError('Invalid color profile accepted')
    print('PASS: neutral correction, saturated-color protection, fixed endpoints and profile validation')
    with tempfile.TemporaryDirectory(prefix='pet-color-test-') as temporary:
        root=Path(temporary); (root/'animation').mkdir(); base=root/'assets/base'; base.mkdir(parents=True)
        source=root/'animation/sample.mov'
        raw=[]
        for frame in range(3):
            data=bytearray(64*64*4)
            for y in range(16,40):
                for x in range(24,40):
                    rgb=(235,80,120) if 30<=x<36 and 25<=y<34 else (140+frame,132+frame,151+frame)
                    alpha=128 if x in (24,39) or y in (16,39) else 255
                    data[(y*64+x)*4:(y*64+x+1)*4]=bytes((*rgb,alpha))
            raw.append(data)
        command([str(FFMPEG),'-nostdin','-hide_banner','-loglevel','error','-f','rawvideo','-pix_fmt','rgba',
            '-s','64x64','-r','24','-i','pipe:0','-c:v','prores_ks','-profile:v','4','-pix_fmt','yuva444p10le',str(source)],input=b''.join(raw))
        command([sys.executable,str(ROOT/'scripts/import-alpha-video.py'),str(source),'--output',str(base),
                 '--width','64','--height','64','--target-height','24','--feet-y','48','--reference-start','0','--lossless'])
        skin=read_json(base/'skin.json'); skin['Clips']['Idle']['Alignment']={'Scale':.99}
        write_json(base/'skin.json',skin); video=base/skin['Clips']['Idle']['Video']; original_hash=digest(video)
        defaults=dict(width=64,height=64,targetHeight=24,feetY=48,referenceStart=0)
        action=dict(key='Idle',label='校色待机',group='Idle',loop=True,source='animation/sample.mov')
        action['reviewed']=dict(videoSha256=original_hash,sourceSha256=digest(source),recipeSha256=fingerprint(recipe(defaults,action)))
        action.update(colorFromReviewed=True,conversion={'colorCorrection':PROFILE})
        config=dict(schemaVersion=1,baseSkin='assets/base',outputSkin='assets/imported/color',defaults=defaults,actions=[action])
        config_file=root/'animation/actions.json'; report_file=root/'output/animation-import/last-run.json'; manifest=root/'assets/imported/color/skin.json'
        def invoke(expected=0):
            write_json(config_file,config)
            result=subprocess.run([sys.executable,str(ROOT/'scripts/import_pet_actions.py'),'--root',str(root),'--ffmpeg',str(FFMPEG)],
                                  capture_output=True,env=ENV)
            assert result.returncode==expected,result.stdout.decode('utf-8')+result.stderr.decode('utf-8')
            return read_json(report_file)
        assert invoke()['converted']==1
        updated=read_json(manifest); converted=manifest.parent/updated['Clips']['Idle']['Video']
        before,after=decode(video),decode(converted)
        assert len(before)==len(after)==3*64*64*4 and before[3::4]==after[3::4]
        assert updated['Clips']['Idle']['Alignment']==skin['Clips']['Idle']['Alignment']
        changed=protected=0
        protected_errors=[]
        for i in range(0,len(before),4):
            r,g,b,a=before[i:i+4]
            if a<240:continue
            delta=[after[i+k]-before[i+k] for k in range(3)]
            if max(r,g,b)-min(r,g,b)<40 and 100<g<180:
                assert sum(delta)>15; changed+=1
            if r-g>110:
                protected_errors.extend(map(abs,delta)); protected+=1
        # YUV 4:2:0 mixes chroma at the red/neutral boundary; protect the aggregate
        # saturated region while requiring a substantial neutral-color change.
        assert sum(protected_errors)/len(protected_errors)<=3
        assert changed>100 and protected>5 and digest(video)==original_hash
        print('PASS: actual VP9 LUT output improves neutral pixels; decoded alpha, frame count and authored alignment stay exact')
        assert invoke()['converted']==0
        action['conversion']['colorCorrection']=copy.deepcopy(PROFILE)|{'strength':.5}
        assert invoke()['converted']==1
        before_manifest=manifest.read_bytes()
        action['conversion']['colorCorrection']['strength']=float('nan')
        assert invoke(1)['status']=='failed' and manifest.read_bytes()==before_manifest
        action['conversion']['colorCorrection']['strength']=.5
        action['reviewed']['sourceSha256']='0'*64
        assert invoke(1)['status']=='failed' and manifest.read_bytes()==before_manifest
        print('PASS: profile changes invalidate cache; invalid profiles and unreviewed-source fallback cannot replace the manifest')


if __name__=='__main__':
    main()
