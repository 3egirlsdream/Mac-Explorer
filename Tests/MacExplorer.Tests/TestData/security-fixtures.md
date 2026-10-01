# Security follow-up fixtures

All contents and coordinates are synthetic. Do not substitute user files or credentials.

- `legacy-dotnetzip-aes256.zip`: generated on 2026-09-30 with the prior DotNetZip 1.16.0 writer in a separate temporary .NET project launched with `Tools/Testing/run-isolated.sh`. Password: `fixture-password`. Entry: `中文目录/旧照片.txt`, UTF-8 name; payload: `legacy AES256 中文 fixture`. This is a read-only interoperability fixture; DotNetZip is not a production or test package reference anymore.
- `gps-ocr-fixture.jpg`: generated using AppKit / ImageIO, 800×600 white image with `PRIVACY TEST 2026`, GPS N30.25 E120.5, camera make `Fixture`, model `QA Camera`. The coordinates do not represent a user photo. Tests copy it into their own temporary root before invoking the native helper without networking permission.
- `sftp-loopback.py`: opt-in loopback-only SFTP server using Paramiko. Host keys, log and configuration are generated under the test root. Username/password: `fixture` / `fixture-secret`. Authentication logging records only an event name, never secrets. Tests require `MACEXPLORER_SFTP_TEST_PYTHON` pointing at a separate virtualenv; no system Python packages, real SSH config, agents or Keychain are used.
