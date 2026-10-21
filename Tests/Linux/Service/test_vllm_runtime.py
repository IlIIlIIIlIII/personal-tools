#!/usr/bin/env python3
"""Network lifecycle regression tests; no model or GPU allocation required."""
import importlib.util
import pathlib
import socket
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location('vllm_runtime', ROOT / 'Sources/Linux/Vllm/runtime.py')
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)


class PortOwnershipTests(unittest.TestCase):
    def test_live_foreign_listener_is_rejected(self):
        with socket.socket() as server:
            server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            server.bind(('127.0.0.1', 0))
            server.listen(1)
            with self.assertRaisesRegex(RuntimeError, 'unowned server'):
                runtime.assert_port_available(number=server.getsockname()[1])
            # A failed probe must neither stop nor adopt the listener.
            with socket.create_connection(server.getsockname()) as client:
                accepted, _ = server.accept()
                accepted.close()

    def test_closed_http_connection_allows_immediate_restart(self):
        with socket.socket() as server:
            server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            server.bind(('127.0.0.1', 0))
            address = server.getsockname()
            server.listen(1)
            with socket.create_connection(address) as client:
                accepted, _ = server.accept()
                # Server closes first, leaving its local port in TIME_WAIT.
                accepted.shutdown(socket.SHUT_WR)
                self.assertEqual(client.recv(1), b'')
                accepted.close()
        runtime.assert_port_available(number=address[1])


if __name__ == '__main__':
    unittest.main()
