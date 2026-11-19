"""Exact-height exports, preserving source display aspect and nominal frame rate.

Repair at source resolution into a lossless temporary clip, then resize/compose
and encode once to the requested delivery codec. Each kept segment follows this
path independently so the original and cleaned panels share the same cut.
"""
from dataclasses import replace
from fractions import Fraction
from pathlib import Path
import json
import shutil
import subprocess
import tempfile

from .media import PROCESS_FLAGS, inspect_video
from .processor import publish_output
from .processor import (Laufergebnis, diagnostic_tail, encoding_options, find_asset,
                        output_duration, temporary_output)


def display_aspect(source, info):
    import json
    result = subprocess.run(['ffprobe', '-v', 'error', '-select_streams', 'v:0',
                             '-show_entries', 'stream=display_aspect_ratio', '-of', 'json', str(source)],
                            capture_output=True, text=True, check=True, creationflags=PROCESS_FLAGS)
    streams = json.loads(result.stdout).get('streams', [])
    try:
        ratio = Fraction(streams[0].get('display_aspect_ratio', '').replace(':', '/'))
        if ratio > 0:
            return float(ratio)
    except (ValueError, ZeroDivisionError, IndexError):
        pass
    return info.width * info.sar / info.height


def delivery_size(aspect, height, comparison=False):
    width = max(2, round(height * aspect / 2) * 2)
    return width * (2 if comparison else 1), height


def deliver_cut(source, target, recipe, info, render, progress, cancel):
    recipe.validate()
    source, target = Path(source).resolve(), Path(target).resolve()
    if source == target:
        raise ValueError('Source and destination must differ')
    target.parent.mkdir(parents=True, exist_ok=True)
    duration = output_duration(info, recipe)
    # Native-size fusion restores detail before the final Lanczos upscale.
    intermediate = replace(recipe, output_height=0, comparison=False, dateiformat='mkv-ffv1',
                           vergroesserung_prozent=100, dateiziel_mb=0, untertiteltext='')
    with tempfile.TemporaryDirectory(prefix='.fpv-render-', dir=target.parent) as folder:
        folder = Path(folder)
        clean = folder / 'clean.mkv'
        outcome = render(source, clean, intermediate, lambda value: progress(value * .8))
        if not outcome.success or cancel.is_set():
            return outcome if not outcome.success else Laufergebnis(source, target, False, 'Canceled')
        made = inspect_video(clean)
        counted = subprocess.check_output(['ffprobe', '-v', 'error', '-select_streams', 'v:0',
                                           '-count_packets', '-show_entries', 'stream=nb_read_packets',
                                           '-of', 'json', str(clean)])
        frame_count = int(json.loads(counted)['streams'][0]['nb_read_packets'])
        video_duration = frame_count / made.fps
        height = recipe.output_height
        width, _ = delivery_size(display_aspect(source, info), height)
        fps = str(Fraction(made.fps).limit_denominator(100000))
        args = ['ffmpeg', '-hide_banner', '-nostdin', '-y', '-loglevel', 'error', '-progress', 'pipe:1',
                '-i', str(clean)]
        base = f'scale={width}:{height}:flags=lanczos,setsar=1,settb=AVTB,setpts=N/({fps}*TB)'
        if recipe.comparison:
            # Match the repair decoder's frame grid, including its lead-in context
            # and fractional cut rounding, rather than seeking panels differently.
            from .restoration import CONTEXT_SECONDS
            lead_frames = round(min(recipe.schnittbeginn, CONTEXT_SECONDS) * info.fps)
            start = max(0.0, recipe.schnittbeginn - lead_frames / info.fps)
            args += ['-ss', f'{start:.6f}', '-i', str(source)]
            shutil.copyfile(find_asset('Inter.ttf'), folder / 'Inter.ttf')
            label = f'drawtext=fontfile=Inter.ttf:fontsize={max(18, height//32)}:fontcolor=white:box=1:boxcolor=black@0.65:boxborderw=8:x=16:y=16:text='
            filters = (f'[0:v]{base},{label}CLEANED[c];'
                       f'[1:v]fps={info.fps},trim=start_frame={lead_frames},setpts=PTS-STARTPTS,{base},{label}ORIGINAL[o];'
                       '[o][c]hstack=inputs=2:shortest=1[out]')
            args += ['-filter_complex', filters, '-map', '[out]']
        else:
            args += ['-map', '0:v:0', '-vf', base]
        args += ['-map', '0:a?', '-t', str(video_duration), '-frames:v', str(frame_count), '-r', fps]
        args += encoding_options(recipe, made, video_duration)
        pending = temporary_output(target)
        args += [str(pending)]
        log = tempfile.TemporaryFile(mode='w+b')
        proc = None
        try:
            proc = subprocess.Popen(args, cwd=folder, stdout=subprocess.PIPE, stderr=log,
                                    text=True, creationflags=PROCESS_FLAGS)
            for line in proc.stdout:
                if cancel.is_set():
                    proc.terminate()
                    break
                if line.startswith('out_time_us='):
                    try:
                        value = int(line.split('=', 1)[1]) / 1e6
                        progress(.8 + .2 * min(1., value / max(made.duration, .001)))
                    except ValueError:
                        pass
            proc.wait(timeout=30)
            if cancel.is_set():
                return Laufergebnis(source, target, False, 'Canceled')
            if proc.returncode:
                return Laufergebnis(source, target, False, diagnostic_tail(log) or 'Export encoding failed')
            publish_output(pending, target)
            progress(1.)
            return Laufergebnis(source, target, True, 'Done')
        finally:
            if proc is not None:
                if proc.poll() is None:
                    proc.kill()
                    proc.wait()
                if proc.stdout:
                    proc.stdout.close()
            log.close()
            pending.unlink(missing_ok=True)
