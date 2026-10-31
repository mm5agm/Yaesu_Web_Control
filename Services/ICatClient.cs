using System;
using System.Threading;
using System.Threading.Tasks;

namespace Yaesu_Web_Control.Services
{
    public interface ICatClient : IDisposable
    {
        Task<bool> ConnectAsync(string portName, int baudRate = 38400);
        Task DisconnectAsync();
        bool IsConnected { get; }
        Task<string> SendCommandAsync(string command, string clientId, CancellationToken cancellationToken = default, int timeoutMs = 150);

        /// <summary>
        /// Send a read query and forward its reply through the message
        /// dispatcher so RadioStateService (and the browser) learn the answer.
        /// Plain SendCommandAsync hands the reply to the awaiter and to nobody
        /// else -- use this for "read this property into app state".
        /// </summary>
        Task SendCommandAndDispatchAsync(string command, string clientId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Send a set command and don't wait for an answer: a set command
        /// gives none, so SendCommandAsync waits its whole timeout for nothing.
        /// Anything the radio does send back goes to the message dispatcher.
        /// </summary>
        Task SendWithoutReplyAsync(string command, string clientId, CancellationToken cancellationToken = default);

        // VFO-A (Main) Methods
        Task<long> ReadFrequencyAsync();
        Task<long> ReadFrequencyAAsync();
        Task<bool> SetFrequencyAAsync(long frequencyHz);
        Task<int> ReadSMeterAsync();
        Task<int> ReadSMeterMainAsync();
        Task<string> ReadModeAsync();
        Task<string> ReadModeMainAsync();
        Task<bool> SetModeMainAsync(string mode);
        Task<long> QueryFrequencyAAsync(string clientId, CancellationToken cancellationToken = default);

        // VFO-B (Sub) Methods
        Task<long> ReadFrequencyBAsync();
        Task<bool> SetFrequencyBAsync(long frequencyHz);
        Task<int> ReadSMeterSubAsync();
        Task<string> ReadModeSubAsync();
        Task<bool> SetModeSubAsync(string mode);
        Task<long> QueryFrequencyBAsync(string clientId, CancellationToken cancellationToken = default);

        // Common Methods
        Task<bool> ReadTransmitStatusAsync();
    }
}