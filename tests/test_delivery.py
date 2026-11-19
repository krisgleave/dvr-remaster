"""Real exports verify resolution, codecs, panel order, timing and saturation."""
import contextlib
import io
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

import cv2
import numpy as np
from flugbild.bridge import run, display_frame
from flugbild.delivery import delivery_size
from flugbild.look import Look, look_frame
from flugbild.media import tools_available, VideoKenndaten
from flugbild.recipe import Arbeitsrezept


class DeliverySettingsTests(unittest.TestCase):
    def test_aspect_preserving_sizes(self):
        self.assertEqual(delivery_size(4/3, 720), (960, 720))
        self.assertEqual(delivery_size(4/3, 1080, True), (2880, 1080))
        self.assertEqual(delivery_size(16/9, 1080), (1920, 1080))
        self.assertEqual(delivery_size(5/4, 1080), (1350, 1080))
        self.assertEqual(delivery_size(5/4, 2160), (2700, 2160))
        Arbeitsrezept(output_height=2160).validate()

    def test_anamorphic_preview(self):
        info = VideoKenndaten(1, 1920, 1080, False, sar=45/64)
        frame = np.zeros((1080, 1920, 3), np.uint8)
        self.assertEqual(display_frame(frame, info).shape, (1080, 1350, 3))

    def test_saturation_only_increases_colour_not_grey(self):
        grey = np.full((32, 32, 3), 100, np.uint8)
        np.testing.assert_array_equal(look_frame(grey, Look(saturation=1.2)), grey)
        colour = np.full((32, 32, 3), (70, 100, 140), np.uint8)
        self.assertIs(look_frame(colour, Look()), colour)
        boosted = look_frame(colour, Look(saturation=1.2))
        self.assertGreater(float(boosted.std()), float(colour.std()))

    def test_new_settings_round_trip_and_validation(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/'recipe.json'
            plan = Arbeitsrezept(output_height=720, comparison=True, saturation=1.5, remove_audio=True, brightness=1.2, contrast=1.1, gamma=1.4, spatial=.3)
            plan.save(path)
            self.assertEqual(Arbeitsrezept.load(path), plan)
        for saturation in (0.0, 1.0, 1.5, 2.0):
            Arbeitsrezept(saturation=saturation).validate()
        for options in [dict(remove_audio="yes"), dict(output_height=9000), dict(comparison=True),dict(saturation=float('nan')),dict(saturation=-0.01), dict(saturation=2.01),
                        dict(brightness=.49), dict(contrast=1.51), dict(gamma=2.01),
                        dict(spatial=-.01), dict(spatial=1.01)]:
            with self.assertRaises(ValueError):
                Arbeitsrezept(**options).validate()


@unittest.skipUnless(tools_available(), 'FFmpeg required')
class DeliveryExportTests(unittest.TestCase):
    def test_codecs_resolutions_audio_and_sync(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            source = folder/'source.mov'
            # Moving pattern, 4:3, fractional rate, audio deliberately shorter than picture.
            subprocess.run(['ffmpeg','-v','error','-y','-f','lavfi','-i',
                            'testsrc2=size=160x120:rate=60000/1001:duration=0.8',
                            '-f','lavfi','-i','sine=frequency=440:duration=0.3',
                            '-c:v','libx264','-crf','12','-c:a','aac',str(source)],check=True)
            cases=[('mp4-h264',720,False),('mp4-h264',720,True),
                   ('mp4-hevc',1080,False),('mov-prores',1080,True)]
            for form,height,comparison in cases:
                with self.subTest(form=form,height=height,comparison=comparison):
                    target=folder/(f'{form}-{comparison}'+('.mov' if form=='mov-prores' else '.mp4'))
                    request={'action':'export','source':str(source),'target':str(target),'mode':'manual',
                             'segments':[[0.1001,0.3003],[0.5005,0.7007]],
                             'recipe':{'signalart':'analog','farbberuhigung':0,'kantendetail':0,'aufwand':'fast',
                                       'output_height':height,'comparison':comparison,'dateiformat':form}}
                    with contextlib.redirect_stdout(io.StringIO()): run(request)
                    data=json.loads(subprocess.check_output(['ffprobe','-v','error','-count_frames',
                         '-show_streams','-show_format','-of','json',str(target)]))
                    v=next(s for s in data['streams'] if s['codec_type']=='video')
                    self.assertTrue(any(s['codec_type']=='audio' for s in data['streams']))
                    self.assertEqual((v['width'],v['height']),delivery_size(4/3,height,comparison))
                    self.assertEqual(v['codec_name'],{'mp4-h264':'h264','mp4-hevc':'hevc','mov-prores':'prores'}[form])
                    num,den=map(int,v['r_frame_rate'].split('/'))
                    self.assertAlmostEqual(num/den,60000/1001,places=3)
                    self.assertEqual(int(v['nb_read_frames']),24)
                    self.assertAlmostEqual(float(data['format']['duration']),24/(60000/1001),delta=.06)
                    if comparison:
                        from flugbild.media import decode_frames
                        frames = decode_frames(target, 0, v['width'], v['height'], count=26)
                        count = 0
                        for f in frames:
                            w = f.shape[1]//2
                            delta = np.abs(f[80:,:w].astype(float)-f[80:,w:].astype(float)).mean()
                            self.assertLess(delta,4.0, f'Frame {count} is out of sync: {delta}')
                            count += 1
                        self.assertEqual(count,24)
            self.assertFalse(list(folder.glob('.fpv-render-*')), 'Temporary frames were not cleaned up')

    def test_remove_audio_all_formats_and_layouts(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            source = folder/'with-audio.mov'
            subprocess.run(['ffmpeg','-v','error','-y','-f','lavfi','-i',
                            'testsrc2=size=160x120:rate=25:duration=0.24',
                            '-f','lavfi','-i','sine=frequency=440:duration=0.24',
                            '-c:v','libx264','-c:a','aac',str(source)],check=True)
            for form in ('mp4-h264','mp4-hevc','mov-prores'):
                for comparison in (False, True):
                    with self.subTest(form=form, comparison=comparison):
                        target=folder/(f'{form}-{comparison}'+('.mov' if form=='mov-prores' else '.mp4'))
                        with contextlib.redirect_stdout(io.StringIO()):
                            run({'action':'export','source':str(source),'target':str(target),'mode':'manual',
                                 'segments':[[0,.12],[.12,.24]],
                                 'recipe':{'signalart':'analog','farbberuhigung':0,'kantendetail':0,
                                           'output_height':720,'comparison':comparison,
                                           'dateiformat':form,'remove_audio':True}})
                        data=json.loads(subprocess.check_output(['ffprobe','-v','error','-count_frames',
                            '-show_streams','-of','json',str(target)]))
                        self.assertEqual([x['codec_type'] for x in data['streams']], ['video'])
                        self.assertEqual(int(data['streams'][0]['nb_read_frames']),6)

    def test_original_left_and_saturated_clean_right(self):
        with tempfile.TemporaryDirectory() as folder:
            folder=Path(folder);source=folder/'colour.mp4';target=folder/'comparison.mp4'
            subprocess.run(['ffmpeg','-v','error','-y','-f','lavfi','-i',
                            'color=c=0x996644:size=160x120:rate=25:duration=0.2','-c:v','libx264',str(source)],check=True)
            with contextlib.redirect_stdout(io.StringIO()):
                run({'action':'export','source':str(source),'target':str(target),'mode':'manual',
                     'recipe':{'signalart':'analog','farbberuhigung':0,'kantendetail':0,
                               'saturation':1.25,'output_height':720,'comparison':True}})
            cap=cv2.VideoCapture(str(target));ok,f=cap.read();cap.release()
            self.assertTrue(ok)
            cc=cv2.cvtColor(f,cv2.COLOR_BGR2YCrCb).astype(float)
            left=np.abs(cc[100:,:960,1:]-128).mean()
            right=np.abs(cc[100:,960:,1:]-128).mean()
            self.assertGreater(right,left*1.15)
            # Text produces a visible label in each top-left region.
            self.assertGreater(float(f[:65,:220].std()),20)
            self.assertGreater(float(f[:65,960:1180].std()),20)

    def test_silent_final_has_no_labels(self):
        with tempfile.TemporaryDirectory() as folder:
            folder=Path(folder);source=folder/'flat.mp4';target=folder/'final.mp4'
            subprocess.run(['ffmpeg','-v','error','-y','-f','lavfi','-i',
                            'color=c=0x666666:size=160x120:rate=25:duration=0.2','-c:v','libx264',str(source)],check=True)
            with contextlib.redirect_stdout(io.StringIO()):
                run({'action':'export','source':str(source),'target':str(target),'mode':'manual',
                     'recipe':{'signalart':'neutral','kantendetail':0,'output_height':720}})
            cap=cv2.VideoCapture(str(target));ok,f=cap.read();cap.release()
            self.assertTrue(ok); self.assertLess(float(f.std()),2)
            data=json.loads(subprocess.check_output(['ffprobe','-v','error','-show_streams','-of','json',str(target)]))
            self.assertFalse(any(s['codec_type']=='audio' for s in data['streams']))

if __name__=='__main__': unittest.main()
