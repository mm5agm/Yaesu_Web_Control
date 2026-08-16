using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// ICatClient implementation that uses the multiplexer instead of direct serial access
    /// </summary>
    public class MultiplexedCatClient : ICatClient
    {
        private readonly CatMultiplexerService _multiplexer;
        private readonly ILogger<MultiplexedCatClient> _logger;
        private const string DefaultClientId = "WebUI";

        public bool IsConnected => _multiplexer.IsConnected;

        public MultiplexedCatClient(CatMultiplexerService multiplexer, ILogger<MultiplexedCatClient> logger)
        {
            _multiplexer = multiplexer;
            _logger = logger;
        }

        public Task<bool> ConnectAsync(string portName, int baudRate = 38400)
        {
            // Connection is managed by multiplexer
            return Task.FromResult(_multiplexer.IsConnected);
        }

        public Task DisconnectAsync()
        {
            // Don't disconnect the multiplexer from individual client
            return Task.CompletedTask;
        }

        public void Dispose() { }

        public async Task<string> SendCommandAsync(string command, string clientId, CancellationToken cancellationToken = default, int timeoutMs = 150)
        {
            // Debug, not Warning: this fires for every CAT command the app sends
            // (hundreds per minute once the scope is polling). At Warning it made
            // routine traffic indistinguishable from real problems in a user's log.
            _logger.LogDebug("[sent] {Command}", command.Trim());
            var result = await _multiplexer.SendCommandAsync(command, clientId, cancellationToken, timeoutMs);
            return result ?? string.Empty; // Guarantees non-null return
        }

        // Overload for legacy code (optional)
        public Task<string> SendCommandAsync(string command, CancellationToken cancellationToken = default)
            => SendCommandAsync(command, DefaultClientId, cancellationToken);

        public async Task<long> ReadFrequencyAsync() => await ReadFrequencyAAsync();

        public async Task<long> ReadFrequencyAAsync()
        {
            var response = await SendCommandAsync(CatCommands.FrequencyVfoA, DefaultClientId, CancellationToken.None);
            return CatCommands.ParseFrequency(response);
        }

        public async Task<long> ReadFrequencyBAsync()
        {
            var response = await SendCommandAsync(CatCommands.FrequencyVfoB, DefaultClientId, CancellationToken.None);
            return CatCommands.ParseFrequency(response);
        }

        public async Task<bool> SetFrequencyAAsync(long frequencyHz)
        {
            var command = CatCommands.FormatFrequencyA(frequencyHz);
            await SendCommandAsync(command, DefaultClientId, CancellationToken.None);
            return true;
        }

        public async Task<bool> SetFrequencyBAsync(long frequencyHz)
        {
            var command = CatCommands.FormatFrequencyB(frequencyHz);
            await SendCommandAsync(command, DefaultClientId, CancellationToken.None);
            return true;
        }

        public async Task<int> ReadSMeterAsync() => await ReadSMeterMainAsync();

        public async Task<int> ReadSMeterMainAsync()
        {
            var response = await SendCommandAsync(CatCommands.SMeterMain, DefaultClientId, CancellationToken.None);
            return CatCommands.ParseSMeter(response);
        }

        public async Task<int> ReadSMeterSubAsync()
        {
            var response = await SendCommandAsync(CatCommands.SMeterSub, DefaultClientId, CancellationToken.None);
            return CatCommands.ParseSMeter(response);
        }

        public async Task<string> ReadModeAsync() => await ReadModeMainAsync();

        public async Task<string> ReadModeMainAsync()
        {
            var response = await SendCommandAsync(CatCommands.ModeMain, DefaultClientId, CancellationToken.None);
            return CatCommands.ParseMode(response);
        }

        public async Task<string> ReadModeSubAsync()
        {
            var response = await SendCommandAsync(CatCommands.ModeSub, DefaultClientId, CancellationToken.None);
            return CatCommands.ParseMode(response);
        }

        public async Task<bool> SetModeMainAsync(string mode)
        {
            var command = CatCommands.FormatMode(mode, false);
            await SendCommandAsync(command, DefaultClientId, CancellationToken.None);
            return true;
        }

        public async Task<bool> SetModeSubAsync(string mode)
        {
            var command = CatCommands.FormatMode(mode, true);
            await SendCommandAsync(command, DefaultClientId, CancellationToken.None);
            return true;
        }

        public Task<bool> ReadTransmitStatusAsync()
        {
            // Implement as needed
            return Task.FromResult(false);
        }

        public async Task<long> QueryFrequencyAAsync(string clientId, CancellationToken cancellationToken = default)
        {
            var response = await SendCommandAsync("FA;", clientId, cancellationToken);
            // Response format: "FA00014074000;" (example for 14.074 MHz)
            if (!string.IsNullOrEmpty(response) && response.StartsWith("FA"))
            {
                var freqStr = response.Substring(2, 9); // 9 digits after "FA"
                if (long.TryParse(freqStr, out var freq))
                    return freq;
            }
            return 0;
        }

        public async Task<long> QueryFrequencyBAsync(string clientId, CancellationToken cancellationToken = default)
        {
            var response = await SendCommandAsync("FB;", clientId, cancellationToken);
            // Response format: "FB00024915000;" (example for 24.915 MHz)
            if (!string.IsNullOrEmpty(response) && response.StartsWith("FB"))
            {
                var freqStr = response.Substring(2, 9); // 9 digits after "FB"
                if (long.TryParse(freqStr, out var freq))
                    return freq;
            }
            return 0;
        }
    }
}