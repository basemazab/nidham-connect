# Code Signing Policy

> بالعربي باختصار: نسخة ويندوز من «نِظام كونكت» بتتوقّع رقميًا بشهادة متبرّع
> بيها من SignPath Foundation، والتوقيع بيحصل جوّه خط بناء GitHub Actions من
> الكود اللي في الريبو ده — مش من جهاز شخصي. الشهادة مش بتتسلّم لأي حد.

Free code signing for this project is provided by [SignPath.io](https://signpath.io/),
using a certificate from the [SignPath Foundation](https://signpath.org/).

## Team roles

This is a single-maintainer project. All three roles are held by the same
person, and this is stated plainly rather than padded with fictional
reviewers:

| Role | Who |
|---|---|
| Author | Basem Azab ([@basemazab](https://github.com/basemazab)) |
| Reviewer | Basem Azab |
| Approver | Basem Azab |

Multi-factor authentication is enabled on both the GitHub account that owns
this repository and the SignPath account used for signing.

## What gets signed, and from where

- The only signed artifact is `Nidham-Connect.exe`, the Windows build of this
  repository.
- It is built by the GitHub Actions workflow in
  [`.github/workflows/build.yml`](.github/workflows/build.yml), on a
  GitHub-hosted `windows-latest` runner, from a tagged commit on the `main`
  branch of this repository.
- The signing request is submitted from that workflow. Nothing is signed from
  a developer machine, and no signing key material is ever present on one.
- The test suite (`test/e2e.mjs`) must pass in the same workflow run before a
  release artifact is produced.

## Privacy and non-transfer

- The certificate is used exclusively to sign builds of this project. It is
  not shared with, lent to, or used on behalf of any other person, project or
  organisation.
- Private key material is generated and held on SignPath's HSM. It is never
  exported and never present on any machine controlled by this project.
- This program's own data handling is documented separately in
  [PRIVACY.md](PRIVACY.md): it transmits attendance records only to the
  operator's own Nidham HR account, and performs no telemetry of any kind.

## Reporting

Suspected misuse of the certificate, or a signed binary that does not
correspond to a commit in this repository, should be reported to
basemazab640@gmail.com and to SignPath.
