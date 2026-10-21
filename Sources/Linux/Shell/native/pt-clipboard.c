#define _GNU_SOURCE
#include "pt-clipboard.h"

#include <errno.h>
#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

/* Never use dup()+seek for each read: duplicated descriptors share an offset. */
typedef struct {
  GInputStream parent_instance;
  int fd;
  off_t offset;
} PtClipboardInput;
typedef GInputStreamClass PtClipboardInputClass;
G_DEFINE_TYPE (PtClipboardInput, pt_clipboard_input, G_TYPE_INPUT_STREAM)

static gssize
input_read (GInputStream *stream, void *buffer, gsize count,
            GCancellable *cancellable, GError **error)
{
  PtClipboardInput *self = (PtClipboardInput *) stream;
  ssize_t result;
  if (g_cancellable_set_error_if_cancelled (cancellable, error)) return -1;
  do { result = pread (self->fd, buffer, MIN (count, G_MAXSSIZE), self->offset); }
  while (result < 0 && errno == EINTR);
  if (result < 0) {
    g_set_error (error, G_IO_ERROR, g_io_error_from_errno (errno),
                 "Cannot read clipboard descriptor: %s", g_strerror (errno));
    return -1;
  }
  self->offset += result;
  return result;
}

static gboolean
input_close (GInputStream *stream, GCancellable *cancellable, GError **error)
{
  PtClipboardInput *self = (PtClipboardInput *) stream;
  (void) cancellable;
  (void) error;
  if (self->fd >= 0) { close (self->fd); self->fd = -1; }
  return TRUE;
}

static void
input_finalize (GObject *object)
{
  input_close (G_INPUT_STREAM (object), NULL, NULL);
  G_OBJECT_CLASS (pt_clipboard_input_parent_class)->finalize (object);
}

static void
pt_clipboard_input_class_init (PtClipboardInputClass *klass)
{
  GInputStreamClass *stream_class = G_INPUT_STREAM_CLASS (klass);
  G_OBJECT_CLASS (klass)->finalize = input_finalize;
  stream_class->read_fn = input_read;
  stream_class->close_fn = input_close;
}

static void
pt_clipboard_input_init (PtClipboardInput *self)
{
  self->fd = -1;
}

struct _PtClipboardSource {
  MetaSelectionSource parent_instance;
  GHashTable *formats;
};
G_DEFINE_TYPE (PtClipboardSource, pt_clipboard_source, META_TYPE_SELECTION_SOURCE)

static void
descriptor_free (gpointer data)
{
  int *fd = data;
  close (*fd);
  g_free (fd);
}

static GList *
source_mimetypes (MetaSelectionSource *source)
{
  PtClipboardSource *self = PT_CLIPBOARD_SOURCE (source);
  GHashTableIter iter;
  gpointer key;
  GList *list = NULL;
  g_hash_table_iter_init (&iter, self->formats);
  while (g_hash_table_iter_next (&iter, &key, NULL)) list = g_list_prepend (list, g_strdup (key));
  return g_list_reverse (list);
}

static void
source_read_async (MetaSelectionSource *source, const char *mimetype,
                   GCancellable *cancellable, GAsyncReadyCallback callback,
                   gpointer user_data)
{
  PtClipboardSource *self = PT_CLIPBOARD_SOURCE (source);
  g_autoptr (GTask) task = g_task_new (source, cancellable, callback, user_data);
  int *fd = g_hash_table_lookup (self->formats, mimetype);
  g_task_set_source_tag (task, source_read_async);
  if (g_task_return_error_if_cancelled (task)) return;
  if (!fd) {
    g_task_return_new_error (task, G_IO_ERROR, G_IO_ERROR_NOT_SUPPORTED, "Clipboard format not offered");
    return;
  }
  int duplicate = fcntl (*fd, F_DUPFD_CLOEXEC, 3);
  if (duplicate < 0) {
    g_task_return_new_error (task, G_IO_ERROR, g_io_error_from_errno (errno), "Cannot retain clipboard descriptor");
    return;
  }
  PtClipboardInput *stream = g_object_new (pt_clipboard_input_get_type (), NULL);
  stream->fd = duplicate;
  g_task_return_pointer (task, stream, g_object_unref);
}

static GInputStream *
source_read_finish (MetaSelectionSource *source, GAsyncResult *result, GError **error)
{
  g_return_val_if_fail (g_task_is_valid (result, source), NULL);
  g_return_val_if_fail (g_task_get_source_tag (G_TASK (result)) == source_read_async, NULL);
  return g_task_propagate_pointer (G_TASK (result), error);
}

static void
source_finalize (GObject *object)
{
  g_hash_table_unref (PT_CLIPBOARD_SOURCE (object)->formats);
  G_OBJECT_CLASS (pt_clipboard_source_parent_class)->finalize (object);
}

static void
pt_clipboard_source_class_init (PtClipboardSourceClass *klass)
{
  MetaSelectionSourceClass *selection_class = META_SELECTION_SOURCE_CLASS (klass);
  G_OBJECT_CLASS (klass)->finalize = source_finalize;
  selection_class->get_mimetypes = source_mimetypes;
  selection_class->read_async = source_read_async;
  selection_class->read_finish = source_read_finish;
}

static void
pt_clipboard_source_init (PtClipboardSource *self)
{
  self->formats = g_hash_table_new_full (g_str_hash, g_str_equal, g_free, descriptor_free);
}

static gboolean
mime_allowed (const char *mime)
{
  const char *allowed[] = {"text/plain", "text/plain;charset=utf-8", "text/plain;charset=UTF-8",
                          "UTF8_STRING", "text/html", "image/png", "image/jpeg",
                          "text/uri-list", "x-special/gnome-copied-files", NULL};
  for (int i = 0; allowed[i]; i++) if (g_str_equal (mime, allowed[i])) return TRUE;
  return FALSE;
}

gint64
pt_clipboard_fd_size (gint fd, GError **error)
{
  struct stat info;
  if (fstat (fd, &info) < 0) {
    g_set_error (error, G_IO_ERROR, g_io_error_from_errno (errno), "Cannot inspect clipboard descriptor");
    return -1;
  }
  if (!S_ISREG (info.st_mode) || info.st_size < 0) {
    g_set_error_literal (error, G_IO_ERROR, G_IO_ERROR_INVALID_ARGUMENT, "Clipboard descriptor is not a regular file");
    return -1;
  }
  return info.st_size;
}

PtClipboardSource *
pt_clipboard_source_new (GVariant *formats, GUnixFDList *fds, guint64 max_bytes, GError **error)
{
  if (!formats || !g_variant_is_of_type (formats, G_VARIANT_TYPE ("a(sh)")) ||
      !fds || !max_bytes || max_bytes > 1024ULL * 1024 * 1024 ||
      g_variant_n_children (formats) == 0 || g_variant_n_children (formats) > 9) {
    g_set_error_literal (error, G_IO_ERROR, G_IO_ERROR_INVALID_ARGUMENT, "Invalid clipboard descriptor manifest");
    return NULL;
  }
  g_autoptr (PtClipboardSource) self = g_object_new (PT_CLIPBOARD_TYPE_SOURCE, NULL);
  GVariantIter iter;
  const char *mime;
  int index;
  guint64 total = 0;
  g_variant_iter_init (&iter, formats);
  while (g_variant_iter_next (&iter, "(&sh)", &mime, &index)) {
    if (!mime_allowed (mime) || g_hash_table_contains (self->formats, mime) || index < 0 || index >= g_unix_fd_list_get_length (fds)) {
      g_set_error_literal (error, G_IO_ERROR, G_IO_ERROR_INVALID_ARGUMENT, "Unsupported, duplicate, or invalid clipboard format");
      return NULL;
    }
    int fd = g_unix_fd_list_get (fds, index, error);
    if (fd < 0) return NULL;
    gint64 size = pt_clipboard_fd_size (fd, error);
    int flags = fcntl (fd, F_GETFL);
    int seals = fcntl (fd, F_GET_SEALS);
    int required = F_SEAL_WRITE | F_SEAL_GROW | F_SEAL_SHRINK | F_SEAL_SEAL;
    gboolean immutable = (flags >= 0 && (flags & O_ACCMODE) == O_RDONLY) || (seals >= 0 && (seals & required) == required);
    if (size < 0) { close (fd); return NULL; }
    if (!immutable || (guint64) size > max_bytes - total) {
      close (fd);
      g_set_error_literal (error, G_IO_ERROR, G_IO_ERROR_INVALID_ARGUMENT, "Clipboard descriptor is mutable or exceeds the size limit");
      return NULL;
    }
    total += size;
    int *retained = g_new (int, 1);
    *retained = fd;
    g_hash_table_insert (self->formats, g_strdup (mime), retained);
  }
  return g_steal_pointer (&self);
}

gint
pt_clipboard_create_transfer_fd (GError **error)
{
  int fd = memfd_create ("personal-tools-clipboard", MFD_CLOEXEC | MFD_ALLOW_SEALING);
  if (fd < 0) g_set_error (error, G_IO_ERROR, g_io_error_from_errno (errno), "Cannot create clipboard transfer");
  return fd;
}

gboolean
pt_clipboard_seal_transfer_fd (gint fd, guint64 max_bytes, GError **error)
{
  gint64 size = pt_clipboard_fd_size (fd, error);
  if (size < 0) return FALSE;
  if ((guint64) size > max_bytes) {
    g_set_error_literal (error, G_IO_ERROR, G_IO_ERROR_NO_SPACE, "Clipboard payload exceeds the size limit");
    return FALSE;
  }
  if (fcntl (fd, F_ADD_SEALS, F_SEAL_WRITE | F_SEAL_GROW | F_SEAL_SHRINK | F_SEAL_SEAL) < 0) {
    g_set_error (error, G_IO_ERROR, g_io_error_from_errno (errno), "Cannot seal clipboard transfer");
    return FALSE;
  }
  return TRUE;
}

void
pt_clipboard_close_fd (gint fd)
{
  if (fd >= 0) close (fd);
}
