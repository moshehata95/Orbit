Orbit — Control your computers remotely
=======================================

One folder you copy to any computer. The MAIN computer controls the OTHER
computers on the same network, one way only, over two channels: commands
(SSH, key-based) and screen (VNC, code-protected).

Run
---
On each computer: double-click "Run Orbit.cmd".
- Main computer (yours): pick "Main computer". It scans for devices.
- Other computer:        pick "The other computer" -> click Yes on the UAC
                         prompt -> a code appears.
- On the main computer:  pick the device from the list, type its code -> Connected.

Each other computer has its own code. The main computer remembers it, so you
type it only once.

Notes
-----
- Each other computer needs internet ONCE during setup (it downloads OpenSSH
  and the small screen server).
- Antivirus (Kaspersky, etc.) may warn about the screen server (TightVNC) as a
  "remote access" tool. That is expected - allow it so the screen channel works.
- Security: the other computer trusts only the main computer's key and never
  receives a key back (one way). The screen server binds to 127.0.0.1 and is
  reached only through the encrypted SSH tunnel.

Command line (for automated testing)
------------------------------------
After pairing, from the main computer:
  %LOCALAPPDATA%\Orbit\orbit.cmd list
  %LOCALAPPDATA%\Orbit\orbit.cmd ssh   "Reception PC" "PowerShell command"
  %LOCALAPPDATA%\Orbit\orbit.cmd shot  "Reception PC" out.png
  %LOCALAPPDATA%\Orbit\orbit.cmd click "Reception PC" x y
  %LOCALAPPDATA%\Orbit\orbit.cmd type  "Reception PC" "text"
  %LOCALAPPDATA%\Orbit\orbit.cmd key   "Reception PC" enter
  %LOCALAPPDATA%\Orbit\orbit.cmd tunnel "Reception PC" 3443 3443   (test a web app)

Remove
------
Open "Run Orbit.cmd" on the computer -> "Remove remote control" -> it restores
the computer to how it was.
