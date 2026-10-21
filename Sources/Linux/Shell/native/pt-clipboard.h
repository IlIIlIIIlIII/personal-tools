#pragma once

#include <gio/gio.h>
#include <gio/gunixfdlist.h>
#include <meta/meta-selection-source.h>

G_BEGIN_DECLS

#define PT_CLIPBOARD_TYPE_SOURCE (pt_clipboard_source_get_type ())
G_DECLARE_FINAL_TYPE (PtClipboardSource, pt_clipboard_source, PT_CLIPBOARD, SOURCE, MetaSelectionSource)

/**
 * pt_clipboard_source_new:
 * @formats: an a(sh) variant of MIME type and Unix FD-list index
 * @fds: descriptors containing the original clipboard payloads
 * @max_bytes: maximum aggregate payload size
 * @error: return location for an error
 *
 * Retains duplicate descriptors, not paths. Each read has its own pread cursor.
 * Descriptors must be readable regular files, opened read-only or fully sealed.
 *
 * Returns: (transfer full): a validated multi-format selection source
 */
PtClipboardSource *pt_clipboard_source_new (GVariant *formats, GUnixFDList *fds,
                                           guint64 max_bytes, GError **error);

/**
 * pt_clipboard_create_transfer_fd:
 * @error: return location for an error
 * Returns: a close-on-exec, sealable memory file descriptor, or -1
 */
gint pt_clipboard_create_transfer_fd (GError **error);

/**
 * pt_clipboard_seal_transfer_fd:
 * @fd: transfer descriptor
 * @max_bytes: maximum accepted size, not including the overflow sentinel
 * @error: return location for an error
 * Returns: whether the file is within bounds and was made immutable
 */
gboolean pt_clipboard_seal_transfer_fd (gint fd, guint64 max_bytes, GError **error);

/**
 * pt_clipboard_fd_size:
 * @fd: regular file descriptor
 * @error: return location for an error
 * Returns: the descriptor size, or -1
 */
gint64 pt_clipboard_fd_size (gint fd, GError **error);

/**
 * pt_clipboard_close_fd:
 * @fd: owned descriptor to close
 */
void pt_clipboard_close_fd (gint fd);

G_END_DECLS
