!define APPNAME "Yaesu Web Control"
!define COMPANY "MM5AGM"
!define VERSION "2.3.8"
!define INSTALLDIR "$PROGRAMFILES64\${COMPANY}\${APPNAME}"
Name "${APPNAME} ${VERSION}"
OutFile "Yaesu_Web_Control_Setup.exe"
InstallDir "${INSTALLDIR}"

RequestExecutionLevel admin

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
    CreateDirectory "$SMPROGRAMS\${COMPANY}"
    CreateShortCut "$SMPROGRAMS\${COMPANY}\${APPNAME}.lnk" "$INSTDIR\Yaesu_Web_Control.exe"

    WriteUninstaller "$INSTDIR\Uninstall.exe"

    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}" "DisplayName" "${APPNAME} ${VERSION}"
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
    Delete "$SMPROGRAMS\${COMPANY}\${APPNAME}.lnk"
    RMDir "$SMPROGRAMS\${COMPANY}"
    RMDir /r "$INSTDIR"
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APPNAME}"
SectionEnd
