"""v1.2 FFmpeg end-to-end regression, no dotnet required.

Command: python tests/ffmpeg_smoke.py

Tests: (a) multiple MP3 => one continuous audio, (b) different bitrates/rates,
(c) two normalized video clips, each with its own speed, (d) ordered playlist
loops from video 1, (e) one MP4 output at full audio duration, (f) 2 PNG overlays,
(g) muted source / source-audio mix / MP3 shorter than video.
"""
from pathlib import Path
from tempfile import TemporaryDirectory
import json
import math
import subprocess
import sys


def call(*args):
    cmd = [str(x) for x in args]
    result = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode:
        raise RuntimeError(f"Command failed: {' '.join(cmd)}\n{result.stderr.decode(errors='replace')[-3500:]}")
    return result.stdout


def info(path):
    return json.loads(call('ffprobe', '-v', 'error', '-show_entries',
        'format=duration:stream=codec_type,width,height', '-of', 'json', path))


def duration(path):
    return float(info(path)['format']['duration'])


def normalize_mp3(mp3, out):
    call('ffmpeg','-nostdin','-v','error','-y','-i',mp3,'-map','0:a:0','-vn','-sn','-dn',
         '-af','aresample=48000:async=1:first_pts=0,aformat=sample_fmts=s16:channel_layouts=stereo',
         '-c:a','flac','-compression_level','5',out)


def normalize_video(src, out, speed, source_audio, muted):
    length = f'{duration(src)/speed:.6f}'
    flt = f'setpts=(PTS-STARTPTS)/{speed:g},fps=30,scale=160:90:force_original_aspect_ratio=decrease:flags=lanczos,pad=160:90:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p,tpad=stop_mode=clone:stop_duration=0.10,trim=duration={length},setpts=PTS-STARTPTS'
    args = ['ffmpeg','-nostdin','-v','error','-y','-i',str(src)]
    if not muted and not source_audio:
        args += ['-f','lavfi','-i','anullsrc=channel_layout=stereo:sample_rate=48000']
    if muted:
        args += ['-map','0:v:0','-an','-sn','-dn','-vf',flt]
    else:
        atempo = 'atempo=0.5,atempo=0.8' if speed==.4 else f'atempo={speed:g}'
        astart = f'[0:a:0]{atempo},aresample=48000:async=1:first_pts=0,' if source_audio else '[1:a]aresample=48000:async=1:first_pts=0,'
        graph = f'[0:v]{flt}[vnorm];'+astart+f'aformat=channel_layouts=stereo,apad,atrim=duration={length},asetpts=PTS-STARTPTS[anorm]'
        args += ['-filter_complex',graph,'-map','[vnorm]','-map','[anorm]']
    args += ['-c:v','libx264','-preset','ultrafast','-crf','21','-pix_fmt','yuv420p',
             '-r','30','-video_track_timescale','30000']
    if not muted:
        args += ['-c:a','aac','-b:a','160k','-ar','48000','-ac','2']
    args += ['-t',length,'-movflags','+faststart',str(out)]
    call(*args)


def concatfile(path,dirname,files):
    path.write_text('ffconcat version 1.0\n'+''.join(f"file '{dirname}/{f.name}'\n" for f in files), encoding='utf8')


def sample_pixel(video, seconds, x=80, y=45):
    result = call('ffmpeg','-v','error','-ss',str(seconds),'-i',video,
                  '-vf',f'crop=1:1:{x}:{y},format=rgb24','-vframes','1',
                  '-f','rawvideo','-pix_fmt','rgb24','-')
    if len(result) < 3:
        raise RuntimeError(f'No frame at {seconds}s')
    return tuple(result[:3])


def measure_tone(video, start, frequency):
    # Goertzel magnitude for a short decoded 8k mono window (stdlib only).
    raw = call('ffmpeg','-v','error','-ss',str(start),'-i',video,
               '-vn','-t','0.30','-ac','1','-ar','8000','-f','s16le','-')
    from array import array
    pcm=array('h'); pcm.frombytes(raw)
    n=len(pcm)
    if n==0: raise RuntimeError('No audio decoded')
    w=2*math.pi*frequency/8000
    coeff=2*math.cos(w)
    q1=q2=0.0
    for sample in pcm:
        q0=sample+coeff*q1-q2
        q2,q1=q1,q0
    return math.sqrt(max(0,q1*q1+q2*q2-coeff*q1*q2))


def run_case(root, short=False, muted=True):
    aud=root/'audio';clips=root/'clips';aud.mkdir(exist_ok=True);clips.mkdir(exist_ok=True)
    mp3s=[]
    mp3_spec=[(440,1.00,44100),(880,1.05,48000)] if short else [(440,3.4,44100),(880,3.3,48000)]
    for i,(hz,secs,rate) in enumerate(mp3_spec):
        path=root/f'mp3_{i}.mp3';mp3s.append(path)
        call('ffmpeg','-v','error','-y','-f','lavfi','-i',f'sine=frequency={hz}:duration={secs}:sample_rate={rate}',
             '-c:a','libmp3lame','-q:a','3',path)
    segs=[]
    for i,mp3 in enumerate(mp3s):
        target=aud/f'{i+1:05}.flac'
        normalize_mp3(mp3,target);segs.append(target)
    concatfile(root/'audio.ffconcat','audio',segs)
    joined=root/'joined_audio.m4a'
    call('ffmpeg','-v','error','-y','-safe','0','-f','concat','-i',root/'audio.ffconcat',
         '-map','0:a:0','-vn','-sn','-dn','-c:a','aac','-b:a','192k','-ar','48000','-ac','2',
         '-movflags','+faststart',joined)
    audio_dur=duration(joined)
    assert abs(audio_dur-sum(s for _,s,_ in mp3_spec)) < .08,(audio_dur,mp3_spec)

    files=[]
    for i,(color,has_audio,speed) in enumerate([('blue',True,.5),('red',False,1.0)]):
        src=root/f'video_{i}.mp4'
        a=['ffmpeg','-v','error','-y','-f','lavfi','-i',f'color=c={color}:s=160x90:r={24+i}:d=1']
        if has_audio:
            a+=['-f','lavfi','-i','sine=frequency=220:duration=1','-c:a','aac','-shortest']
        a+=['-c:v','libx264','-preset','ultrafast',src];call(*a)
        dest=clips/f'{i+1:05}.mp4'
        normalize_video(src,dest,speed,has_audio,muted);files.append(dest)
    concatfile(root/'playlist.ffconcat','clips',files)

    # Reuse bundled overlay fixtures when available (format, alpha and corner checks).
    asset_dir=Path(__file__).resolve().parents[2] / 'ffmpeg_test'
    logos=[str(asset_dir/'overlay1.png'),str(asset_dir/'overlay2.png')]
    if not all(Path(x).exists() for x in logos):
        logos=[]
        for i in range(2):
            logo=root/f'overlay{i}.png'; logos.append(str(logo))
            call('ffmpeg','-v','error','-y','-f','lavfi','-i',f'color=c={"yellow" if i==0 else "green"}:s=24x24:d=1',
                 '-frames:v','1','-update','1',logo)
    out=root/('short.mp4' if short else ('mixed.mp4' if not muted else 'muted.mp4'))
    a=['ffmpeg','-v','error','-y','-stream_loop','-1','-safe','0','-f','concat','-i',root/'playlist.ffconcat','-i',joined]
    for logo in logos:
        a+=['-loop','1','-framerate','30','-i',logo]
    graph=('[0:v]setpts=PTS-STARTPTS[v0];'
           '[2:v]scale=42:42:force_original_aspect_ratio=decrease:flags=lanczos,format=rgba[logo0];'
           '[v0][logo0]overlay=x=3:y=H-h-3:eof_action=repeat:format=auto[v1];'
           '[3:v]scale=42:42:force_original_aspect_ratio=decrease:flags=lanczos,format=rgba[logo1];'
           '[v1][logo1]overlay=x=W-w-3:y=H-h-3:eof_action=repeat:format=auto[v2];'
           '[v2]format=yuv420p[vout]')
    if not muted:
        graph+=(';[0:a:0]aresample=async=1:first_pts=0,volume=0.20[gameaudio]'
               ';[1:a:0]aresample=async=1:first_pts=0,apad[voice]'
               ';[voice][gameaudio]amix=inputs=2:duration=longest:dropout_transition=0:normalize=0,aresample=async=1[aout]')
    a+=['-filter_complex',graph,'-map','[vout]','-map','1:a:0' if muted else '[aout]',
        '-c:v','libx264','-preset','ultrafast','-crf','20',
        '-c:a','copy' if muted else 'aac']
    if not muted: a+=['-b:a','192k','-ar','48000']
    a+=['-pix_fmt','yuv420p','-r','30','-t',f'{audio_dur:.6f}','-map_metadata','-1',
        '-map_chapters','-1','-movflags','+faststart',out]
    call(*a)
    result=info(out)
    assert abs(duration(out)-audio_dur)<.09,(out, duration(out),audio_dur)
    assert sorted(s['codec_type'] for s in result['streams'])==['audio','video'],result
    if not short:
        blue1=sample_pixel(out,1.1)
        red1=sample_pixel(out,2.5)
        blue2=sample_pixel(out,3.6)
        red2=sample_pixel(out,5.5)
        assert blue1[2]>blue1[0]+50 and blue2[2]>blue2[0]+50,(blue1,blue2)
        assert red1[0]>red1[2]+50 and red2[0]>red2[2]+50,(red1,red2)
        # MP3 1 then MP3 2, never overlapping (unless intentionally mixed source).
        p1=(measure_tone(out,1.1,440),measure_tone(out,1.1,880))
        p2=(measure_tone(out,4.5,440),measure_tone(out,4.5,880))
        assert p1[0]>4*p1[1] and p2[1]>4*p2[0],(p1,p2)
        assert duration(out)>duration(files[0])+duration(files[1])+2
    print('PASS:',out.name, f'audio={audio_dur:.3f}s', f'mp4={duration(out):.3f}s')


def main():
    with TemporaryDirectory(prefix='lkhk-v12-') as tmp:
        root=Path(tmp)
        for mode in ('muted','mixed','short'):
            folder=root/mode;folder.mkdir()
            run_case(folder,short=(mode=='short'),muted=(mode!='mixed'))
        # Preserve v1.1's entire 6-speed coverage, with both video and audio.
        speed_root=root/'speeds'; speed_root.mkdir()
        src=speed_root/'speed_test.mp4'
        call('ffmpeg','-v','error','-y','-f','lavfi','-i','color=c=blue:s=160x90:r=24:d=0.9',
             '-f','lavfi','-i','sine=frequency=500:duration=0.9',
             '-c:v','libx264','-preset','ultrafast','-c:a','aac','-shortest',src)
        for i,speed in enumerate((0.4,0.5,0.75,1.0,1.5,2.0)):
            out=speed_root/f'speed_{i}.mp4'
            normalize_video(src,out,speed,source_audio=True,muted=False)
            assert abs(duration(out)-duration(src)/speed) < .075, (speed,duration(out))
        print('PASS: all 6 independent playback speeds [0.4, 0.5, 0.75, 1, 1.5, 2]')
    print('PASS: exactly one MP4 per render, order of 2 MP3, loop of 2 videos, independent speeds, two overlays, muted/mixed, cut to joined audio')


if __name__=='__main__':
    try: main()
    except Exception as e:
        print('FAIL:',e,file=sys.stderr)
        sys.exit(1)
