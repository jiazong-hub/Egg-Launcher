# Code signing policy

ChatGPT Local Launcher 0.9.0 public beta is signed with a project-controlled,
self-signed Authenticode code-signing certificate. The signature establishes
continuity between releases made by the holder of the same private key and
detects changes made after signing. It is not a third-party identity validation
or a Microsoft Public Trust certificate.

- Displayed developer: 甲总不是贾总
- Certificate subject: `CN=甲总不是贾总`
- Signature algorithm: RSA 3072 / SHA-256
- SHA-1 certificate thumbprint: `44A03B7EA758C184DB1F15E5ADC6DE7EBDC8153C`
- SHA-256 certificate fingerprint: `04FD703BF16895B37931EA173B68CA25DD73B3A06D110869717056CE786B2262`
- Valid from: 2026-09-14 10:19:43 +08:00
- Valid until: 2031-09-14 10:24:43 +08:00

Users should compare the certificate fingerprint embedded in the downloaded
binary with the value published in the official source repository and release
notes. Installing the self-signed certificate into a Windows trust store is not
required and should never be requested merely to run the portable application.

The password-protected PFX private key is not stored in this repository or in
release packages. Only the project maintainer controls the private key.

