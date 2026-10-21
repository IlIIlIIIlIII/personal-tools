import json
import pathlib
import struct
import subprocess
import sys
import tempfile
import unittest


class ReaderTest(unittest.TestCase):
    def test_event_summary_and_latency_aligned_wave(self):
        script = pathlib.Path(__file__).resolve().parents[1]/'Scripts/read-aec-diagnostics.py'
        header = struct.Struct('<4I7Q')
        with tempfile.TemporaryDirectory(prefix='aec-reader-test-') as folder:
            root = pathlib.Path(folder)
            with (root/'aec-1-123-0.acd').open('wb') as f:
                def record(kind, frames=0, time=0, a=0, b=0, c=0, d=0, payload=b''):
                    f.write(header.pack(0x31444341, kind, frames, 0, 10000000, time, 0, a, b, c, d))
                    f.write(payload)
                record(6, a=10000000, b=134000000000000000, c=4096, d=48000)
                record(1, 256, 20000000, payload=struct.pack('<512f', *([.25, -.5]*256)))
                record(2, 256, 20000000, payload=struct.pack('<512f', *([.125, -.25]*256)))
                record(8, a=19, b=100, c=0, d=99)
                record(3, c=0, d=(7 << 32)|3)
                record(4, a=100000, b=120000, c=1)
                record(12, 128, payload=b'OpenVINO NPU error -1\0'.ljust(1024, b'\0'))
                record(9, a=2**64-1)
                f.write(bytes(72))
            result = subprocess.run([sys.executable, str(script), str(root), '--wav-seconds', '1'], check=True, capture_output=True, text=True)
            summary, _ = json.JSONDecoder().raw_decode(result.stdout)
            self.assertEqual(summary['missing_frames'], 19)
            self.assertEqual(summary['reference_unavailable_packets'], 1)
            self.assertEqual(summary['max_queue_depth_mic_ref'], [7, 3])
            self.assertEqual(summary['max_inference_ms'], 2)
            self.assertEqual(summary['errors'], ['OpenVINO NPU error -1'])
            self.assertEqual(summary['states']['-1'], 1)
            wav = next(root.glob('*.wav')).read_bytes()
            self.assertEqual(len(wav), 44+48000*16)
            raw_start = 48000-256
            self.assertEqual(struct.unpack_from('<f', wav, 44+raw_start*16)[0], .25)
            self.assertEqual(struct.unpack_from('<f', wav, 44+(raw_start-4096)*16+4)[0], -.5)
            self.assertEqual(struct.unpack_from('<f', wav, 44+raw_start*16+8)[0], .125)


if __name__ == '__main__':
    unittest.main()
