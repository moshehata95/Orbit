#!/usr/bin/env python3
"""Orbit VNC actuator - the "computer use" layer.

Orbit - remote control. Developed by Dr. Mohamed Shehata.

Driven by Orbit, which opens an SSH tunnel then calls this against
127.0.0.1:<localport>. Password comes from the ORBIT_VNC_PW env var (never argv).

Usage:
  orbit-vnc.py capture <host::port> <out.png>
  orbit-vnc.py click   <host::port> <x> <y> [button]
  orbit-vnc.py move    <host::port> <x> <y>
  orbit-vnc.py type    <host::port> <text>
  orbit-vnc.py key     <host::port> <key[ key ...]>
  orbit-vnc.py probe   <host::port>          # authenticate only (fast, no capture)
"""
import os
import sys
import time
import socket
import struct
import warnings

warnings.filterwarnings("ignore")  # silence CryptographyDeprecationWarning (DES) & friends


# ---- fast, capture-free auth check used for pairing ----------------------
# A full screen capture can stall (large framebuffers, slow links); pairing only
# needs to know the code is right, so we do just the RFB handshake + VNC auth and
# stop. Exit: 0 = ok, 5 = wrong code, 3 = screen channel unreachable.
def _split_server(server):
    host, port = server, 5900
    if "::" in server:
        host, p = server.rsplit("::", 1)
        port = int(p)
    elif ":" in server:
        host, d = server.rsplit(":", 1)
        port = 5900 + int(d)
    return host, port


def _recvn(sock, n):
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise IOError("connection closed by server")
        buf += chunk
    return buf


def _probe(server):
    from vncdotool.rfb import des_encrypt, _vnc_des  # exact VNC DES the client uses
    pw = os.environ.get("ORBIT_VNC_PW") or ""
    host, port = _split_server(server)
    try:
        s = socket.create_connection((host, port), timeout=8)
    except Exception as e:
        print("connect-failed: %s" % e, file=sys.stderr)
        return 3
    try:
        s.settimeout(8)
        _recvn(s, 12)                       # ProtocolVersion "RFB 003.00x\n"
        s.sendall(b"RFB 003.008\n")
        count = _recvn(s, 1)[0]
        if count == 0:                      # server refused; reason string follows
            rlen = struct.unpack(">I", _recvn(s, 4))[0]
            print("refused: %s" % _recvn(s, rlen).decode("latin-1", "ignore"), file=sys.stderr)
            return 3
        types = _recvn(s, count)
        if 2 in types:                      # VNC authentication
            s.sendall(b"\x02")
            challenge = _recvn(s, 16)
            s.sendall(des_encrypt(_vnc_des(pw), challenge))
            result = struct.unpack(">I", _recvn(s, 4))[0]
            if result == 0:
                print("ok")
                return 0
            print("auth-failed: wrong code", file=sys.stderr)
            return 5
        if 1 in types:                      # "None" - no password required
            s.sendall(b"\x01")
            result = struct.unpack(">I", _recvn(s, 4))[0]
            print("ok" if result == 0 else "auth-failed", file=(None if result == 0 else sys.stderr))
            return 0 if result == 0 else 5
        print("no-vnc-auth: server offers %s" % list(types), file=sys.stderr)
        return 3
    except Exception as e:
        print("probe-failed: %s" % e, file=sys.stderr)
        return 3
    finally:
        try:
            s.close()
        except Exception:
            pass


# ---- full actuator (screen + input) via vncdotool ------------------------
def _connect(server):
    from vncdotool import api
    pw = os.environ.get('ORBIT_VNC_PW') or None
    client = api.connect(server, password=pw)
    client.timeout = 15
    return client

# vncdotool sends a single character as keysym = ord(ch); the server handles shift.
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

    if verb == 'probe':
        return _probe(server)

    try:
        client = _connect(server)
    except Exception as e:
        print('connect-failed: %s' % e, file=sys.stderr)
        return 3
    try:
        if verb == 'capture':
            out = rest[0] if rest else os.path.join(os.environ.get('TEMP', '.'), 'orbit-shot.png')
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
            print('unknown-verb: %s' % verb, file=sys.stderr); return 2
        time.sleep(0.15)
        return 0
    except Exception as e:
        print('action-failed: %s' % e, file=sys.stderr)
        return 4
    finally:
        try: client.disconnect()
        except Exception: pass

if __name__ == '__main__':
    sys.exit(main())
