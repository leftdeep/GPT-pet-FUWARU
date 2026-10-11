"""New transparent MOVs run color + fixed standing-anchor calibration by default."""
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
import numpy as np

ROOT=Path(__file__).resolve().parents[1]
sys.path.insert(0,str(ROOT/'scripts'))
from import_pet_actions import digest,fingerprint,recipe,read_json,write_json
from pet_preview import frames
from pet_calibration import head_measure,match_feet

FFMPEG=ROOT/'runtime/ffmpeg.exe'
ENV=os.environ|{'PYTHONIOENCODING':'utf-8'}


def movie(path,shift=0,jump=False,missing_feet=False):
    images=[]
    for frame in range(12):
        image=np.zeros((384,384,4),np.uint8);dy=-6 if jump and 5<=frame<9 else 0
        # Head of fixed height, tapered shoulders, and two distinct shoe shapes.
        for y in range(137,216):
            half=42 if y<195 else 35
            image[y+dy,192-half:192+half]=[210+shift,207+shift,219+shift,255]
        image[216+dy:328+dy,161:225]=[170+shift,167+shift,179+shift,255]
        if not missing_feet:
            image[328+dy:345+dy,179:191]=[140+shift,137+shift,149+shift,255]
            image[333+dy:345+dy,196:210]=[155+shift,152+shift,164+shift,255]
        else:
            # Preserve measured full standing height with a remote tail, while
            # the reviewed sole locations are empty; cannot silently fit feet.
            image[328+dy:345+dy,240:244]=[180,180,190,255]
        if jump and frame>=8:
            image[260:275,258:270]=[240,20,30,255]  # saturated effect survives
        images.append(image)
    result=subprocess.run([str(FFMPEG),'-nostdin','-hide_banner','-loglevel','error','-f','rawvideo',
        '-pix_fmt','rgba','-s','384x384','-r','24','-i','pipe:0','-c:v','prores_ks','-profile:v','4',
        '-pix_fmt','yuva444p10le','-y',str(path)],input=np.asarray(images).tobytes(),capture_output=True)
    if result.returncode:raise AssertionError(result.stderr)


class CalibrationTest(unittest.TestCase):
    def test_new_mov_cache_and_uncertain_pose(self):
        with tempfile.TemporaryDirectory(prefix='pet-calibration-') as folder:
            root=Path(folder);animation=root/'animation';animation.mkdir();base=root/'assets/base';base.mkdir(parents=True)
            idle_source=animation/'idle.mov';source=animation/'new.mov';movie(idle_source);movie(source,shift=-10,jump=True)
            args=[sys.executable,str(ROOT/'scripts/import-alpha-video.py'),str(idle_source),'--output',str(base),
                  '--width','384','--height','384','--target-height','209','--feet-y','345.6','--margin','0.06','--reference-start','0','--lossless','--ffmpeg',str(FFMPEG)]
            result=subprocess.run(args,capture_output=True,env=ENV)
            self.assertEqual(result.returncode,0,result.stderr)
            skin=read_json(base/'skin.json');video=base/skin['Clips']['Idle']['Video'];original_hash=digest(video)
            reference=frames(video,ffmpeg=FFMPEG)[:5]
            # Sole annotations are in the normalized canvas, not original MOV.
            feet=[]
            for lo,hi in ((173,194),(195,214)):
                yy,xx=np.nonzero(reference[0,325:350,lo:hi,3]>220)
                bottom=yy.max()+325;xs=xx[yy>=yy.max()-1]+lo
                feet.append([float(xs.mean()),float(bottom)])
            defaults=dict(width=384,height=384,crf=24,lossless=True,targetHeight=209,feetY=345.6,margin=.06,referenceStart=0)
            idle=dict(key='Idle',label='待机',group='Idle',loop=True,source='animation/idle.mov')
            idle['reviewed']=dict(videoSha256=original_hash,sourceSha256=digest(idle_source),recipeSha256=fingerprint(recipe(defaults,idle)))
            new=dict(key='New',label='互动',group='ClickInteraction',loop=False,source='animation/new.mov')
            policy=dict(version=1,referenceAction='Idle',referenceAnnotations='animation/anchors.json',colorMaxShift=14)
            annotation=dict(canvas=[384,384],standingWindowFrames=5,headRegion=[135,153,115,43],
                            actions={'Idle':dict(feetContacts=feet,reviewedColorVideoSha256=original_hash)})
            write_json(animation/'policy.json',policy);write_json(animation/'anchors.json',annotation)
            config=dict(schemaVersion=1,baseSkin='assets/base',outputSkin='assets/imported/test',defaults=defaults,
                        actions=[idle,new],calibration='animation/policy.json')
            def invoke(code=0):
                write_json(animation/'actions.json',config)
                result=subprocess.run([sys.executable,str(ROOT/'scripts/import_pet_actions.py'),'--root',str(root),
                    '--ffmpeg',str(FFMPEG)],capture_output=True,env=ENV)
                self.assertTrue((root/'output/animation-import/last-run.json').is_file(),result.stderr.decode('utf-8',errors='replace'))
                report=read_json(root/'output/animation-import/last-run.json')
                self.assertEqual(result.returncode,code,report)
                return report
            first=invoke();self.assertEqual(first['converted'],1)
            manifest=root/'assets/imported/test/skin.json';safe=manifest.read_bytes();pack=read_json(manifest)
            self.assertEqual(digest(manifest.parent/pack['Clips']['Idle']['Video']),original_hash)
            self.assertFalse(pack['Clips']['New']['Loop']);self.assertEqual(pack['Clips']['New']['VideoFrameCount'],12)
            report=read_json(root/'output/animation-import/New.calibration.json')
            self.assertLess(report['colorQuantileErrorAfter'],report['colorQuantileErrorBefore'])
            self.assertEqual(report['geometry']['scaleY'],1);self.assertFalse(report['geometry']['perFrameGroundPinning'])
            normalized=frames(Path(report['normalizedVideo']),ffmpeg=FFMPEG)
            final=frames(manifest.parent/pack['Clips']['New']['Video'],ffmpeg=FFMPEG)
            # Head height change through jump is unchanged, to filter rounding.
            def top(f):return np.nonzero(f[120:230,150:235,3]>220)[0].min()
            self.assertEqual(top(normalized[0])-top(normalized[6]),top(final[0])-top(final[6]))
            red=(final[9,...,0]>180)&(final[9,...,1]<60)&(final[9,...,3]>200)
            self.assertGreater(red.sum(),100)
            self.assertEqual(invoke()['converted'],0);self.assertEqual(manifest.read_bytes(),safe)
            normal_cache=list((root/'output/.cache/animation-normalized').glob('*/video.webm'))
            times=[p.stat().st_mtime_ns for p in normal_cache]
            # A segment starts while airborne, but fits its anchors to the full
            # clip's standing reference and reuses the same normalized source.
            segment=new|dict(key='Segment',label='分段',conversion={'frameRange':[4,10]})
            config['actions'].append(segment)
            self.assertEqual(invoke()['converted'],1)
            segmented=read_json(manifest)['Clips']['Segment']
            self.assertEqual(segmented['VideoFrameCount'],6)
            self.assertEqual(len(list((root/'output/.cache/animation-normalized').glob('*/video.webm'))),1)
            segmented_report=read_json(root/'output/animation-import/Segment.calibration.json')
            self.assertEqual(segmented_report['anchorCorrection'],report['anchorCorrection'])
            config['actions'].pop()
            invoke()
            # Change only the color policy: refit once, normalized MOV stays cached.
            policy['colorMaxShift']=8;write_json(animation/'policy.json',policy)
            self.assertEqual(invoke()['converted'],1)
            self.assertEqual(times,[p.stat().st_mtime_ns for p in normal_cache])
            safe=manifest.read_bytes()
            # Missing shoes must not publish a plausible alpha-bbox substitute.
            movie(source,shift=-10,jump=True,missing_feet=True)
            pending=invoke(3);self.assertEqual(pending['status'],'needs_calibration');self.assertEqual(manifest.read_bytes(),safe)
            self.assertTrue(Path(pending['measurement']).is_file())
            failed=read_json(Path(pending['measurement']))
            new['calibration']=dict(normalizedVideoSha256='0'*64,feetContacts=feet)
            self.assertIn('different normalized bytes',invoke(3)['error'])
            # Hash-bound manual override can reuse normalization, with explicit
            # annotations for a nonstandard source (test restores normal feet).
            movie(source,shift=-10,jump=True)
            new.pop('calibration');self.assertEqual(invoke()['converted'],0)
            new['calibration']=dict(normalizedVideoSha256=report['normalizedVideoSha256'],feetContacts=report['feetContacts'])
            self.assertEqual(invoke()['converted'],1)
            before=manifest.read_bytes()
            annotation['actions']['Idle']['reviewedColorVideoSha256']='0'*64;write_json(animation/'anchors.json',annotation)
            self.assertEqual(invoke(1)['status'],'failed');self.assertEqual(manifest.read_bytes(),before)
            # New 640 actions retain the reviewed 384 measurement coordinates,
            # apply the transform at full resolution, and honor final CRF 40.
            annotation['actions']['Idle']['reviewedColorVideoSha256']=original_hash
            write_json(animation/'anchors.json',annotation)
            new.pop('calibration',None)
            new['conversion']=dict(width=640,height=640,targetHeight=209*640/384,feetY=576,crf=40,lossless=False)
            self.assertEqual(invoke()['converted'],1)
            high=read_json(manifest)['Clips']['New']
            self.assertEqual([high['VideoWidth'],high['VideoHeight'],high['VideoFrameCount']],[640,640,12])
            high_report=read_json(root/'output/animation-import/New.conversion.json')
            self.assertEqual(high_report['crf'],40)
            self.assertFalse(high_report['lossless'])
            self.assertEqual(high_report['calibration']['measurementCanvas'],[384,384])
            self.assertEqual(high_report['calibration']['outputCanvas'],[640,640])
            self.assertEqual(high_report['calibration']['geometry']['scaleY'],1)


if __name__=='__main__':unittest.main(verbosity=2)
