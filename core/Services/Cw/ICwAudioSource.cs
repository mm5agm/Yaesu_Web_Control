namespace RadioWebControl.Core.Services.Cw
{
    /// <summary>
    /// The seam between the applications' audio capture and the decoder.
    ///
    /// Core has no package references on purpose, and opening a sound device
    /// needs one (PortAudioSharp in Yaesu Web Control, whatever Icom Web Control
    /// ends up using). So Core declares the shape of an audio source and each
    /// application implements it over its own capture stack. The decoder never
    /// learns what kind of device is on the other end, or whether there is a
    /// device at all, which is exactly what lets the test suite feed it
    /// generated audio.
    ///
    /// Frames are mono 32-bit float, nominally 480 samples of 48 kHz audio
    /// (10 ms), matching what the existing capture already produces. The decoder
    /// does not require that exact length; it buffers.
    /// </summary>
    public interface ICwAudioSource
    {
        /// <summary>Sample rate of the frames handed to FrameAvailable.</summary>
        int SampleRate { get; }

        /// <summary>True between a successful Start and a Stop.</summary>
        bool IsRunning { get; }

        /// <summary>
        /// Raised for each captured frame. The memory is only guaranteed valid
        /// for the duration of the call: copy anything that has to outlive it.
        /// </summary>
        event Action<ReadOnlyMemory<float>>? FrameAvailable;

        Task StartAsync(CancellationToken cancellationToken = default);
        Task StopAsync(CancellationToken cancellationToken = default);
    }
}
