import unittest

import cv2
import numpy as np

from flugbild.analog import repair_stream


def moving_scene(count: int) -> list[np.ndarray]:
    """Fine brightness detail with coarse colour areas, like a real analog picture."""
    random = np.random.default_rng(5)
    luma = cv2.resize(random.integers(50, 200, size=(24, 40), dtype=np.uint8), (320, 192),
                      interpolation=cv2.INTER_CUBIC)
    colour = cv2.resize(random.integers(70, 186, size=(5, 8, 2), dtype=np.uint8), (320, 192),
                        interpolation=cv2.INTER_CUBIC)
    base = cv2.cvtColor(np.dstack([luma, colour]), cv2.COLOR_YCrCb2BGR)
    wide = cv2.copyMakeBorder(base, 0, 0, 0, 40, cv2.BORDER_REFLECT)
    return [wide[:, 2 * index:2 * index + 320].copy() for index in range(count)]


def chroma(frame: np.ndarray) -> np.ndarray:
    return cv2.cvtColor(frame, cv2.COLOR_BGR2YCrCb).astype(np.float32)[:, :, 1:]


class AnalogRepairTest(unittest.TestCase):
    def test_black_and_white_frame_gets_colour_back(self):
        clean = moving_scene(9)
        damaged = [frame.copy() for frame in clean]
        damaged[4] = cv2.cvtColor(cv2.cvtColor(clean[4], cv2.COLOR_BGR2GRAY), cv2.COLOR_GRAY2BGR)
        repaired = list(repair_stream(damaged, 50))
        self.assertEqual(len(repaired), 9)
        before = np.abs(chroma(damaged[4]) - chroma(clean[4]))[8:-8, 16:-16].mean()
        after = np.abs(chroma(repaired[4]) - chroma(clean[4]))[8:-8, 16:-16].mean()
        self.assertLess(after, before * 0.5)
        luma_change = np.abs(cv2.cvtColor(repaired[4], cv2.COLOR_BGR2GRAY).astype(int)
                             - cv2.cvtColor(damaged[4], cv2.COLOR_BGR2GRAY)).mean()
        self.assertLess(luma_change, 3)

    def test_rainbow_band_is_removed(self):
        clean = moving_scene(9)
        damaged = [frame.copy() for frame in clean]
        ycc = cv2.cvtColor(clean[4], cv2.COLOR_BGR2YCrCb).astype(np.float32)
        hue = np.linspace(0, 6 * np.pi, ycc.shape[1])
        ycc[90:98, :, 1] = 128 + 70 * np.cos(hue)
        ycc[90:98, :, 2] = 128 + 70 * np.sin(hue)
        damaged[4] = cv2.cvtColor(np.clip(ycc, 0, 255).astype(np.uint8), cv2.COLOR_YCrCb2BGR)
        repaired = list(repair_stream(damaged, 50))
        band = (slice(90, 98), slice(16, -16))
        before = np.abs(chroma(damaged[4]) - chroma(clean[4]))[band].mean()
        after = np.abs(chroma(repaired[4]) - chroma(clean[4]))[band].mean()
        self.assertLess(after, before * 0.4)

    def test_short_white_band_responds_to_strength(self):
        base = np.full((96, 160, 3), 90, np.uint8)
        frames = [base.copy() for _ in range(7)]
        frames[3][40:54] = 245
        light = list(repair_stream(frames, 25))[3]
        strong = list(repair_stream(frames, 100))[3]
        self.assertLess(strong[44:50].mean(), light[44:50].mean() - 30)
        self.assertLess(np.abs(strong[:20].astype(float) - base[:20]).mean(), 3)

    def test_zero_amount_passes_frames_through(self):
        clean = moving_scene(3)
        self.assertTrue(all(a is b for a, b in zip(repair_stream(clean, 0), clean)))


class ColourRetentionTest(unittest.TestCase):
    def test_coloured_frame_survives_monochrome_neighbours(self):
        # A coloured frame surrounded by complete colour loss must not itself
        # be desaturated merely because its neighbours have no chroma evidence.
        frame = np.full((96, 160, 3), (80, 120, 165), np.uint8)
        grey = cv2.cvtColor(cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY), cv2.COLOR_GRAY2BGR)
        result = list(repair_stream([grey.copy() for _ in range(3)] + [frame] +
                                    [grey.copy() for _ in range(3)], 50))[3]
        before = np.abs(chroma(frame) - 128).mean()
        after = np.abs(chroma(result) - 128).mean()
        self.assertGreater(after, before * 0.95)

    def test_single_frame_keeps_colour(self):
        frame = np.full((96, 160, 3), (80, 120, 165), np.uint8)
        result = list(repair_stream([frame], 50))[0]
        self.assertLess(np.abs(result.astype(float) - frame).mean(), 2)

    def test_spatial_repair_without_evidence_retains_colour(self):
        from flugbild.analog import _luma_guided
        colour = np.full((48, 80), 25 + 12j, np.complex64)
        light = np.full(colour.shape, 120, np.float32)
        result = _luma_guided(colour, light, np.ones(colour.shape, np.float32))
        self.assertTrue(np.allclose(result, colour))

    def test_monochrome_footage_does_not_gain_invented_colour(self):
        frame = np.full((96, 160, 3), 120, np.uint8)
        result = list(repair_stream([frame.copy() for _ in range(7)], 50))[3]
        self.assertLess(np.abs(chroma(result) - 128).mean(), 1)


if __name__ == "__main__":
    unittest.main()
