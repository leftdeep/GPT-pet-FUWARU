"""Review the configured production actions with head/feet annotations; never apply."""
import argparse
import json
import subprocess
import sys
from pathlib import Path
import numpy as np
from PIL import Image,ImageDraw
from import_pet_actions import digest,read_json,write_json,fingerprint
from pet_anchor import anchor_matrix
from pet_anchor import transform_point
from pet_anchor import anchor_filter
import pet_preview as preview
ROOT=Path(__file__).resolve().parents[1]
FOLDER=ROOT/'output/anchor-preview'
CYAN=(0,155,190);ORANGE=(245,116,18)

def head_center(frames,roi):
    if not isinstance(roi,list) or len(roi)!=4 or any(type(v) is not int for v in roi):
        raise ValueError('Head region must be [x,y,width,height]')
    x,y,w,h=roi
    if min(x,y)<0 or min(w,h)<20 or x+w>frames.shape[2] or y+h>frames.shape[1]:
        raise ValueError('Head region must be inside the frame')
    # The reviewed upper-head silhouette excludes eyes, blush, hands and hearts.
    # Average only corresponding standing windows, never an airborne silhouette.
    alpha=frames[:,y:y+h,x:x+w,3]
    weights=(alpha>220).mean(axis=0)
    if weights.sum()<w*h*.4:raise ValueError('Standing head reference is missing')
    yy,xx=np.indices(weights.shape)
    return [round(float((weights*(xx+x)).sum()/weights.sum()),6),
            round(float((weights*(yy+y)).sum()/weights.sum()),6)]


def markers(image,head,feet,color,reference=False):
    d=ImageDraw.Draw(image)
    for x,y in [head]+feet:
        if reference:d.line((x-4,y,x+4,y),fill=color,width=1);d.line((x,y-4,x,y+4),fill=color,width=1)
        else:d.ellipse((x-3,y-3,x+3,y+3),outline=color,width=1)
    d.line(tuple(feet[0]+feet[1]),fill=color,width=1)
    x=(feet[0][0]+feet[1][0])/2;y=(feet[0][1]+feet[1][1])/2
    d.line((x,y-6,x,y+3),fill=color,width=1)


def comparison(path,idle,old,new,reference,source,matrix,old_matrix,annotated):
    yy,xx=np.indices((384,384));gray=np.where((xx//16+yy//16)%2==0,226,245).astype(np.float32)
    background=np.repeat(gray[...,None],3,axis=2)
    process=subprocess.Popen([str(preview.FFMPEG),'-nostdin','-hide_banner','-loglevel','error','-y',
        '-f','rawvideo','-pix_fmt','rgb24','-s','1152x384','-r','24','-i','pipe:0','-an','-c:v','libx264',
        '-preset','fast','-crf','17','-pix_fmt','yuv420p','-movflags','+faststart',str(path)],
        stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
    try:
        for index in range(len(new)+48):
            panels=[idle[index%len(idle)],preview.sequence_frame(idle,old,index),preview.sequence_frame(idle,new,index)]
            flattened=[]
            for col,panel in enumerate(panels):
                a=panel[...,3:4].astype(np.float32)/255
                image=Image.fromarray(np.rint(panel[...,:3]*a+background*(1-a)).clip(0,255).astype(np.uint8))
                if annotated:
                    markers(image,reference['head'],reference['feet'],CYAN,True)
                    local=index-24
                    # These are calibration annotations, not a tracking system.
                    # Hide them during the moving middle of the action.
                    if col and (0<=local<5 or len(new)-5<=local<len(new)):
                        m=old_matrix if col==1 else matrix
                        markers(image,transform_point(source['head'],m),[transform_point(p,m) for p in source['feet']],ORANGE)
                flattened.append(np.asarray(image))
            process.stdin.write(np.concatenate(flattened,axis=1).tobytes())
        process.stdin.close();error=process.stderr.read().decode('utf-8',errors='replace')
        if process.wait(timeout=90):raise ValueError('Comparison encoding failed: '+error[-1000:])
    finally:
        process.stderr.close()
        if process.poll() is None:process.kill();process.wait()


def zoom_sheet(path,idle,old,new,reference,source,matrix,old_matrix):
    sheet=Image.new('RGB',(1152,520),'#eeeef4');d=ImageDraw.Draw(sheet)
    for row,(frame,title) in enumerate(((0,'ENTRY'),(-1,'RETURN'))):
        d.text((8,row*260+5),title,fill='#273042')
        for col,frames in enumerate((idle,old,new)):
            rgba=Image.fromarray(frames[frame]);bg=Image.new('RGBA',rgba.size,'#eeeef4');bg.alpha_composite(rgba)
            markers(bg,reference['head'],reference['feet'],CYAN,True)
            if col:
                m=old_matrix if col==1 else matrix
                markers(bg,transform_point(source['head'],m),[transform_point(p,m) for p in source['feet']],ORANGE)
            head=bg.crop((140,150,245,231)).resize((315,243),Image.Resampling.NEAREST)
            feet=bg.crop((169,326,218,349)).resize((147,69),Image.Resampling.NEAREST)
            sheet.paste(head,(col*384+8,row*260+17));sheet.paste(feet,(col*384+223,row*260+187))
    sheet.save(path)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config',default='animation/actions.json')
    args=parser.parse_args()
    config_path=(ROOT/args.config).resolve();config=read_json(config_path)
    base=(ROOT/config['baseSkin']).resolve();output=(ROOT/config['outputSkin']).resolve()
    FOLDER.mkdir(parents=True,exist_ok=True)
    # Preparation is reusable; this command never changes settings or launches
    # the desktop pet. Installation remains in import-pet-actions.ps1 -Apply.
    result=subprocess.run([sys.executable,str(ROOT/'scripts/import_pet_actions.py'),'--config',str(config_path)],
        capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=600)
    (FOLDER/'import.log').write_text(result.stdout+'\n'+result.stderr,encoding='utf-8')
    if result.returncode:raise ValueError(result.stdout+result.stderr)
    skin=read_json(output/'skin.json');calibration=read_json(ROOT/'animation/anchor-calibration.json')
    settings_path=ROOT/'local-data/settings.json';settings_before=read_json(settings_path)
    source_arrays={};points={}
    for key in ('Idle','Happy','Shy'):
        entry=calibration['actions'][key];video=(base/entry['colorVideo']).resolve()
        if not video.is_relative_to(base) or digest(video)!=entry['reviewedColorVideoSha256']:
            raise ValueError(f'{key}: reviewed color source changed; check anchor annotations')
        source_arrays[key]=preview.frames(video)
        standing=np.concatenate([source_arrays[key][:5],source_arrays[key][-5:]])
        points[key]=dict(head=head_center(standing,calibration['headRegion']),feet=entry['feetContacts'])
    idle=source_arrays['Idle'];reference=points['Idle'];reports={};cards=[]
    by_key={a['key']:a for a in config['actions']}
    applied=Path(settings_before['SkinDirectory']).resolve()==output
    for key,label in [('Happy','开心跳跃'),('Shy','害羞')]:
        old=source_arrays[key];clip=skin['Clips'][key];new_path=output/clip['Video'];new=preview.frames(new_path,resize=clip['VideoWidth']!=384 or clip['VideoHeight']!=384)
        profile=by_key[key]['conversion']['anchorCorrection'];matrix=anchor_matrix(profile)
        identity=[1,0,0,0,1,0];source=points[key]
        if new.shape!=old.shape or clip['Loop'] or clip['PreviewGroup']!='ClickInteraction':
            raise ValueError('Interaction metadata or frame dimensions changed')
        rebuilt = bool(config.get('qualityProfiles'))
        if rebuilt:
            # Lossy CRF output and the 384 review downsample cannot promise exact
            # alpha identity with the old 384 video. Check the verified receipt.
            imported = read_json(ROOT/'output/animation-import/last-run.json')
            item = next(a for a in imported['actions'] if a['key']==key)
            validation = read_json(ROOT/f'output/animation-import/{key}.conversion.json')['validation']
            if digest(new_path)!=item['videoSha256'] or not validation['allFramesAlphaVerified']:
                raise ValueError('Final quality video differs from the fully verified import')
        else:
            filters=anchor_filter(profile,384,384)+',alphaextract'
            expected=preview.run([str(preview.FFMPEG),'-nostdin','-hide_banner','-loglevel','error','-c:v','libvpx-vp9',
                '-i',str(base/calibration['actions'][key]['colorVideo']),'-vf',filters,'-fps_mode','passthrough',
                '-an','-pix_fmt','gray','-f','rawvideo','pipe:1']).stdout
            if expected!=new[...,3].tobytes():raise ValueError('Final video differs from the reviewed alpha correction')
        clean=FOLDER/(key+'-comparison.mp4');marked=FOLDER/(key+'-anchors.mp4')
        extras=[FOLDER/(key+'-zoom.png'),FOLDER/(key+'-poster.png')]
        render_key=fingerprint(dict(before=calibration['actions'][key]['reviewedColorVideoSha256'],after=digest(new_path),
            idle=calibration['actions']['Idle']['reviewedColorVideoSha256'],points=points,matrix=matrix,
            script=digest(Path(__file__)),helpers=digest(ROOT/'scripts/pet_preview.py')))
        receipt=FOLDER/(key+'-render.json');hit=False
        if receipt.is_file() and clean.is_file() and marked.is_file() and all(p.is_file() for p in extras):
            r=read_json(receipt);hit=r.get('key')==render_key and r.get('files')=={p.name:digest(p) for p in [clean,marked]+extras}
        if not hit:
            comparison(clean,idle,old,new,reference,source,matrix,identity,False)
            comparison(marked,idle,old,new,reference,source,matrix,identity,True)
            zoom_sheet(extras[0],idle,old,new,reference,source,matrix,identity)
            preview.run([str(preview.FFMPEG),'-nostdin','-hide_banner','-loglevel','error','-ss','1.05','-i',str(marked),
                '-frames:v','1','-update','1','-y',str(extras[1])])
            write_json(receipt,dict(key=render_key,files={p.name:digest(p) for p in [clean,marked]+extras}))
        reports[key]=dict(frames=len(new),seconds=len(new)/24,videoSha256=digest(new_path),profile=profile,
            sourceAnchors=source,referenceAnchors=reference,matrix=matrix,allFramesPlannedAlphaVerified=not rebuilt,
            allFramesAlphaVerified=True,outputCanvas=[clip['VideoWidth'],clip['VideoHeight']],reviewCanvas=[384,384],
            perFrameGroundPinning=False,colorCurvesNotReapplied=not rebuilt,colorCurvesAppliedOnceFromMov=rebuilt,renderCacheHit=hit)
        cards.append(f'''<section data-key="{key}"><h2>{label}</h2><div class="labels"><b>待机基准</b><b>原动作（色调已统一）</b><b>正式锚点校准动作</b></div>
<video controls loop muted playsinline preload="metadata" src="{key}-anchors.mp4" poster="{key}-poster.png"></video>
<label class="toggle"><input type="checkbox" checked> 显示标定锚点</label>
<p class="caption">{len(new)} 帧 · {len(new)/24:.3f} 秒 · 单次播放后返回待机 · 宽度 {profile['scaleX']*100:.2f}% · 高度 100%</p>
<details><summary>放大查看首尾头部与双脚</summary><p class="caption">上排为动作首帧，下排为末帧。脸部角度和脚间距保留原动作。</p><img src="{key}-zoom.png" alt="{label}首尾锚点放大比较"></details></section>''')
    # Auto-walking legitimately updates position; preview must not alter settings.
    settings_after=read_json(settings_path)
    if {k:v for k,v in settings_before.items() if k not in ('X','Y')}!={k:v for k,v in settings_after.items() if k not in ('X','Y')}:
        raise ValueError('Preview changed non-position settings')
    write_json(FOLDER/'report.json',dict(status='ready',applied=applied,outputSkin=str(output),actions=reports))
    badge='已应用的正式动作' if applied else '正式动作 · 已验证待安装'
    document='''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>龙娘正式点击互动动作</title><style>*{box-sizing:border-box}body{margin:0;background:#f3f4f8;color:#242938;font:16px/1.6 "Microsoft YaHei",sans-serif}main{max-width:1184px;margin:auto;padding:28px 16px 60px}h1{font-size:28px;margin-bottom:8px}.intro,.caption{color:#596074}.badge{display:inline-block;background:#e3edf9;color:#295586;padding:3px 12px;border-radius:20px;font-size:14px}section{background:#fff;border:1px solid #dddfea;border-radius:16px;margin-top:24px;padding:18px}h2{margin:0 0 14px;font-size:21px}.labels{display:grid;grid-template-columns:repeat(3,1fr);text-align:center;gap:8px;margin:8px 0}video,img{display:block;width:100%;border-radius:8px}summary{cursor:pointer;color:#45536c}.caption,.toggle{font-size:14px}.toggle{display:inline-block;margin-top:10px;cursor:pointer}.cyan{color:#008ba8}.orange{color:#d26610}@media(max-width:600px){main{padding:16px 8px}section{padding:10px}.labels{font-size:12px}}</style>
<main><span class="badge">'''+badge+'''</span><h1>开心跳跃与害羞 · 正式动作对比</h1><p class="intro">三列同步展示完整的待机 → 动作 → 待机。采用已确认的色调与头部、双脚锚点效果，保留高度、真实跳跃和完整特效。</p><p class="caption"><span class="cyan">青色：待机基准。</span><span class="orange">橙色：动作站姿标定点，仅首尾各 5 帧显示。</span> 可关闭标记观察实际衔接。</p>'''+''.join(cards)+'''
<p class="caption">视频显示的是对比过程；两个桌宠动作均为单次播放。右键菜单中的点击互动按「开心跳跃、害羞」排列；点击随机触发沿用当前程序。</p></main><script>document.querySelectorAll('section').forEach(section=>{const video=section.querySelector('video'),toggle=section.querySelector('input');toggle.addEventListener('change',()=>{const time=video.currentTime,paused=video.paused;video.src=section.dataset.key+(toggle.checked?'-anchors.mp4':'-comparison.mp4');video.addEventListener('loadedmetadata',()=>{video.currentTime=Math.min(time,video.duration);if(!paused)video.play()},{once:true})})});</script></html>'''
    (FOLDER/'index.html').write_text(document,encoding='utf-8')
    print(json.dumps(dict(preview=str(FOLDER/'index.html'),applied=applied,frames={k:v['frames'] for k,v in reports.items()}),ensure_ascii=False))


if __name__=='__main__':main()

