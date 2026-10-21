pt_clipboard_deps = [dependency('gio-unix-2.0'), dependency('libmutter-18')]
pt_clipboard_library = shared_library('pt-clipboard', 'pt-clipboard.c',
  dependencies: pt_clipboard_deps, install: false)
gnome = import('gnome')
pt_clipboard_gir = gnome.generate_gir(pt_clipboard_library,
  sources: ['pt-clipboard.h', 'pt-clipboard.c'],
  namespace: 'PtClipboard', nsversion: '1.0',
  identifier_prefix: 'PtClipboard', symbol_prefix: 'pt_clipboard',
  includes: ['Gio-2.0', 'Meta-18'],
  dependencies: pt_clipboard_deps, install: false,
  extra_args: ['--warn-all', '--c-include=pt-clipboard.h'])
