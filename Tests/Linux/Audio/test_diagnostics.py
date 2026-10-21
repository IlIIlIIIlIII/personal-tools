import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('diagnostics', Path(__file__).resolve().parents[3] / 'Scripts/linux-audio-diagnostics.py')
diagnostics = importlib.util.module_from_spec(spec)
spec.loader.exec_module(diagnostics)


class DiagnosticsTests(unittest.TestCase):
    def row(self, errors, state='R', name=' + personal-tools-eq-process'):
        return diagnostics.parse_top(f'{state} 51 256 48000 1.2us 5.1us 0.00 0.00 {errors} {name}')

    def test_driver_and_follower_formats(self):
        self.assertEqual(self.row(4)['errors'], 4)
        self.assertEqual(self.row(4, name='S32LE 2 48000 alsa_input.hw_M2_0')['quantum'], 256)

    def test_header_and_invalid_lines(self):
        self.assertIsNone(diagnostics.parse_top('S ID QUANT RATE WAIT BUSY W/Q B/Q ERR FORMAT NAME'))
        self.assertIsNone(diagnostics.parse_top('connection error'))

    def test_baseline_is_not_new_xrun(self):
        self.assertIsNone(diagnostics.error_delta(None, self.row(200)))
        self.assertIsNone(diagnostics.error_delta(self.row(0, 'C'), self.row(200)))

    def test_increase_and_counter_reset(self):
        self.assertEqual(diagnostics.error_delta(self.row(7), self.row(9)), 2)
        self.assertIsNone(diagnostics.error_delta(self.row(9), self.row(0)))
        self.assertIsNone(diagnostics.error_delta(self.row(9), self.row(9)))

    def test_reused_id(self):
        self.assertIsNone(diagnostics.error_delta(self.row(2), self.row(30, name='other-node')))


if __name__ == '__main__':
    unittest.main()
