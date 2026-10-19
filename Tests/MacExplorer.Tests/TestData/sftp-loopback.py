"""Opt-in local SFTP fixture. Requires Paramiko in an isolated QA virtualenv.
No user config, credentials, home directory, or external network is used.
"""
import json
import os
import socket
import sys
import threading
import time
import paramiko

root, key_name, port_text = sys.argv[1:]
events = os.path.join(root, "events.jsonl")
paramiko.util.log_to_file(os.path.join(root, "transport.log"))
log_lock = threading.Lock()
def record(event):
    with log_lock:
        with open(events, "a", encoding="utf-8") as log:
            log.write(json.dumps({"event": event}) + "\n")

key_path = os.path.join(root, key_name + ".pem")
if os.path.exists(key_path):
    key = paramiko.RSAKey.from_private_key_file(key_path)
else:
    key = paramiko.RSAKey.generate(2048)
    key.write_private_key_file(key_path)

client_path = os.path.join(root, "client-encrypted.pem")
if not os.path.exists(client_path):
    client_key = paramiko.ECDSAKey.generate(bits=256)
    client_key.write_private_key_file(client_path, password="fixture-private-key-secret")
    client_key.write_private_key_file(os.path.join(root, "client-clear.pem"))
else:
    client_key = paramiko.ECDSAKey.from_private_key_file(client_path, password="fixture-private-key-secret")

class Server(paramiko.ServerInterface):
    def check_auth_publickey(self, username, public_key):
        record("authentication")
        return paramiko.AUTH_SUCCESSFUL if username == "fixture" and public_key == client_key else paramiko.AUTH_FAILED

    def check_auth_password(self, username, password):
        record("authentication")
        return paramiko.AUTH_SUCCESSFUL if username == "fixture" and password == "fixture-secret" else paramiko.AUTH_FAILED
    def get_allowed_auths(self, username):
        return "password,publickey"
    def check_channel_request(self, kind, chanid):
        return paramiko.OPEN_SUCCEEDED if kind == "session" else paramiko.OPEN_FAILED_ADMINISTRATIVELY_PROHIBITED

storage = os.path.join(root, "remote-files")
os.makedirs(storage, exist_ok=True)
fixture_file = os.path.join(storage, "fixture.txt")
if not os.path.exists(fixture_file):
    with open(fixture_file, "w", encoding="utf-8") as fixture:
        fixture.write("fixture")

class Files(paramiko.SFTPServerInterface):
    def local(self, path):
        candidate = os.path.realpath(os.path.join(storage, path.lstrip("/")))
        if os.path.commonpath([candidate, os.path.realpath(storage)]) != os.path.realpath(storage):
            raise PermissionError("Outside isolated fixture")
        return candidate

    def list_folder(self, path):
        try:
            result = []
            for name in os.listdir(self.local(path)):
                item = paramiko.SFTPAttributes.from_stat(os.lstat(os.path.join(self.local(path), name)))
                item.filename = name
                result.append(item)
            return result
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def stat(self, path):
        try:
            return paramiko.SFTPAttributes.from_stat(os.stat(self.local(path)))
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def lstat(self, path):
        return self.stat(path)

    def open(self, path, flags, attr):
        try:
            descriptor = os.open(self.local(path), flags, 0o600)
            mode = "r+b" if flags & os.O_RDWR else "wb" if flags & os.O_WRONLY else "rb"
            stream = os.fdopen(descriptor, mode)
            handle = paramiko.SFTPHandle(flags)
            handle.readfile = stream
            handle.writefile = stream
            return handle
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def remove(self, path):
        try:
            os.remove(self.local(path))
            return paramiko.SFTP_OK
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def mkdir(self, path, attr):
        try:
            os.mkdir(self.local(path))
            return paramiko.SFTP_OK
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def rmdir(self, path):
        try:
            os.rmdir(self.local(path))
            return paramiko.SFTP_OK
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def rename(self, oldpath, newpath):
        try:
            if os.path.exists(self.local(newpath)):
                return paramiko.SFTP_FAILURE
            os.rename(self.local(oldpath), self.local(newpath))
            return paramiko.SFTP_OK
        except OSError as error:
            return paramiko.SFTPServer.convert_errno(error.errno)

    def canonicalize(self, path):
        import posixpath
        return posixpath.normpath("/" + path.lstrip("/"))

listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
listener.bind(("127.0.0.1", int(port_text)))
listener.listen(8)
print(listener.getsockname()[1], flush=True)

def serve(connection):
    transport = paramiko.Transport(connection)
    transport.add_server_key(key)
    transport.set_subsystem_handler("sftp", paramiko.SFTPServer, Files)
    try:
        transport.start_server(server=Server())
        while transport.is_active():
            time.sleep(0.05)
    except Exception:
        pass
    finally:
        transport.close()

while True:
    connection, _ = listener.accept()
    threading.Thread(target=serve, args=(connection,), daemon=True).start()
