"""Run the built macOS app against disposable authenticated HTTP/HTTPS WebDAV servers.

Requires an isolated Python environment with wsgidav==4.4.0 and cheroot==11.1.2.
Usage: python webdav-smoke.py /absolute/path/to/MacExplorer
"""
import json
import logging
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import tempfile
import threading
import time

from cheroot import wsgi
from cheroot.ssl.builtin import BuiltinSSLAdapter
from wsgidav.wsgidav_app import WsgiDAVApp


def start_apache(root, data):
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    subprocess.run(["/usr/sbin/htpasswd", "-bc", str(root / "users"), "fixture", "fixture-secret"],
                   check=True, capture_output=True)
    modules = ["mpm_prefork", "unixd", "authn_core", "authn_file", "authz_core", "authz_user",
               "auth_basic", "dav", "dav_fs", "alias", "log_config"]
    configuration = f'ServerRoot "{root}"\nPidFile "{root}/httpd.pid"\nListen 127.0.0.1:{port}\nServerName localhost\n'
    configuration += "".join(f"LoadModule {name}_module /usr/libexec/apache2/mod_{name}.so\n" for name in modules)
    configuration += f'''ErrorLog "{root}/apache-error.log"
CustomLog "{root}/apache-access.log" "%m %U %s"
DavLockDB "{root}/dav-lock"
Alias /dav/ "{data}/"
<Directory "{data}">
Dav On
AuthType Basic
AuthName fixture
AuthUserFile "{root}/users"
Require valid-user
</Directory>
'''
    config = root / "httpd.conf"
    config.write_text(configuration)
    subprocess.run(["/usr/sbin/httpd", "-t", "-f", str(config)], check=True, capture_output=True)
    with (root / "apache.log").open("w") as log:
        process = subprocess.Popen(["/usr/sbin/httpd", "-f", str(config), "-DFOREGROUND"],
                                   stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
    return process, port


def main():
    repository = Path(__file__).resolve().parents[2]
    root = Path(tempfile.mkdtemp(prefix="macexplorer-webdav-fixture-"))
    data = root / "data"
    data.mkdir()
    (data / "seed.txt").write_text("fixture seed")
    certificate, key = root / "certificate.pem", root / "key.pem"
    subprocess.run(["/usr/bin/openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes",
                    "-keyout", str(key), "-out", str(certificate), "-days", "1",
                    "-subj", "/CN=127.0.0.1", "-addext", "subjectAltName=IP:127.0.0.1"],
                   check=True, capture_output=True)
    logging.disable(logging.CRITICAL)
    application = WsgiDAVApp({
        "provider_mapping": {"/dav": str(data), "/slow": str(data)},
        "simple_dc": {"user_mapping": {"*": {"fixture": {"password": "fixture-secret", "roles": []}}}},
        "http_authenticator": {"accept_basic": True, "accept_digest": True, "default_to_digest": True},
        "verbose": 0,
        "logging": {"enable": False},
    })

    def app(environ, start_response):
        def start(status, headers, exc_info=None):
            with (root / "requests.log").open("a") as log:
                log.write(f"{environ.get('REQUEST_METHOD')} {environ.get('PATH_INFO')} {status}\n")
            return start_response(status, headers, exc_info)
        if environ.get("PATH_INFO", "").startswith("/slow"):
            (root / "slow-started").touch()
            time.sleep(2)
        return application(environ, start)

    servers = []
    ports = []
    apache = None
    try:
        for tls in (False, True):
            server = wsgi.Server(("127.0.0.1", 0), app, numthreads=10)
            if tls:
                server.ssl_adapter = BuiltinSSLAdapter(str(certificate), str(key))
            server.prepare()
            threading.Thread(target=server.serve, daemon=True).start()
            servers.append(server)
        ports = [server.socket.getsockname()[1] for server in servers]
        # Apple's client mounts Apache DAV correctly; retain WsgiDAV as an explicit compatibility probe.
        server_kind = os.environ.get("MACEXPLORER_WEBDAV_TEST_SERVER", "apache")
        if server_kind not in ("apache", "wsgidav"):
            raise ValueError("MACEXPLORER_WEBDAV_TEST_SERVER must be apache or wsgidav")
        mount_port = ports[0]
        if server_kind == "apache":
            apache, mount_port = start_apache(root, data)
            ports.append(mount_port)
            for attempt in range(100):
                if apache.poll() is not None:
                    raise RuntimeError("The isolated Apache fixture exited before accepting connections.")
                try:
                    with socket.create_connection(("127.0.0.1", mount_port), timeout=0.1):
                        break
                except OSError:
                    if attempt == 99:
                        raise RuntimeError("The isolated Apache fixture did not start.")
                    time.sleep(0.05)
        print(f"WebDAV fixture server: {server_kind}", flush=True)
        env = dict(os.environ, MACEXPLORER_WEBDAV_SMOKE="1",
                   MACEXPLORER_WEBDAV_TEST_URL=f"http://127.0.0.1:{mount_port}/",
                   MACEXPLORER_WEBDAV_TEST_SLOW_URL=f"http://127.0.0.1:{ports[0]}/",
                   MACEXPLORER_WEBDAV_TEST_TLS_URL=f"https://127.0.0.1:{ports[1]}/",
                   MACEXPLORER_WEBDAV_TEST_DATA=str(root))
        with (root / "app.log").open("w") as log:
            result = subprocess.run(["/bin/bash", str(repository / "Tools/Testing/run-isolated.sh"), sys.argv[1]],
                                    env=env, stdout=log, stderr=subprocess.STDOUT, text=True, timeout=120)
        output = (root / "app.log").read_text()
        print(output)
        reports = re.findall(r"WebDAV smoke: ([^;]+);", output)
        if not reports:
            raise RuntimeError("The application did not produce a WebDAV smoke report.")
        report = json.loads(Path(reports[-1]).read_text())
        print(json.dumps(report, ensure_ascii=False, indent=2))
        if result.returncode or report["error"]:
            raise RuntimeError("WebDAV native smoke failed; inspect the report above.")
    finally:
        # Clean up only mounts from these ephemeral loopback ports, including after an app timeout.
        for port in ports:
            mounted = subprocess.run(["/sbin/mount", "-t", "webdav"], capture_output=True, text=True).stdout
            for line in mounted.splitlines():
                if f"127.0.0.1:{port}/" in line and " on " in line:
                    path = line.split(" on ", 1)[1].rsplit(" (", 1)[0]
                    try:
                        result = subprocess.run(["/usr/sbin/diskutil", "unmount", path], capture_output=True, timeout=15)
                        if result.returncode:
                            subprocess.run(["/sbin/umount", "-f", path], capture_output=True, timeout=10)
                    except subprocess.TimeoutExpired:
                        # A provisional mount can stall Disk Arbitration; this is only our disposable fixture.
                        subprocess.run(["/sbin/umount", "-f", path], capture_output=True, timeout=10)
        if apache is not None:
            apache.terminate()
            try:
                apache.wait(timeout=10)
            except subprocess.TimeoutExpired:
                apache.kill()
                apache.wait()
        for server in servers:
            server.stop()
        print(f"WebDAV fixture retained for inspection: {root}")


if __name__ == "__main__":
    main()
