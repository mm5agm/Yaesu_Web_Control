!define APPNAME "Yaesu Web Control"
!define COMPANY "MM5AGM"
!ifndef VERSION
!define VERSION "2.5.3"
!endif
!define INSTALLDIR "$PROGRAMFILES64\${COMPANY}\${APPNAME}"
Name "${APPNAME} ${VERSION}"
OutFile "Yaesu_Web_Control_Setup.exe"
InstallDir "${INSTALLDIR}"
; On an upgrade, offer the folder YWC is already installed in rather than the
; default (#192). The install section writes InstallLocation to the uninstall
; key; a fresh install finds no key and falls back to InstallDir above.
; InstallDirRegKey can only read the 32-bit registry view, which is where
; installers up to v2.5.3-pre1 wrote the key. Later ones write the 64-bit
; view, and .onInit below reads that first, so both are found.
InstallDirRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "InstallLocation"

RequestExecutionLevel admin

Function .onInit
    SetRegView 64
    ReadRegStr $0 HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "InstallLocation"
    SetRegView 32
    StrCmp $0 "" +2
        StrCpy $INSTDIR $0
FunctionEnd

Page directory
Page instfiles

Section "Install"
    ; Stop any running instance of YWC before copying files. Without this,
    ; an upgrade install on top of a running YWC fails with NSIS's "Error
    ; opening file for writing" on every locked DLL (Accessibility.dll
    ; tends to be the first one hit). Reported by Ken KN2D 2026-06-15.
    ; Also kill the per-SDR worker processes (Yaesu_Sdr_Worker.exe) since
    ; those are spawned by YWC and hold their own copies of the worker
    ; binaries — they'd cause the same file-lock failure on a second SDR
    ; install. /F is the force flag; if the process isn't running,
    ; taskkill exits non-zero but ExecWait doesn't check the return code,
    ; so missing-process is harmless. The Sleep gives Windows a moment to
    ; release the file handles before the File copy begins.
    ExecWait 'taskkill /F /IM Yaesu_Web_Control.exe'
    ExecWait 'taskkill /F /IM Yaesu_Sdr_Worker.exe'
    Sleep 1500

    SetOutPath "$INSTDIR"

    ; Exclude files that must not be shipped or must not overwrite user data.
    ; The build-installer.ps1 script removes these before NSIS runs;
    ; the /x flags here are a belt-and-braces safety net.
    File /r \
        /x "*.pdb" \
        /x "libman.json" \
        /x "web.config" \
        /x "radio_state.json" \
        /x "appsettings.user.json" \
        "publish\*"

    ; --- SoapySDR backend (vendor DLLs + SDR plugins) ---
    ; Populated by scripts\collect-soapy-deps.ps1 before release.
    SetOutPath "$INSTDIR\SoapySDR\bin"
    File "soapysdr-dist\runtime\SoapySDR.dll"
    File "soapysdr-dist\runtime\airspy.dll"
    File "soapysdr-dist\runtime\hackrf.dll"
    File "soapysdr-dist\runtime\librtlsdr.dll"
    File "soapysdr-dist\runtime\libusb-1.0.dll"
    File "soapysdr-dist\runtime\libwinpthread-1.dll"
    File "soapysdr-dist\runtime\pthreadVC2.dll"
    File "soapysdr-dist\runtime\pthreadVC3.dll"

    SetOutPath "$INSTDIR\SoapySDR\lib\SoapySDR\modules0.8-3"
    File "soapysdr-dist\plugins\airspySupport.dll"
    File "soapysdr-dist\plugins\HackRFSupport.dll"
    File "soapysdr-dist\plugins\rtlsdrSupport.dll"

    ; Restore output path to app root for remaining install steps
    SetOutPath "$INSTDIR"

    CreateShortCut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\Yaesu_Web_Control.exe"

    ; Start menu entry goes straight into Programs, so that typing "Yaesu" into
    ; Start finds it. Up to v2.5.3-pre1 it lived in a folder named after the
    ; publisher, which sorts the app under M for MM5AGM. Same change as Icom
    ; Web Control made in its v1.0.5.
    CreateShortCut "$SMPROGRAMS\${APPNAME}.lnk" "$INSTDIR\Yaesu_Web_Control.exe"

    ; Remove the old entry so an upgrade leaves exactly one. RMDir without /r
    ; deletes the folder only if it is now empty, so an Icom Web Control
    ; shortcut still in there (IWC before v1.0.5) is left alone.
    Delete "$SMPROGRAMS\${COMPANY}\${APPNAME}.lnk"
    RMDir "$SMPROGRAMS\${COMPANY}"

    WriteUninstaller "$INSTDIR\Uninstall.exe"

    ; NSIS itself is a 32-bit process, so an unqualified HKLM write lands in
    ; Wow6432Node -- the wrong view for a 64-bit-only app. Clear the old 32-bit
    ; key first or an upgrade leaves two rows in Apps & features.
    SetRegView 32
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
    SetRegView 64

    ; DisplayName is the name column only -- Apps & features renders DisplayVersion
    ; in its own column beside it, so including the version here showed it twice.
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayName" "${APPNAME}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "UninstallString" "$INSTDIR\Uninstall.exe"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "InstallLocation" "$INSTDIR"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayIcon" "$INSTDIR\Yaesu_Web_Control.exe"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "Publisher" "${COMPANY}"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayVersion" "${VERSION}"
    WriteRegDWORD HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "EstimatedSize" 65000
SectionEnd

Section "Uninstall"
    ; Stop the app if it is running before deleting files
    ExecWait 'taskkill /F /IM Yaesu_Web_Control.exe'
    Sleep 1500

    Delete "$DESKTOP\${APPNAME}.lnk"
    Delete "$SMPROGRAMS\${APPNAME}.lnk"
    ; Pre-v2.5.3 location, still removed so uninstalling an old install leaves
    ; nothing behind; the folder itself only goes if it is empty.
    Delete "$SMPROGRAMS\${COMPANY}\${APPNAME}.lnk"
    RMDir "$SMPROGRAMS\${COMPANY}"
    RMDir /r "$INSTDIR"
    ; Both views: newer installers write the 64-bit one, older ones the 32-bit
    ; one, and an uninstaller built by either may run against either.
    SetRegView 32
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
    SetRegView 64
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
SectionEnd
