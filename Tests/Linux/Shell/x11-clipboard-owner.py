#!/usr/bin/python3
"""Xwayland CLIPBOARD owner that lists text/plain;charset=UTF-8 but refuses it.

Some X11 toolkits advertise every charset variant in TARGETS and answer a
conversion they cannot produce with property None. Mode "partial" serves
UTF8_STRING and text/plain; mode "refuse-all" answers every data target None.
"""
import sys
from Xlib import X, Xatom, display
from Xlib.protocol import event

mode, text = sys.argv[1], sys.argv[2].encode()
connection = display.Display()
atom = connection.intern_atom
clipboard, targets, timestamp = atom('CLIPBOARD'), atom('TARGETS'), atom('TIMESTAMP')
utf8, plain, plain_utf8 = atom('UTF8_STRING'), atom('text/plain'), atom('text/plain;charset=UTF-8')
window = connection.screen().root.create_window(0, 0, 1, 1, 0, X.CopyFromParent, event_mask=X.PropertyChangeMask)

# Selection ownership needs a server timestamp, not CurrentTime.
window.change_property(atom('PT_TIMESTAMP'), Xatom.STRING, 8, b'')
while (notice := connection.next_event()).type != X.PropertyNotify:
    pass
stamp = notice.time
window.set_selection_owner(clipboard, stamp)
if connection.get_selection_owner(clipboard) != window:
    raise SystemExit('could not own CLIPBOARD')

served = {utf8: text, plain: text} if mode == 'partial' else {}
while True:
    request = connection.next_event()
    if request.type == X.SelectionClear:
        break
    if request.type != X.SelectionRequest:
        continue
    prop = request.property or request.target
    if request.target == targets:
        request.requestor.change_property(prop, Xatom.ATOM, 32, [targets, timestamp, plain_utf8, utf8, plain])
    elif request.target == timestamp:
        request.requestor.change_property(prop, Xatom.INTEGER, 32, [stamp])
    elif request.target in served:
        request.requestor.change_property(prop, request.target, 8, served[request.target])
    else:
        prop = X.NONE
    request.requestor.send_event(event.SelectionNotify(time=request.time, requestor=request.requestor,
        selection=request.selection, target=request.target, property=prop))
    connection.flush()
