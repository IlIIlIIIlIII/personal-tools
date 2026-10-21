project('personal-tools-shell', 'c', version: '0.1.0',
  default_options: ['c_std=c11', 'warning_level=3'])
subdir('native')
native_test = executable('personal-tools-clipboard-test', '../../../Tests/Linux/Shell/native-test.c',
  include_directories: include_directories('native'),
  dependencies: pt_clipboard_deps, link_with: pt_clipboard_library)
test('clipboard-fd-source', native_test)
