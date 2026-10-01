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

class Files(paramiko.SFTPServerInterface):
    def list_folder(self, path):
        item = paramiko.SFTPAttributes()
        item.filename = "fixture.txt"
        item.st_size = 7
        item.st_mode = 0o100644
        return [item]
    def canonicalize(self, path):
        return "/"

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
