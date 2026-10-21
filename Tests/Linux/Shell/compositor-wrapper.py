#!/usr/bin/python3
"""Resize only the isolated GNOME test compositor's virtual output."""
import os
import sys
args = sys.argv[1:]
for index, value in enumerate(args):
    if value == '--virtual-monitor':
        args[index + 1] = os.environ.get('PERSONALTOOLS_TEST_SIZE', '1488x1056')
if second := os.environ.get('PERSONALTOOLS_TEST_SECOND_MONITOR'):
    args += ['--virtual-monitor',second]
os.execv(args[0], args)
