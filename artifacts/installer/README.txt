Kiary Installer Artifacts

- MSIX package folder:
  artifacts\\installer\\msix\\EncryptedDiary.WinUI_1.0.0.0_x64_Test

- One-step installer launcher:
  artifacts\\installer\\Install-Kiary.ps1
- One-step signed package builder:
  artifacts\\installer\\Build-Kiary-Installer.ps1

How to install:
1. Open PowerShell as your user.
2. Run:
   powershell -ExecutionPolicy Bypass -File .\\artifacts\\installer\\Install-Kiary.ps1

How to rebuild MSIX before installing:
1. Run:
   powershell -ExecutionPolicy Bypass -File .\\artifacts\\installer\\Build-Kiary-Installer.ps1
2. Re-run installer script above.

Notes:
- The script will request admin elevation and install the package certificate into
  CurrentUser/LocalMachine trust stores before installing the MSIX.
- Build-Kiary-Installer.ps1 signs the MSIX automatically and creates a self-signed
  code-signing cert in CurrentUser\\My if one does not exist.
- By default, the certificate subject is read from Package.appxmanifest
  Package/Identity/Publisher so signing matches Store identity.
- You can override subject explicitly:
  powershell -ExecutionPolicy Bypass -File .\\artifacts\\installer\\Build-Kiary-Installer.ps1 -CertSubject "CN=YourPublisher"
- The installer always picks the newest .msix and uses ForceUpdateFromAnyVersion so
  same-version rebuilds can be reinstalled.
- If no .cer is present next to the .msix, the installer exports it from the MSIX
  signature automatically (for signed packages).
- Build output (portable publish) is under:
  artifacts\\publish\\win-x64

Public release guidance:
- Do not ship with this self-signed certificate.
- Use Microsoft Store distribution, or a trusted OV/EV code-signing certificate
  from a public CA and match Package.appxmanifest Publisher exactly.
