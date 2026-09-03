# Security Policy

## Reporting a vulnerability

Email **basemazab640@gmail.com** with "nidham-connect security" in the
subject. Please include what you found, how to reproduce it, and what you
think the impact is. This is a one-person project: expect a first reply within
a few days rather than within hours.

Please do not open a public issue for anything that could be used against a
running installation before it is fixed.

## Scope

**In scope** — this repository: the ZK protocol client, the pairing and sync
client, the updater and its self-replacement mechanism, the local
configuration and log handling, and the build and release scripts.

**Out of scope** — the Nidham HR server (`nidhamhr.com`) and its API. Report
those to the same address, but they are not part of this repository.

## About the local-network scan

This program contains a network scan, and it is easy to misread it, so here is
exactly what it does — see [`src/Scan.cs`](src/Scan.cs), it is 100 lines:

- It attempts a **TCP connect** to **one port, 4370**, the ZKTeco attendance
  protocol port. Nothing else is probed.
- It sweeps only the `/24` of an address **the machine itself already holds**,
  and only when that address is in a private range (`10.0.0.0/8`,
  `172.16.0.0/12`, `192.168.0.0/16`). A public address is skipped outright.
- It runs only when the operator presses "دوّر في الشبكة" (Search the
  network), on their own network, to find their own attendance device — the
  same thing the manufacturer's ZKTime software does.
- A successful connect is only a hint. The program then performs a real
  protocol handshake and reads the device's serial number to confirm it is an
  attendance device.
- It never sends credentials, never attempts authentication against anything
  it finds, and never probes for or exploits a weakness. If a device is
  protected by a Comm Key, the program reports that it is protected and asks
  the operator for the key. It does not attempt to recover or bypass it.

If you want the device found without any scan at all, add it by IP address
instead — the scan is a convenience, not a requirement.

## Known limitations, stated plainly

- **The binary is not yet code-signed.** This is why the source is public: to
  apply for a free certificate from the SignPath Foundation. Until then
  Windows SmartScreen will warn on first run.
- **The updater does not verify a code signature** on the downloaded
  replacement, because there is no signature to verify yet. It checks that the
  download came from GitHub over TLS, that the file is a PE image, and that
  its size matches what the release metadata declared. Authenticode
  verification is the first thing that gets added once a certificate exists.
- **The pairing token is stored in plain text** in
  `%APPDATA%\Nidham Connect\config.json`, protected only by the Windows user's
  file permissions. Anyone who can already read that user's files can read the
  token, and with it upload attendance records to that one tenant. It grants
  no read access to the tenant's data.
- **The protocol client has been exercised against a byte-exact simulator**
  (`test/zk-sim.js`), not yet against every device family in the wild. Reports
  of a device that misbehaves are very welcome — run
  `Nidham-Connect.exe --probe <ip> --full` and send the output.
