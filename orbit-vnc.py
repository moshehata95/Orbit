#!/usr/bin/env python3
"""Orbit VNC actuator — the "computer use" layer.

Orbit — remote control. Developed by Dr. Mohamed Shehata.


Driven by orbit-cli.ps1, which opens an SSH tunnel then calls this against
127.0.0.1:<localport>. Password comes from the ORBIT_VNC_PW env var (never argv).

Usage:
  orbit-vnc.py capture <host::port> <out.png>
  orbit-vnc.py click   <host::port> <x> <y> [button]
  orbit-vnc.py move    <host::port> <x> <y>
  orbit-vnc.py type    <host::port> <text>
  orbit-vnc.py key     <host::port> <key[ key ...]>
  orbit-vnc.py probe   <host::port>          # connect + capture size only
"""
import os
import sys
import time

def _connect(server):
    from vncdotool import api
    pw = os.environ.get('ORBIT_VNC_PW') or None
    client = api.connect(server, password=pw)
    client.timeout = 15
    return client

# vncdotool يبعت المحرف المفرد كـ keysym = ord(ch)، والسيرفر بيتولّى الـ shift.
_SPECIAL = {' ': 'space', '\n': 'return', '\t': 'tab'}

def _type(client, text):
    for ch in text:
        client.keyPress(_SPECIAL.get(ch, ch))
        time.sleep(0.02)

def main():
    if len(sys.argv) < 3:
        print('usage: orbit-vnc.py <verb> <host::port> [...]', file=sys.stderr)
        return 2
    verb, server = sys.argv[1], sys.argv[2]
    rest = sys.argv[3:]
    try:
        client = _connect(server)
    except Exception as e:
        print(f'connect-failed: {e}', file=sys.stderr)
        return 3
    try:
        if verb == 'capture' or verb == 'probe':
            out = rest[0] if (verb == 'capture' and rest) else os.path.join(os.environ.get('TEMP', '.'), 'orbit-shot.png')
            client.refreshScreen()
            time.sleep(0.3)
            client.captureScreen(out)
            print(out)
        elif verb == 'move':
            client.mouseMove(int(rest[0]), int(rest[1]))
        elif verb == 'click':
            btn = int(rest[2]) if len(rest) > 2 else 1
            client.mouseMove(int(rest[0]), int(rest[1])); time.sleep(0.05); client.mousePress(btn)
        elif verb == 'dclick':
            client.mouseMove(int(rest[0]), int(rest[1])); time.sleep(0.05)
            client.mousePress(1); time.sleep(0.08); client.mousePress(1)
        elif verb == 'type':
            _type(client, ' '.join(rest))
        elif verb == 'key':
            for k in rest:
                client.keyPress(k); time.sleep(0.03)
        else:
            print(f'unknown-verb: {verb}', file=sys.stderr); return 2
        time.sleep(0.15)
        return 0
    except Exception as e:
        print(f'action-failed: {e}', file=sys.stderr)
        return 4
    finally:
        try: client.disconnect()
        except Exception: pass

if __name__ == '__main__':
    sys.exit(main())
