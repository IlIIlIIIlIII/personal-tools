// Reuse the full card keyboard suite with a terminal-style Wayland target.
import GLib from 'gi://GLib';
GLib.setenv('PERSONALTOOLS_TEST_TERMINAL', '1', true);
export {init, run, METRICS} from './clipboard-browse.js';
