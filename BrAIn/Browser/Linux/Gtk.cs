using System.Runtime.InteropServices;

namespace BrAIn.Browser.Linux;

/// <summary>GTK 3 + GDK 3 bindings (Linux only; GTK main thread only).</summary>
internal static unsafe partial class Gtk
{
    private const string LibGtk = "libgtk-3.so.0";
    private const string LibGdk = "libgdk-3.so.0";

    // GtkAlign
    internal const int AlignFill = 0, AlignStart = 1, AlignEnd = 2, AlignCenter = 3;
    // GtkOrientation
    internal const int Horizontal = 0, Vertical = 1;
    // GdkWindowState
    internal const int StateIconified = 2, StateMaximized = 4, StateFullscreen = 16, StateFocused = 128;
    // GdkWindowEdge
    internal const int EdgeNorthWest = 0, EdgeNorth = 1, EdgeNorthEast = 2, EdgeWest = 3, EdgeEast = 4,
        EdgeSouthWest = 5, EdgeSouth = 6, EdgeSouthEast = 7;
    // GdkEventType
    internal const int EvMotion = 3, EvButtonPress = 4, EvButtonRelease = 7, EvEnter = 10, EvLeave = 11, EvScroll = 31;
    // GtkStackTransitionType
    internal const int StackNone = 0, StackCrossfade = 1;
    // GtkResponseType
    internal const int ResponseOk = -5, ResponseCancel = -6, ResponseAccept = -3, ResponseReject = -2;

    // ---- containers / layout ----
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_window_list_toplevels();
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_bin_get_child(IntPtr bin);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_container_get_children(IntPtr container);
    [LibraryImport(LibGtk)] internal static partial void gtk_container_add(IntPtr container, IntPtr child);
    [LibraryImport(LibGtk)] internal static partial void gtk_container_remove(IntPtr container, IntPtr child);
    [LibraryImport(LibGtk)] internal static partial void gtk_container_set_border_width(IntPtr container, uint w);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_fixed_new();
    [LibraryImport(LibGtk)] internal static partial void gtk_fixed_put(IntPtr container, IntPtr widget, int x, int y);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_event_box_new();
    [LibraryImport(LibGtk)] internal static partial void gtk_event_box_set_above_child(IntPtr box, int above);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_overlay_new();
    [LibraryImport(LibGtk)] internal static partial void gtk_overlay_add_overlay(IntPtr overlay, IntPtr widget);
    [LibraryImport(LibGtk)] internal static partial void gtk_overlay_set_overlay_pass_through(IntPtr overlay, IntPtr widget, int passThrough);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_box_new(int orientation, int spacing);
    [LibraryImport(LibGtk)] internal static partial void gtk_box_pack_start(IntPtr box, IntPtr child, int expand, int fill, uint padding);
    [LibraryImport(LibGtk)] internal static partial void gtk_box_set_spacing(IntPtr box, int spacing);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_stack_new();
    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_stack_add_named(IntPtr stack, IntPtr child, string name);
    [LibraryImport(LibGtk)] internal static partial void gtk_stack_set_visible_child(IntPtr stack, IntPtr child);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_stack_get_visible_child(IntPtr stack);
    [LibraryImport(LibGtk)] internal static partial void gtk_stack_set_transition_type(IntPtr stack, int type);
    [LibraryImport(LibGtk)] internal static partial void gtk_stack_set_transition_duration(IntPtr stack, uint ms);
    [LibraryImport(LibGtk)] internal static partial void gtk_stack_set_homogeneous(IntPtr stack, int homogeneous);

    // ---- widgets ----
    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_widget_set_name(IntPtr w, string name);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_show(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_hide(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_show_all(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_destroy(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_widget_get_parent(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_widget_get_toplevel(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_widget_get_window(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial int gtk_widget_get_visible(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial int gtk_widget_get_mapped(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial int gtk_widget_is_ancestor(IntPtr w, IntPtr ancestor);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_set_size_request(IntPtr w, int width, int height);
    [LibraryImport(LibGtk)] internal static partial int gtk_widget_get_allocated_width(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial int gtk_widget_get_allocated_height(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_set_halign(IntPtr w, int align);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_set_valign(IntPtr w, int align);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_set_hexpand(IntPtr w, int expand);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_set_vexpand(IntPtr w, int expand);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_set_margin_top(IntPtr w, int m);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_set_margin_bottom(IntPtr w, int m);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_set_margin_start(IntPtr w, int m);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_set_margin_end(IntPtr w, int m);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_grab_focus(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial int gtk_widget_has_focus(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_set_can_focus(IntPtr w, int can);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_queue_resize(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_queue_draw(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial int gtk_widget_translate_coordinates(IntPtr src, IntPtr dest, int sx, int sy, out int dx, out int dy);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_widget_get_style_context(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_set_app_paintable(IntPtr w, int paintable);
    [LibraryImport(LibGtk)] internal static partial int gtk_widget_event(IntPtr w, IntPtr ev);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_widget_get_screen(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_widget_get_display(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_widget_add_events(IntPtr w, int events);
    [LibraryImport(LibGtk)] internal static partial int gtk_widget_get_scale_factor(IntPtr w);

    // ---- windows ----
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_window_new(int type);
    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_window_set_title(IntPtr w, string title);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_window_get_title(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_set_default_size(IntPtr w, int width, int height);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_set_geometry_hints(IntPtr window, IntPtr geometryWidget, IntPtr geometry, int mask);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_resize(IntPtr w, int width, int height);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_get_size(IntPtr w, out int width, out int height);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_move(IntPtr w, int x, int y);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_set_position(IntPtr w, int pos);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_fullscreen(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_unfullscreen(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_maximize(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_unmaximize(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_iconify(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_deiconify(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_present(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_close(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_set_modal(IntPtr w, int modal);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_set_transient_for(IntPtr w, IntPtr parent);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_set_keep_above(IntPtr w, int above);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_window_get_focus(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial int gtk_window_is_active(IntPtr w);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_set_focus(IntPtr w, IntPtr widget);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_set_decorated(IntPtr w, int decorated);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_set_skip_taskbar_hint(IntPtr w, int skip);
    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)] internal static partial int gtk_window_set_icon_from_file(IntPtr w, string file, out IntPtr error);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_begin_move_drag(IntPtr w, int button, int rootX, int rootY, uint timestamp);
    [LibraryImport(LibGtk)] internal static partial void gtk_window_begin_resize_drag(IntPtr w, int edge, int button, int rootX, int rootY, uint timestamp);
    [LibraryImport(LibGtk)] internal static partial uint gtk_get_current_event_time();
    [LibraryImport(LibGtk)] internal static partial void gtk_main_do_event(IntPtr ev);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_settings_get_default();

    // ---- CSS ----
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_css_provider_new();
    [LibraryImport(LibGtk)] internal static partial int gtk_css_provider_load_from_data(IntPtr provider, IntPtr data, nint length, out IntPtr error);
    [LibraryImport(LibGtk)] internal static partial void gtk_style_context_add_provider(IntPtr ctx, IntPtr provider, uint priority);
    [LibraryImport(LibGtk)] internal static partial void gtk_style_context_add_provider_for_screen(IntPtr screen, IntPtr provider, uint priority);

    // ---- dialogs ----
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_dialog_new();
    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)] internal static partial IntPtr gtk_dialog_add_button(IntPtr dlg, string text, int response);
    [LibraryImport(LibGtk)] internal static partial int gtk_dialog_run(IntPtr dlg);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_dialog_get_content_area(IntPtr dlg);
    [LibraryImport(LibGtk)] internal static partial void gtk_dialog_set_default_response(IntPtr dlg, int response);
    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)] internal static partial IntPtr gtk_label_new(string text);
    [LibraryImport(LibGtk)] internal static partial void gtk_label_set_line_wrap(IntPtr label, int wrap);
    [LibraryImport(LibGtk)] internal static partial void gtk_label_set_xalign(IntPtr label, float x);
    [LibraryImport(LibGtk)] internal static partial void gtk_label_set_max_width_chars(IntPtr label, int n);
    [LibraryImport(LibGtk)] internal static partial void gtk_label_set_selectable(IntPtr label, int sel);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_entry_new();
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_entry_get_text(IntPtr entry);
    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_entry_set_text(IntPtr entry, string text);
    [LibraryImport(LibGtk)] internal static partial void gtk_entry_set_visibility(IntPtr entry, int visible);
    [LibraryImport(LibGtk)] internal static partial void gtk_entry_set_activates_default(IntPtr entry, int activates);
    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)] internal static partial int gtk_show_uri_on_window(IntPtr parent, string uri, uint timestamp, out IntPtr error);

    // ---- clipboard ----
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_clipboard_get(IntPtr selection);
    [LibraryImport(LibGtk)] internal static partial IntPtr gtk_clipboard_wait_for_text(IntPtr clipboard);
    [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)] internal static partial void gtk_clipboard_set_text(IntPtr clipboard, string text, int len);
    [LibraryImport(LibGtk)] internal static partial void gtk_clipboard_set_image(IntPtr clipboard, IntPtr pixbuf);
    [LibraryImport(LibGtk)] internal static partial void gtk_clipboard_store(IntPtr clipboard);
    [LibraryImport(LibGtk)] internal static partial void gtk_clipboard_clear(IntPtr clipboard);

    // ---- GDK ----
    [LibraryImport(LibGdk, StringMarshalling = StringMarshalling.Utf8)] internal static partial IntPtr gdk_atom_intern(string name, int onlyIfExists);
    [LibraryImport(LibGdk)] internal static partial IntPtr gdk_display_get_default();
    [LibraryImport(LibGdk)] internal static partial IntPtr gdk_display_get_monitor_at_window(IntPtr display, IntPtr window);
    [LibraryImport(LibGdk)] internal static partial IntPtr gdk_display_get_primary_monitor(IntPtr display);
    [LibraryImport(LibGdk)] internal static partial void gdk_monitor_get_workarea(IntPtr monitor, int* rect); // x, y, width, height
    [LibraryImport(LibGdk)] internal static partial IntPtr gdk_display_get_default_seat(IntPtr display);
    [LibraryImport(LibGdk)] internal static partial IntPtr gdk_seat_get_pointer(IntPtr seat);
    [LibraryImport(LibGdk)] internal static partial IntPtr gdk_seat_get_keyboard(IntPtr seat);
    [LibraryImport(LibGdk)] internal static partial void gdk_device_get_position(IntPtr device, out IntPtr screen, out int x, out int y);
    [LibraryImport(LibGdk)] internal static partial void gdk_device_get_state(IntPtr device, IntPtr window, IntPtr axes, out int mask);
    [LibraryImport(LibGdk)] internal static partial IntPtr gdk_event_new(int type);
    [LibraryImport(LibGdk)] internal static partial void gdk_event_free(IntPtr ev);
    [LibraryImport(LibGdk)] internal static partial void gdk_event_put(IntPtr ev);
    [LibraryImport(LibGdk)] internal static partial void gdk_event_set_device(IntPtr ev, IntPtr device);
    [LibraryImport(LibGdk)] internal static partial void gdk_event_set_source_device(IntPtr ev, IntPtr device);
    [LibraryImport(LibGdk)] internal static partial int gdk_window_get_state(IntPtr w);
    [LibraryImport(LibGdk)] internal static partial void gdk_window_get_origin(IntPtr w, out int x, out int y);
    [LibraryImport(LibGdk)] internal static partial int gdk_window_get_width(IntPtr w);
    [LibraryImport(LibGdk)] internal static partial int gdk_window_get_height(IntPtr w);
    [LibraryImport(LibGdk)] internal static partial int gdk_window_get_scale_factor(IntPtr w);
    [LibraryImport(LibGdk)] internal static partial IntPtr gdk_pixbuf_get_from_window(IntPtr w, int x, int y, int width, int height);
    [LibraryImport(LibGdk)] internal static partial IntPtr gdk_pixbuf_get_from_surface(IntPtr surface, int srcX, int srcY, int width, int height);
    [LibraryImport(LibGdk)] internal static partial void gdk_window_invalidate_rect(IntPtr w, IntPtr rect, int invalidateChildren);
    [LibraryImport(LibGdk)] internal static partial IntPtr gdk_window_get_children(IntPtr w);
    [LibraryImport(LibGdk)] internal static partial IntPtr gdk_window_get_visible_region(IntPtr w);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_region_get_extents(IntPtr region, int* rect);
    [LibraryImport("libcairo.so.2")] internal static partial void cairo_region_destroy(IntPtr region);
    [LibraryImport(LibGdk)] internal static partial IntPtr gdk_window_get_parent(IntPtr w);
    [LibraryImport(LibGdk)] internal static partial void gdk_window_get_position(IntPtr w, out int x, out int y);
    [LibraryImport(LibGdk)] internal static partial int gdk_window_is_visible(IntPtr w);
    [LibraryImport(LibGdk)] internal static partial int gdk_window_is_viewable(IntPtr w);
    [LibraryImport(LibGdk)] internal static partial void gdk_window_get_user_data(IntPtr w, out IntPtr data);
    [LibraryImport("libgobject-2.0.so.0")] internal static partial IntPtr g_type_name_from_instance(IntPtr instance);

    internal static string RegionExtents(IntPtr region)
    {
        if (region == IntPtr.Zero) return "-";
        var r = stackalloc int[4];
        cairo_region_get_extents(region, r);
        cairo_region_destroy(region);
        return $"{r[0]},{r[1]} {r[2]}x{r[3]}";
    }

    // ---- helpers ----

    [LibraryImport(LibGtk)] internal static partial nuint gtk_window_get_type();

    internal static IntPtr[] Toplevels()
    {
        var list = gtk_window_list_toplevels();
        var items = GLib.ListItems(list);
        // gtk_window_list_toplevels returns a copy of the list the caller must free (not the windows)
        GLib.g_list_free(list);
        return items.ToArray();
    }

    internal static IntPtr[] Children(IntPtr container)
    {
        var list = gtk_container_get_children(container);
        var items = GLib.ListItems(list);
        GLib.g_list_free(list);
        return items.ToArray();
    }

    internal static IntPtr Clipboard() => gtk_clipboard_get(gdk_atom_intern("CLIPBOARD", 0));

    private static readonly HashSet<string> _css = new();

    /// <summary>Applies CSS for the whole app (once per distinct text). Priority 800 = APPLICATION.</summary>
    internal static void AddAppCss(IntPtr widget, string css)
    {
        if (!_css.Add(css)) return;
        var provider = gtk_css_provider_new();
        var bytes = System.Text.Encoding.UTF8.GetBytes(css);
        fixed (byte* p = bytes)
            gtk_css_provider_load_from_data(provider, (IntPtr)p, bytes.Length, out var err);
        gtk_style_context_add_provider_for_screen(gtk_widget_get_screen(widget), provider, 800);
    }
}
