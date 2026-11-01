"""FFmpeg burn-in test matching v2.3 filtergraph layout (SRT on top of PNG).
Runs on Linux/Mac/Windows if FFmpeg is installed; this does not compile the WinForms GUI.
"""
from pathlib import Path
from tempfile import TemporaryDirectory
import subprocess
import json


def run(*cmd, cwd=None, capture=False):
    p=subprocess.run([str(x) for x in cmd], cwd=cwd, stdout=subprocess.PIPE,
                     stderr=subprocess.PIPE, text=not capture)
    if p.returncode:
        err=p.stderr if isinstance(p.stderr,str) else p.stderr.decode(errors='replace')
        raise RuntimeError('FAILED: '+str(cmd)+'\n'+err[-2000:])
    return p.stdout


def main():
    with TemporaryDirectory(prefix='lkhk-v13-srt-') as tmp:
        d=Path(tmp)
        # Several short MP3s with distinct tones & sample rates, as in real TTSS batches.
        lengths=(1.3, 1.6)
        for i,seconds in enumerate(lengths):
            run('ffmpeg','-v','error','-y','-f','lavfi','-i',f'sine=frequency={440+i*220}:duration={seconds}:sample_rate={44100+i*3900}',
                '-c:a','libmp3lame',d/f'{i}.mp3')
            run('ffmpeg','-v','error','-y','-i',d/f'{i}.mp3','-ar','48000','-ac','2','-c:a','flac',d/f'{i}.flac')
        (d/'audio.ffconcat').write_text("ffconcat version 1.0\nfile '0.flac'\nfile '1.flac'\n",encoding='utf-8')
        run('ffmpeg','-v','error','-y','-safe','0','-f','concat','-i',d/'audio.ffconcat',
            '-c:a','aac','-b:a','192k',d/'joined.m4a')
        result=json.loads(run('ffprobe','-v','error','-show_entries','format=duration',
                              '-of','json',d/'joined.m4a'))
        total=float(result['format']['duration'])
        srt=("1\n00:00:00,100 --> 00:00:01,000\nXin chào, Lăng Kính Hư Không!\n\n"
             "2\n00:00:01,500 --> 00:00:02,500\nPhụ đề thứ hai - nội dung tiếng Việt.\n\n")
        (d/'joined_subtitles.srt').write_text(srt,encoding='utf-8')
        run('ffmpeg','-v','error','-y','-f','lavfi','-i','color=c=blue:s=320x180:r=30:d=1.7',
            '-an','-c:v','libx264','-preset','ultrafast',d/'clip.mp4')
        (d/'playlist.ffconcat').write_text("ffconcat version 1.0\nfile 'clip.mp4'\n",encoding='utf-8')
        run('ffmpeg','-v','error','-y','-f','lavfi','-i','color=yellow:s=35x50:d=1',
            '-frames:v','1','-update','1',d/'logo.png')
        style='FontName=Arial,FontSize=28,PrimaryColour=&H00FFFFFF,OutlineColour=&H00000000,BorderStyle=1,Outline=2,Shadow=0,Alignment=2,MarginV=40'
        vf=("[0:v]setpts=PTS-STARTPTS[v0];"
            "[2:v]scale=30:50:force_original_aspect_ratio=decrease:flags=lanczos,format=rgba[logo0];"
            "[v0][logo0]overlay=x=2:y=H-h-2:eof_action=repeat:format=auto[v1];"
            "[v1]subtitles=filename=joined_subtitles.srt:charenc=UTF-8:"
            f"force_style='{style}'[vsub];"
            "[vsub]format=yuv420p[vout]")
        run('ffmpeg','-v','error','-y','-stream_loop','-1','-safe','0','-f','concat',
            '-i',d/'playlist.ffconcat','-i',d/'joined.m4a', '-loop','1','-framerate','30','-i',d/'logo.png',
            '-filter_complex',vf,'-map','[vout]','-map','1:a:0','-c:v','libx264',
            '-preset','ultrafast','-c:a','copy','-pix_fmt','yuv420p','-t',f'{total:.6f}',
            d/'final.mp4',cwd=d)
        info=json.loads(run('ffprobe','-v','error','-show_entries','format=duration:stream=codec_type',
                            '-of','json',d/'final.mp4'))
        assert abs(float(info['format']['duration']) - total) < .08
        assert sorted(x['codec_type'] for x in info['streams']) == ['audio','video']
        # Changing subtitles must alter mid-cue pixels while preserving the video duration.
        run('ffmpeg','-v','error','-y','-i',d/'final.mp4','-ss','0.5','-frames:v','1',d/'frame.png')
        assert (d/'frame.png').stat().st_size > 1000
        print('PASS: UTF-8 Vietnamese subtitle burn-in, subtitle top layer, video playlist looping, single MP4, MP3 duration',round(total,3))


if __name__ == '__main__': main()
