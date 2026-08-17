Required before merge
Stop reopening the wrong device after unplug/replug The current retry logic can reuse index:N after the original capture device disappears. If Windows assigns that index to another camera, YWC may silently stream the wrong device, possibly the laptop webcam. The agreed behaviour for this PR is:

1. Transition to Disconnected. status - READY FOR TESTING

    1. Do not automatically open whatever device now occupies that index.

    2. Require the user to refresh the device list and start the stream again.

    3. Remove or disable the current automatic recovery behaviour and update the manual/PR description accordingly.

1. Stop or back off the retry loop - status - READY FOR TESTING - Once the service is deliberately staying Disconnected, it should not continue attempting the same invalid device every approximately 2.25 seconds and logging warnings indefinitely. The retry loop should either stop until the user refreshes/restarts the stream or apply an appropriate backoff.

1. Log the disconnect reason - status - READY FOR TESTING - The MarkDisconnected path used by the empty-frame failure currently does not emit a clear log entry. The log should explicitly record that the capture device was marked disconnected and why. At present, the log can jump from normal streaming directly to a later “failed to open device” message, making diagnosis unnecessarily difficult.

1. Replace the 30-second counter-based timeout - status - READY FOR TESTING - The passthrough path currently relies on approximately 45 iterations of a 1-second read wait. This makes the disconnect detection time an indirect consequence of two constants. It should be changed to an elapsed-time or deadline-based check, both to make the intended timeout explicit and to make the behaviour easier to tune.

Later (another session — not a merge blocker)

1. Fix four wrong GUID constants in WindowsMfMjpegSession.cs - status - PENDING - Colin checked these against mfidl.h / mfreadwrite.h from SDK 10.0.26100.0. The first is fatal: SetGUID(SOURCE_TYPE) writes an attribute nobody reads, so MFEnumDeviceSources returns 0xC00D36E6 (MF_E_ATTRIBUTENOTFOUND) and the MF path always falls through to OpenCV. DirectShow now carries passthrough on Windows, so this only matters when DirectShow cannot rank an MJPEG pin. MF_MT_* and the symbolic-link GUID are already correct.

    | Constant | In the branch | Windows SDK |
    |---|---|---|
    | MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE | c60ac5fe-672d-41ef-afc3-1f319d7f80b0 | c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3 |
    | MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID | 8ac3587a-4ba1-4d9f-abb4-946d5be8add6 | 8ac3587a-4ae7-42d8-99e0-0a6013eef90f |
    | MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME | 60d0e559-52f8-4fa2-87e0-b834d243a97f | 60d0e559-52f8-4fa2-bbce-acdb34a8ec01 |
    | MF_READWRITE_DISABLE_CONVERTERS | 98d44c05-8a0d-4b80-8f1a-6f9a315fad27 | 98d5b065-1374-4847-8d5d-31520fee7156 |
