namespace BrAIn.Browser.Linux;

/// <summary>
/// Developer aids for the toolbar view. Every one is switched on by an environment variable; nothing runs otherwise.
///   BRAIN_DEBUG_BAR=1       an in-page probe (JS errors, what is under a few points of the toolbar document) and
///                           [bar-layout] logs of every relayout
///   BRAIN_DEBUG_BARSNAP=f   WebKit's own picture of the toolbar view, written to f (PNG) after 3.5 s. Compare it with a
///                           screenshot of the screen: it tells "WebKit rendered nothing" from "the frame never reached
///                           the screen" (that is how the blank-toolbar bug was pinned down)
///   BRAIN_DEBUG_BARTREE=1   the GdkWindow tree (geometry, visible region, owning widget) after 3.5 s
/// </summary>
internal static unsafe partial class LinuxBar
{
    /// <summary>The in-page probe: reports script errors at once and, while the toolbar view is taller than its strip, what
    /// is under a point of its document — so a surprise opaque area can be traced to an element.</summary>
    private const string DebugProbe = @"(function(){var last=0;
 function say(m){try{window.external.sendMessage('brain:dbg:'+m)}catch(_){}}
 window.addEventListener('error',function(e){say('JSERR '+e.message+' @'+(e.filename||'').slice(-40)+':'+e.lineno)});
 window.addEventListener('unhandledrejection',function(e){say('REJECT '+(e.reason&&e.reason.message||e.reason))});
 document.addEventListener('keydown',function(e){say('KEY '+(e.ctrlKey?'ctrl+':'')+e.key+' target='+(e.target&&e.target.tagName)+' focus='+(document.hasFocus()?'doc':'NOFOCUS'))},true);
 function d(x,y){var out=[],seen=new Set();(function walk(root){if(seen.has(root))return;seen.add(root);
   var els=root.elementsFromPoint(x,y);for(var i=0;i<els.length;i++){var e=els[i],cs=getComputedStyle(e);
   out.push((e.tagName||'')+(e.id?'#'+e.id:'')+(e.className&&typeof e.className==='string'?'.'+e.className.split(' ')[0]:'')+' bg='+cs.backgroundColor+' pos='+cs.position+' op='+cs.opacity);
   if(e.shadowRoot)walk(e.shadowRoot)}})(document);return out.join(' | ')}
 setInterval(function(){
  if(innerHeight<=60||Date.now()-last<1500)return;last=Date.now();
  var hs=document.getElementById('__brain-chrome-host'),pr=hs&&hs.shadowRoot&&hs.shadowRoot.getElementById('pal'),prr=pr?pr.getBoundingClientRect():null,bd=document.getElementById('__brain_bd'),bdr=bd?bd.getBoundingClientRect():null;
  say('pal='+(prr?[prr.left,prr.top,prr.width,prr.height].join(','):'-')+' bd='+(bdr?[bdr.left,bdr.top,bdr.width,bdr.height].join(','):'-')+' inner='+innerWidth+'x'+innerHeight+' html.bg='+getComputedStyle(document.documentElement).backgroundColor+' @(700,300): '+d(700,300));
 },500)})();";

    private static void AttachDebug(IntPtr win, IntPtr view)
    {
        if (DebugBar)
        {
            // which view holds the keyboard focus, reported whenever it changes
            var last = "";
            GLib.Every(100, () =>
            {
                var f = Gtk.gtk_window_get_focus(win);
                var name = f == IntPtr.Zero ? "none" : f == _bar ? "BAR" : f == _side ? "SIDE" : f == LinuxTabs.Active ? "PAGE" : f == LinuxWindow.Boot ? "BOOT" : "other";
                name += Gtk.gtk_window_is_active(win) != 0 ? "" : " (window inactive)";
                if (name != last) { last = name; Console.Error.WriteLine($"[bar-focus] t={Environment.TickCount64 % 100000} {name}"); }
                return true;
            });
            // every key press the toplevel receives (before it is handed to the focused widget)
            GLib.Connect(win, "key-press-event", (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int>)&OnDebugKey);
        }

        if (Environment.GetEnvironmentVariable("BRAIN_DEBUG_BARTREE") == "1")
            GLib.Every(3500, () => { DumpWindowTree(Gtk.gtk_widget_get_window(win), 0); return false; });

        var snapPath = Environment.GetEnvironmentVariable("BRAIN_DEBUG_BARSNAP");
        if (!string.IsNullOrEmpty(snapPath))
            GLib.Every(3500, () =>
            {
                _ = Task.Run(() =>
                {
                    var img = LinuxShot.BeginSnapshot(view).GetAwaiter().GetResult();
                    var png = Cairo.ToPng(img);
                    if (png is not null) File.WriteAllBytes(snapPath, png);
                    Console.Error.WriteLine($"[bar-snap] wrote {png?.Length ?? 0} bytes to {snapPath}");
                    LinuxShot.ReleaseImage(img);
                });
                return false;
            });
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static int OnDebugKey(IntPtr window, IntPtr ev, IntPtr data)
    {
        try
        {
            var keyval = System.Runtime.InteropServices.Marshal.ReadInt32(ev, 28);
            var f = Gtk.gtk_window_get_focus(window);
            var who = f == IntPtr.Zero ? "none" : f == _bar ? "BAR" : f == LinuxTabs.Active ? "PAGE" : "other";
            Console.Error.WriteLine($"[bar-key] t={Environment.TickCount64 % 100000} keyval=0x{keyval:x} focus={who}");
        }
        catch { }
        return 0;
    }

    private static void DumpWindowTree(IntPtr gw, int depth)
    {
        if (gw == IntPtr.Zero) return;
        Gtk.gdk_window_get_position(gw, out var x, out var y);
        Gtk.gdk_window_get_user_data(gw, out var owner);
        var typeName = owner == IntPtr.Zero ? "-" : GLib.Str(Gtk.g_type_name_from_instance(owner)) ?? "?";
        var vis = Gtk.RegionExtents(Gtk.gdk_window_get_visible_region(gw));
        Console.Error.WriteLine($"[bar-tree] {new string(' ', depth * 2)}{gw:x} {typeName} @({x},{y}) {Gtk.gdk_window_get_width(gw)}x{Gtk.gdk_window_get_height(gw)} visible={Gtk.gdk_window_is_visible(gw)} viewable={Gtk.gdk_window_is_viewable(gw)} visRegion={vis}");
        foreach (var ch in System.Linq.Enumerable.Reverse(GLib.ListItems(Gtk.gdk_window_get_children(gw)))) DumpWindowTree(ch, depth + 1); // bottom → top
    }
}
