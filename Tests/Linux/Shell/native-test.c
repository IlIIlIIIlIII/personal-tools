#define _GNU_SOURCE
#include "pt-clipboard.h"
#include <fcntl.h>
#include <string.h>
#include <unistd.h>

static void ready(GObject *source, GAsyncResult *result, gpointer data) {
  GError *error = NULL;
  *(GInputStream **)data = meta_selection_source_read_finish(META_SELECTION_SOURCE(source), result, &error);
  g_assert_no_error(error);
}
static GInputStream *read_source(PtClipboardSource *source, const char *mime) {
  GInputStream *stream = NULL;
  meta_selection_source_read_async(META_SELECTION_SOURCE(source), mime, NULL, ready, &stream);
  while (!stream) g_main_context_iteration(NULL, TRUE);
  return stream;
}
static void independent_cursor(void) {
  GError *error = NULL;
  int fd = pt_clipboard_create_transfer_fd(&error);
  g_assert_no_error(error);
  g_assert_cmpint(write(fd, "hello world", 11), ==, 11);
  g_assert_true(pt_clipboard_seal_transfer_fd(fd, 11, &error));
  g_assert_no_error(error);
  g_autoptr(GUnixFDList) fds = g_unix_fd_list_new();
  int handle = g_unix_fd_list_append(fds, fd, &error);
  GVariantBuilder builder;
  g_variant_builder_init(&builder, G_VARIANT_TYPE("a(sh)"));
  g_variant_builder_add(&builder, "(sh)", "text/plain", handle);
  g_variant_builder_add(&builder, "(sh)", "text/html", handle);
  g_autoptr(GVariant) formats = g_variant_ref_sink(g_variant_builder_end(&builder));
  g_autoptr(PtClipboardSource) source = pt_clipboard_source_new(formats, fds, 22, &error);
  g_assert_no_error(error); g_assert_nonnull(source);
  close(fd); g_clear_object(&fds); // Source must own lifetime independently.
  g_autoptr(GInputStream) a = read_source(source, "text/plain");
  g_autoptr(GInputStream) b = read_source(source, "text/html");
  char first[6] = {0}, second[12] = {0}, rest[7] = {0};
  g_assert_cmpint(g_input_stream_read(a, first, 5, NULL, &error), ==, 5);
  g_assert_cmpint(g_input_stream_read(b, second, 11, NULL, &error), ==, 11);
  g_assert_cmpint(g_input_stream_read(a, rest, 6, NULL, &error), ==, 6);
  g_assert_no_error(error);
  g_assert_cmpstr(first, ==, "hello"); g_assert_cmpstr(second, ==, "hello world"); g_assert_cmpstr(rest, ==, " world");
}
static void rejects_mutation_and_limits(void) {
  GError *error = NULL;
  int fd = pt_clipboard_create_transfer_fd(&error);
  g_assert_cmpint(write(fd,"secret",6), ==, 6);
  g_autoptr(GUnixFDList) fds = g_unix_fd_list_new();
  int handle = g_unix_fd_list_append(fds,fd,&error);
  g_autoptr(GVariant) formats = g_variant_ref_sink(g_variant_new_parsed("[('text/plain', %h)]", handle));
  g_assert_null(pt_clipboard_source_new(formats,fds,100,&error));
  g_assert_error(error,G_IO_ERROR,G_IO_ERROR_INVALID_ARGUMENT); g_clear_error(&error);
  g_assert_false(pt_clipboard_seal_transfer_fd(fd,5,&error));
  g_assert_error(error,G_IO_ERROR,G_IO_ERROR_NO_SPACE); g_clear_error(&error);
  g_assert_true(pt_clipboard_seal_transfer_fd(fd,6,&error));
  g_assert_cmpint(write(fd,"x",1), ==, -1);
  g_assert_null(pt_clipboard_source_new(formats,fds,5,&error));
  g_assert_error(error,G_IO_ERROR,G_IO_ERROR_INVALID_ARGUMENT); g_clear_error(&error);
  close(fd);
}
int main(int argc,char **argv) {
  g_test_init(&argc,&argv,NULL);
  g_test_add_func("/clipboard/multi-mime-independent-cursor-lifetime",independent_cursor);
  g_test_add_func("/clipboard/mutable-fd-and-size-rejection",rejects_mutation_and_limits);
  return g_test_run();
}
