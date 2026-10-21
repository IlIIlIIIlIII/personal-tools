import Gio from 'gi://Gio';
import GLib from 'gi://GLib';
import {allowedMimes, isExcluded, isTerminal, panelBounds, monitorAtPoint, refusedFormat, scopePayload, RevisionGate} from '../../../Sources/Linux/Shell/model.js';
function assert(condition,message) { if (!condition) throw new Error(message); }
assert(JSON.stringify(panelBounds({x:0,y:32,width:1488,height:1024}))===JSON.stringify({x:24,y:632,width:1440,height:400}),'panel reference geometry');
const tiny=panelBounds({x:0,y:0,width:500,height:320});
assert(tiny.height===272&&tiny.y===24,'small work area clamps panel');
assert(monitorAtPoint([{x:0,y:0,width:100,height:100},{x:100,y:0,width:200,height:100}],101,80)===1,'pointer monitor');
assert(allowedMimes(['text/plain','image/png','image/jpeg','bad']).join(',')==='text/plain,image/png','bounded MIME selection');
assert(refusedFormat(new GLib.Error(Gio.IOErrorEnum,Gio.IOErrorEnum.NOT_FOUND,'Format text/plain;charset=UTF-8 not supported'))&&!refusedFormat(new GLib.Error(Gio.IOErrorEnum,Gio.IOErrorEnum.FAILED,'pipe'))&&!refusedFormat(new Error('x')),'refused X11 format is skippable');
assert(isExcluded('com.1password.1Password.desktop')&&isExcluded('org.test.desktop',['org.test']),'sensitive/excluded foreground');
assert(isTerminal('org.gnome.Ptyxis.desktop')&&!isTerminal('org.gnome.TextEditor.desktop'),'terminal paste modifier');
assert(scopePayload('work').categoryId==='work'&&scopePayload('*').pinnedOnly,'search collection scope');
const gate=new RevisionGate();const old=gate.next();gate.next();assert(!gate.current(old),'late reply rejected');
print('PASS Shell geometry, MIME, refused formats, exclusions, terminal modifiers, search scope, stale reply gate');
