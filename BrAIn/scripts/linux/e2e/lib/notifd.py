#!/usr/bin/env python3
"""Mock org.freedesktop.Notifications service for the notification test.

Logs every call to /tmp/notif.log. Creating /tmp/notif.click or /tmp/notif.dismiss simulates the user clicking or
dismissing the most recent notification (the test does that with a plain file write).
"""
import os

import gi

gi.require_version('Gio', '2.0')
from gi.repository import Gio, GLib

XML = """<node><interface name='org.freedesktop.Notifications'>
 <method name='GetCapabilities'><arg type='as' direction='out'/></method>
 <method name='Notify'><arg type='s' direction='in'/><arg type='u' direction='in'/><arg type='s' direction='in'/><arg type='s' direction='in'/><arg type='s' direction='in'/><arg type='as' direction='in'/><arg type='a{sv}' direction='in'/><arg type='i' direction='in'/><arg type='u' direction='out'/></method>
 <method name='CloseNotification'><arg type='u' direction='in'/></method>
 <method name='GetServerInformation'><arg type='s' direction='out'/><arg type='s' direction='out'/><arg type='s' direction='out'/><arg type='s' direction='out'/></method>
 <signal name='NotificationClosed'><arg type='u'/><arg type='u'/></signal>
 <signal name='ActionInvoked'><arg type='u'/><arg type='s'/></signal>
</interface></node>"""
PATH, IFACE = '/org/freedesktop/Notifications', 'org.freedesktop.Notifications'
last = {'id': 0}
conn_ref = {}


def log(s):
    with open('/tmp/notif.log', 'a') as f:
        f.write(s + '\n')


def on_call(conn, sender, path, iface, method, params, inv):
    if method == 'GetCapabilities':
        inv.return_value(GLib.Variant('(as)', (['body', 'actions'],)))
    elif method == 'GetServerInformation':
        inv.return_value(GLib.Variant('(ssss)', ('mock', 'brain-e2e', '1', '1.2')))
    elif method == 'Notify':
        app, _rid, icon, summary, body, actions, _hints, _timeout = params.unpack()
        last['id'] += 1
        log(f'NOTIFY id={last["id"]} app={app} summary={summary!r} body={body!r} actions={actions} icon={os.path.basename(icon)}')
        inv.return_value(GLib.Variant('(u)', (last['id'],)))
    elif method == 'CloseNotification':
        nid = params.unpack()[0]
        log(f'CLOSE id={nid}')
        conn.emit_signal(None, PATH, IFACE, 'NotificationClosed', GLib.Variant('(uu)', (nid, 3)))
        inv.return_value(None)


def tick():
    c = conn_ref.get('c')
    if c and os.path.exists('/tmp/notif.click'):
        os.remove('/tmp/notif.click')
        nid = last['id']
        log(f'USER-CLICK id={nid}')
        c.emit_signal(None, PATH, IFACE, 'ActionInvoked', GLib.Variant('(us)', (nid, 'default')))
        c.emit_signal(None, PATH, IFACE, 'NotificationClosed', GLib.Variant('(uu)', (nid, 2)))
    if c and os.path.exists('/tmp/notif.dismiss'):
        os.remove('/tmp/notif.dismiss')
        nid = last['id']
        log(f'USER-DISMISS id={nid}')
        c.emit_signal(None, PATH, IFACE, 'NotificationClosed', GLib.Variant('(uu)', (nid, 2)))
    return True


def acquired(conn, _name):
    conn_ref['c'] = conn
    conn.register_object(PATH, Gio.DBusNodeInfo.new_for_xml(XML).interfaces[0], on_call, None, None)
    log('MOCK READY')


Gio.bus_own_name(Gio.BusType.SESSION, IFACE, Gio.BusNameOwnerFlags.NONE, acquired, None, None)
GLib.timeout_add(100, tick)
GLib.MainLoop().run()
