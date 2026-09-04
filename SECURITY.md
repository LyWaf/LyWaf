# Security Policy

## Reporting

Do not publish suspected vulnerabilities, exploit payloads, credentials, private keys or production logs in a public issue. Report privately to the repository maintainer and include the affected version, configuration, minimal reproduction, impact and any known workaround.

## Response Process

1. Acknowledge and assign an incident owner.
2. Reproduce against a supported release without using production data.
3. Classify impact, affected versions and whether credentials or signing material require rotation.
4. Develop the smallest fix with regression tests and review security-sensitive defaults.
5. Build packages through `tools/release.ps1`, publish SHA-256 checksums and document upgrade and rollback steps.
6. Coordinate disclosure after a fixed release is available. Credit is opt-in.

Critical issues that permit unauthenticated remote code execution, authentication bypass, arbitrary file access or WAF bypass receive priority over normal feature work. If a dependency CVE is not reachable in LyWaf, record the analysis rather than silently ignoring it.

## Release Requirements

- Backend tests and frontend production build pass on supported CI systems.
- Configuration and upgrade paths have rollback instructions.
- Release archives include an internal `SHA256SUMS`; external archive checksums are published beside them.
- Secrets, runtime databases, logs, generated authentication files and plugin data are excluded.
- Security changes name affected versions and operator actions without publishing unnecessary weaponized detail.
