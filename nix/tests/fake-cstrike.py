"""
A stand-in for CS:S started with -usercon, for the desktop test. Installed as cstrike_linux64, so KSF Companion finds
it the way it finds the real game. It joins a KSF server (writes the console log the game would), answers on its remote
console with the password KSF Companion put in autoexec.cfg, and notes every command it gets in rcon-commands.txt.

Usage: cstrike_linux64 <cstrike folder> [port]
"""
import os
import re
import socket
import struct
import sys
import threading
import time

cstrike = sys.argv[1]
port = int(sys.argv[2]) if len(sys.argv) > 2 else 27015
received = open(os.path.join(cstrike, "rcon-commands.txt"), "a", buffering=1)


def say(*lines):
    with open(os.path.join(cstrike, "ksf_console.log"), "a") as log:
        for line in lines:
            log.write(line + "\n")


def password():
    try:
        with open(os.path.join(cstrike, "cfg", "autoexec.cfg")) as f:
            m = re.search(r'^rcon_password "([^"]*)"', f.read(), re.M)
            return m.group(1) if m else None
    except OSError:
        return None


def packet(request, kind, body):
    data = body.encode() + b"\0\0"
    return struct.pack("<iii", len(data) + 8, request, kind) + data


def read_exactly(conn, n):
    buf = b""
    while len(buf) < n:
        chunk = conn.recv(n - len(buf))
        if not chunk:
            raise EOFError
        buf += chunk
    return buf


def answer(command):
    out = []
    for part in command.split(";"):
        part = part.strip()
        echo = re.match(r'^echo\s+"?(.*?)"?$', part)
        if echo:
            out.append(echo.group(1))
        elif part == "status":
            out += ["hostname: KSF - Beginner EU | Surf | ksf.surf", "udp/ip  : 192.0.2.10:27015",
                    "map     : surf_utopia_njv at: 0 x, 0 y, 0 z", "players : 12 humans, 0 bots (24 max)", "",
                    "# userid name uniqueid connected ping loss state", '#      2 "alice" [U:1:2000] 10:00 40 0 active']
        elif part == "mp_timelimit":
            out.append('"mp_timelimit" = "80" ( def. "0" )')
    return "".join(line + "\n" for line in out)


def serve(conn):
    authed = False
    try:
        while True:
            size = struct.unpack("<i", read_exactly(conn, 4))[0]
            data = read_exactly(conn, size)
            request, kind = struct.unpack("<ii", data[:8])
            body = data[8:-2].decode()
            if kind == 3:
                authed = body == password()
                conn.sendall(packet(request, 0, "") + packet(request if authed else -1, 2, ""))
            elif kind == 2 and authed:
                received.write(body + "\n")
                reply = answer(body)
                if reply:
                    conn.sendall(packet(request, 0, reply))
            elif kind == 0:
                conn.sendall(packet(request, 0, "") + packet(request, 0, "\0\x01"))
    except (EOFError, OSError):
        pass
    finally:
        conn.close()


time.sleep(1)
say("Connected to 192.0.2.10:27015", "", "Counter-Strike: Source", "Map: surf_utopia_njv", "Players: 12 / 24", "")
server = socket.socket()
server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
server.bind(("127.0.0.1", port))
server.listen(4)
while True:
    client, _ = server.accept()
    threading.Thread(target=serve, args=(client,), daemon=True).start()
