"""Anchor alignment: shared ground, real jumping, alpha and guarded cache reuse."""
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import numpy as np
from PIL import Image,ImageDraw

ROOT=Path(__file__).resolve().parents[1];sys.path.insert(0,str(ROOT/'scripts'))
from pet_anchor import validate_anchor_profile,anchor_matrix,transform_point,anchor_filter
from import_pet_actions import digest,fingerprint,recipe,read_json,write_json
FFMPEG=ROOT/'runtime/ffmpeg.exe';ENV=os.environ|{'PYTHONIOENCODING':'utf-8'}


def command(args,**kwargs):
    result=subprocess.run(args,capture_output=True,env=ENV,**kwargs)
    if result.returncode:raise AssertionError(result.stderr.decode('utf-8',errors='replace'))
    return result.stdout


class AnchorTests(unittest.TestCase):
    def setUp(self):
        self.profile=dict(version=1,scaleX=1.04,sourceFeet=[64,110],targetFeet=[65,111],sourceHead=[60,30],targetHead=[62,30])

    def test_two_anchor_fit_and_preserved_vertical_displacement(self):
        m=anchor_matrix(self.profile)
        np.testing.assert_allclose(transform_point(self.profile['sourceFeet'],m),self.profile['targetFeet'])
        self.assertAlmostEqual(transform_point(self.profile['sourceHead'],m)[0],62)
        for point in ([60,30],[64,110],[110,50]):
            jumped=[point[0],point[1]-24]
            self.assertAlmostEqual(transform_point(jumped,m)[1]-transform_point(point,m)[1],-24)
        self.assertEqual(m[3:5],[0,1])
        for bad in (self.profile|{'scaleX':True},self.profile|{'scaleX':float('nan')},
                    self.profile|{'targetFeet':[65,113]},self.profile|{'targetHead':[80,30]},
                    self.profile|{'sourceHead':[64,100]},self.profile|{'scaleY':1.01}):
            with self.assertRaises(ValueError):validate_anchor_profile(bad)
        with self.assertRaises(ValueError):anchor_filter(self.profile,64,64)

    def test_import_alpha_jump_cache_and_atomic_rejection(self):
        with tempfile.TemporaryDirectory(prefix='pet-anchor-test-') as temporary:
            root=Path(temporary);(root/'animation').mkdir();base=root/'assets/base';base.mkdir(parents=True)
            authored=root/'animation/source.mov';authored.write_bytes(b'unchanged authored source; reviewed video used')
            video=base/'idle.webm';images=[]
            for lift in (0,2,4,4,2,0):
                image=Image.new('RGBA',(128,128),(0,255,0,0));d=ImageDraw.Draw(image)
                d.ellipse((42,12-lift,78,50-lift),fill=(210,210,210,255))
                d.rectangle((55,40-lift,73,109-lift),fill=(150,150,150,255))
                d.rectangle((52,108-lift,61,110-lift),fill=(220,220,220,255))
                d.rectangle((66,108-lift,75,110-lift),fill=(220,220,220,255))
                d.ellipse((105,48,113,56),fill=(255,70,100,220));images.append(np.asarray(image))
            raw=np.stack(images)
            command([str(FFMPEG),'-nostdin','-hide_banner','-loglevel','error','-f','rawvideo','-pix_fmt','rgba',
                '-s','128x128','-r','24','-i','pipe:0','-an','-c:v','libvpx-vp9','-pix_fmt','yuva420p',
                '-lossless','1','-auto-alt-ref','0',str(video)],input=raw.tobytes())
            original=digest(video)
            clip=dict(Video='idle.webm',VideoWidth=128,VideoHeight=128,VideoFrameCount=6,FrameRate=24,
                AnchorX=64,AnchorY=110,DisableProceduralMotion=True,Loop=True)
            write_json(base/'skin.json',dict(Name='Anchor test',StandingHeightRatio=80/128,Clips={'Idle':clip}))
            defaults=dict(width=128,height=128,targetHeight=80,feetY=112)
            action=dict(key='Idle',label='锚点测试',group='Idle',loop=True,source='animation/source.mov')
            action['reviewed']=dict(videoSha256=original,sourceSha256=digest(authored),recipeSha256=fingerprint(recipe(defaults,action)))
            action.update(anchorFromReviewed=True,conversion={'anchorCorrection':self.profile})
            config=dict(schemaVersion=1,baseSkin='assets/base',outputSkin='assets/imported/anchors',defaults=defaults,actions=[action])
            config_file=root/'animation/actions.json';manifest=root/'assets/imported/anchors/skin.json'
            def invoke(code=0):
                write_json(config_file,config)
                result=subprocess.run([sys.executable,str(ROOT/'scripts/import_pet_actions.py'),'--root',str(root),'--ffmpeg',str(FFMPEG)],capture_output=True,env=ENV)
                self.assertEqual(result.returncode,code,result.stdout.decode('utf-8')+result.stderr.decode('utf-8'))
                return read_json(root/'output/animation-import/last-run.json')
            self.assertEqual(invoke()['converted'],1)
            after_skin=read_json(manifest);after_path=manifest.parent/after_skin['Clips']['Idle']['Video']
            def decode(path,filters=None):
                args=[str(FFMPEG),'-nostdin','-hide_banner','-loglevel','error','-c:v','libvpx-vp9','-i',str(path)]
                if filters:args+=['-vf',filters]
                return np.frombuffer(command(args+['-fps_mode','passthrough','-an','-pix_fmt','rgba','-f','rawvideo','pipe:1']),np.uint8).reshape(-1,128,128,4)
            before,after=decode(video),decode(after_path)
            expected=decode(video,anchor_filter(self.profile,128,128))
            self.assertTrue(np.array_equal(expected[...,3],after[...,3]))
            rows=lambda f:np.array([np.flatnonzero(np.max(a[...,3],axis=1)>=128)[-1] for a in f])
            self.assertTrue(np.array_equal(rows(after)-rows(before),np.ones(6)))
            self.assertTrue(np.array_equal(rows(after)-rows(after)[0],rows(before)-rows(before)[0]))
            interior=(after[...,3]==255)&(np.ptp(expected[...,:3].astype(float),axis=-1)<3)
            self.assertLess(np.abs(after[...,:3].astype(float)-expected[...,:3])[interior].mean(),1.5)
            self.assertEqual(digest(video),original)
            self.assertEqual(invoke()['converted'],0)
            action['conversion']['anchorCorrection']=self.profile|{'targetFeet':[66,111]}
            self.assertEqual(invoke()['converted'],1)
            safe=manifest.read_bytes()
            action['reviewed']['sourceSha256']='0'*64
            self.assertEqual(invoke(1)['status'],'failed');self.assertEqual(manifest.read_bytes(),safe)
            action['reviewed']['sourceSha256']=digest(authored)
            action['colorFromReviewed']=True;action['conversion']['colorCorrection']={'version':1}
            self.assertEqual(invoke(1)['status'],'failed');self.assertEqual(manifest.read_bytes(),safe)
            action.pop('colorFromReviewed');action['conversion'].pop('colorCorrection')
            action['conversion']['anchorCorrection']=self.profile|{'targetFeet':[66,150]}
            self.assertEqual(invoke(1)['status'],'failed');self.assertEqual(manifest.read_bytes(),safe)
            # Promote exactly the accepted bytes. The full corrected recipe,
            # source hash and final video hash all participate in the approval.
            action['conversion']['anchorCorrection']=self.profile
            action.pop('anchorFromReviewed');action['reviewedFinal']=True
            action['reviewed']['videoSha256']=original
            action['reviewed']['recipeSha256']=fingerprint(recipe(defaults,action))
            self.assertEqual(invoke()['converted'],0)
            final_skin=read_json(manifest)
            self.assertEqual(digest(manifest.parent/final_skin['Clips']['Idle']['Video']),original)
            safe=manifest.read_bytes()
            action['conversion']['anchorCorrection']=self.profile|{'scaleX':1.06}
            self.assertEqual(invoke(1)['status'],'failed');self.assertEqual(manifest.read_bytes(),safe)
            action['conversion']['anchorCorrection']=self.profile;action['anchorFromReviewed']=True
            self.assertEqual(invoke(1)['status'],'failed');self.assertEqual(manifest.read_bytes(),safe)


if __name__=='__main__':unittest.main(verbosity=2)
