using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text;

namespace Yaesu_Web_Control.Services
{
    /// <summary>
    /// Central CAT multiplexer that owns the serial port and services multiple clients
    /// </summary>
    public class CatMultiplexerService : IDisposable
    {
        private SerialPort? _serialPort;
        private readonly SemaphoreSlim _serialSemaphore = new(1, 1);
        private readonly ILogger<CatMultiplexerService> _logger;
        private readonly ConcurrentQueue<CatRequest> _commandQueue = new();
        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private Task? _processingTask;

        // Auto-Information support
        private readonly CatMessageBuffer _messageBuffer;
        private readonly CatMessageDispatcher _messageDispatcher;
        private bool _autoInformationEnabled = false;

        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pendingResponses = new();
        private TaskCompletionSource<bool>? _initializationCompletionSource;

        public bool IsConnected => _serialPort?.IsOpen ?? false;

        private readonly ISettingsService _settingsService;

        public CatMultiplexerService(
            ILogger<CatMultiplexerService> logger,
            CatMessageBuffer messageBuffer,
            CatMessageDispatcher messageDispatcher,
            ISettingsService settingsService)
        {
            _logger = logger;
            _messageBuffer = messageBuffer;
            _messageDispatcher = messageDispatcher;
            _settingsService = settingsService;

            _messageBuffer.MessageReceived += OnMessageReceived;
            _messageDispatcher.OnInitializationComplete = SignalInitializationComplete;
        }

        private void OnMessageReceived(object? sender, CatMessageReceivedEventArgs e)
        {
            var message = e.Message.Trim();

            // Suppress high-frequency RM meter poll responses from logs
            if (!message.StartsWith("RM"))
            {
                _logger.LogInformation("[CatMultiplexerService] OnMessageReceived: {Message}", message);
            }

            if (message.Length < 2) return;

            var prefix = message.Substring(0, 2);

            // DT messages always go to the dispatcher — never to _pendingResponses.
            // This is the only path that triggers SignalInitializationComplete.
            if (prefix == "DT")
            {
                _logger.LogWarning("[CatMultiplexerService] >>> Dispatching DT message: {Message}", message);
                _messageDispatcher.DispatchMessage(message);
                return; // Do not fall through to _pendingResponses
            }

            if (_pendingResponses.TryRemove(prefix, out var tcs))
            {
                tcs.TrySetResult(message.TrimEnd(';'));
            }
            else
            {
                _messageDispatcher.DispatchMessage(message);
            }
        }

        public async Task<bool> ConnectAsync(string portName, int baudRate = 38400)
        {
            if (!await _serialSemaphore.WaitAsync(5000))
            {
                _logger.LogError("ConnectAsync: timed out waiting for serial semaphore — possible deadlock");
                return false;
            }
            try
            {
                if (_serialPort?.IsOpen == true)
                {
                    _logger.LogInformation("Port {PortName} already connected", portName);
                    return true;
                }

                var availablePorts = SerialPort.GetPortNames();
                _logger.LogInformation("Available COM ports: {Ports}", string.Join(", ", availablePorts));

                if (!availablePorts.Contains(portName))
                {
                    _logger.LogError("Port {PortName} not found", portName);
                    return false;
                }

                _serialPort = new SerialPort
                {
                    PortName = portName,
                    BaudRate = baudRate,
                    DataBits = 8,
                    Parity = Parity.None,
                    StopBits = StopBits.Two,
                    Handshake = Handshake.None,
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    DtrEnable = true,
                    RtsEnable = true
                };

                _serialPort.Open();
                await Task.Delay(50);

                _logger.LogInformation("✓ Connected to {PortName} at {BaudRate} baud, 8-N-2", portName, baudRate);

                // Start processing queue
                _processingTask = Task.Run(() => ProcessCommandQueueAsync(_cancellationTokenSource.Token));

                _serialPort.DataReceived += (s, e) => {
                    var data = _serialPort.ReadExisting();
                    _messageBuffer.AppendData(data);
                };

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to {PortName}", portName);
                return false;
            }
            finally
            {
                _serialSemaphore.Release();
            }
        }

        /// <summary>
        /// Enable Auto Information mode - radio sends updates automatically
        /// </summary>
        public async Task EnableAutoInformationAsync()
        {
            await SendCommandAsync("AI1;", "System");
            await SendCommandAsync("DT0;", "System");
        }

        /// <summary>
        /// Disable Auto Information mode
        /// </summary>
        public async Task DisableAutoInformationAsync()
        {
            await SendCommandAsync("AI0;", "System");
        }

        /// <summary>
        /// Query all initial radio parameters to populate state
        /// </summary>
        private async Task QueryInitialStateAsync()
        {
            _logger.LogInformation("Querying initial radio state...");

            var commands = new[]
            {
                "FA;",    // VFO A frequency
                "FB;",    // VFO B frequency
                "MD0;",   // VFO A mode
                "MD1;",   // VFO B mode
                "SM0;",   // VFO A S-meter
                "SM1;",   // VFO B S-meter
                "PC;",    // Power
                "AN0;",   // VFO A antenna
                "AN1;",   // VFO B antenna
                "TX;",    // TX status
            };

            foreach (var cmd in commands)
            {
                try
                {
                    var response = await SendCommandAsync(cmd, "InitialQuery");
                    if (!string.IsNullOrEmpty(response))
                    {
                        _messageDispatcher.DispatchMessage(response + ";");
                    }
                    await Task.Delay(10);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to query {Command}", cmd);
                }
            }

            _logger.LogInformation("✓ Initial state queried");
        }

        // Read-query helper: send a command, await its response, AND forward the
        // response through CatMessageDispatcher so it updates RadioStateService.
        //
        // Plain SendCommandAsync resolves the response via _pendingResponses and
        // returns it to the awaiter without dispatching — fine for callers that
        // parse the response themselves, but wrong for "read this property into
        // app state" queries. Without this helper the readQueries loop in
        // RadioInitializationService silently failed for every property it
        // queried (PC, MG, PR, PL, GT0/1, PA0/1, ...). Reported by SP3L-Jacek
        // as #35: YWC's slider stayed at the persisted Power instead of
        // reflecting the radio's actual setting on startup.
        public async Task SendCommandAndDispatchAsync(string command, string clientId, CancellationToken cancellationToken = default)
        {
            var response = await SendCommandAsync(command, clientId, cancellationToken);
            if (!string.IsNullOrEmpty(response))
            {
                _messageDispatcher.DispatchMessage(response + ";");
            }
        }

        public async Task<string?> SendCommandAsync(string command, string clientId, CancellationToken cancellationToken = default, int timeoutMs = 150)
        {
            if (_serialPort?.IsOpen != true)
                return null;

            var prefix = command.Substring(0, 2);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingResponses[prefix] = tcs;
            await SendToSerialPortAsync(command, cancellationToken);

            var timeoutTask = Task.Delay(timeoutMs, cancellationToken);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            _pendingResponses.TryRemove(prefix, out _);

            if (completedTask == tcs.Task)
            {
                return await tcs.Task;
            }
            else
            {
                return null;
            }
        }

        private async Task ProcessCommandQueueAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Command queue processor started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_commandQueue.TryDequeue(out var request))
                    {
                        await ProcessSingleCommandAsync(request);
                    }
                    else
                    {
                        await Task.Delay(2, cancellationToken);
                    }
                }
                catch (TaskCanceledException tce)
                {
                    _logger.LogInformation("Command queue processor canceled: {Message}", tce.Message);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing command queue");
                    await Task.Delay(20, cancellationToken);
                }
            }

            _logger.LogInformation("Command queue processor stopped");
        }

        private async Task ProcessSingleCommandAsync(CatRequest request)
        {
            try
            {
                if (_serialPort?.IsOpen != true)
                {
                    _logger.LogError("[{ClientId}] Command #{RequestId} failed: Serial port not open", request.ClientId, request.RequestId);
                    request.CompletionSource.SetResult(string.Empty);
                    return;
                }

                // Clear stale data
                if (_serialPort.BytesToRead > 0)
                {
                    _serialPort.DiscardInBuffer();
                }

                var fullCommand = request.Command.EndsWith(";") ? request.Command : request.Command + ";";
                var commandBytes = Encoding.ASCII.GetBytes(fullCommand);

                _logger.LogDebug("[{ClientId}] >>> #{RequestId}: {Command}", request.ClientId, request.RequestId, fullCommand.TrimEnd(';'));

                _serialPort.Write(commandBytes, 0, commandBytes.Length);

                _logger.LogDebug("[{ClientId}] <<< #{RequestId}: (response will be handled asynchronously)", request.ClientId, request.RequestId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ClientId}] Error processing command #{RequestId}: '{Command}'",
                    request.ClientId, request.RequestId, request.Command);
                request.CompletionSource.SetException(ex);
            }
        }

        public async Task DisconnectAsync()
        {
            bool acquired = await _serialSemaphore.WaitAsync(5000);
            if (!acquired)
                _logger.LogWarning("DisconnectAsync: timed out waiting for serial semaphore — forcing disconnect without lock");
            try
            {
                _cancellationTokenSource.Cancel();

                if (_processingTask != null)
                {
                    await _processingTask;
                }

                if (_serialPort?.IsOpen == true)
                {
                    if (_autoInformationEnabled)
                    {
                        try
                        {
                            var cmdBytes = Encoding.ASCII.GetBytes("AI0;");
                            _serialPort.Write(cmdBytes, 0, cmdBytes.Length);
                            await Task.Delay(100);
                        }
                        catch { /* Ignore errors during disconnect */ }
                    }

                    _serialPort.DtrEnable = false;
                    _serialPort.RtsEnable = false;
                    _serialPort.DiscardInBuffer();
                    _serialPort.DiscardOutBuffer();
                    _serialPort.Close();
                    _logger.LogInformation("Disconnected from serial port");
                }

                _serialPort?.Dispose();
                _serialPort = null;
                _autoInformationEnabled = false;
            }
            finally
            {
                if (acquired) _serialSemaphore.Release();
            }
        }

        public void Dispose()
        {
            DisconnectAsync().GetAwaiter().GetResult();
            _serialSemaphore?.Dispose();
            _cancellationTokenSource?.Dispose();
        }

        private class CatRequest
        {
            public int RequestId { get; set; }
            public string ClientId { get; set; } = string.Empty;
            public string Command { get; set; } = string.Empty;
            public TaskCompletionSource<string> CompletionSource { get; set; } = null!;
            public DateTime Timestamp { get; set; }
        }

        public async Task SendCommand(string command, bool processResult = false, int delay = 0)
        {
            var response = await SendCommandAsync(command, "InitialValues");
            _logger.LogDebug("Command {Command} got response: {Response}", command, response);
            if (processResult && !string.IsNullOrEmpty(response))
            {
                _messageDispatcher.DispatchMessage(response + ";");
            }
            if (delay > 0)
                await Task.Delay(Math.Min(delay, 20));
        }

        public async Task SendCommandPause(string command, bool processResult = false)
        {
            await SendCommand(command, processResult, delay: 20);
        }

        public async Task GetInitialValues()
        {
            await SendCommand("AI1;", true);
            await SendCommand("ID;", true);
            await SendCommand("AG0;", true);
            await SendCommand("AG1;", true);
            await SendCommand("RG0;", true);
            await SendCommand("RG1;", true);
            await SendCommand("FA;", true);
            await SendCommand("FB;", true);
            await SendCommand("FR;", true);
            await SendCommand("FT;", true);
            await SendCommand("SS04;", true);
            await SendCommand("SS14;", true);
            await SendCommand("AO;", true);
            await SendCommand("MG;", true);
            await SendCommand("PL;", true);
            await SendCommand("PR0;", true);
            await SendCommand("PR1;", true);
            await SendCommand("MD0;", true);
            await SendCommand("MD1;", true);
            await SendCommand("VS;", true);
            await SendCommand("KP;", true);
            await SendCommand("PC;", true);
            await SendCommand("RL0;", true);
            await SendCommand("RL1;", true);
            await SendCommand("NR0;", true);
            await SendCommand("NR1;", true);
            await SendCommand("NB0;", true);
            await SendCommand("NB1;", true);
            await SendCommand("NL0;", true);
            await SendCommand("CO00;", true);
            await SendCommand("CO10;", true);
            await SendCommand("CO01;", true);
            await SendCommand("CO11;", true);
            await SendCommand("CO02;", true);
            await SendCommand("CO12;", true);
            await SendCommand("CO03;", true);
            await SendCommand("CO13;", true);
            await SendCommand("CN00;", true);
            await SendCommand("CN10;", true);
            await SendCommand("CT0;", true);
            await SendCommand("CT1;", true);
            await SendCommandPause("EX030203;", true);
            await SendCommandPause("EX030202;", true);
            await SendCommandPause("EX030102;", true);
            await SendCommandPause("EX030103;", true);
            await SendCommandPause("EX040105;", true);
            await SendCommandPause("EX030201;", true);
            await SendCommandPause("EX010111;", true);
            await SendCommandPause("EX010112;", true);
            await SendCommandPause("EX030405;", true);
            await SendCommandPause("EX010111;", true);
            await SendCommandPause("EX010211;", true);
            await SendCommandPause("EX010310;", true);
            await SendCommandPause("EX010413;", true);
            await SendCommandPause("EX010112;", true);
            await SendCommandPause("EX010213;", true);
            await SendCommandPause("EX010312;", true);
            await SendCommandPause("EX010414;", true);
            await SendCommandPause("EX0403021;", false);
            await SendCommand("SH0;", true);
            await SendCommand("SH1;", true);
            await SendCommand("IS0;", true);
            await SendCommand("SS06;", true);
            await SendCommand("IS1;", true);
            await SendCommand("AC;", true);
            await SendCommand("KP;", true);
            await SendCommand("FT;", true);
            await SendCommand("IF;", true);
            await SendCommand("BP00;", true);
            await SendCommand("BP01;", true);
            await SendCommand("BP10;", true);
            await SendCommand("BP11;", true);
            await SendCommand("GT0;", true);
            await SendCommand("GT1;", true);
            await SendCommand("AN0;", true);
            await SendCommand("AN1;", true);
            await SendCommand("PA0;", true);
            await SendCommand("PA1;", true);
            await SendCommand("RF0;", true);
            await SendCommand("RF1;", true);
            await SendCommand("ID;", true);
            await SendCommand("CS;", true);
            await SendCommand("ML0;", true);
            await SendCommand("ML1;", true);
            await SendCommand("BI;", true);
            await SendCommand("MS;", true);
            await SendCommand("KS;", true);
            await SendCommand("SS05;", true);
            await SendCommand("SS15;", true);
            await SendCommand("SS06;", true);
            await SendCommand("SS16;", true);
            await SendCommand("VT0;", true);
            await SendCommand("VX;", true);
            await SendCommand("VG;", true);
            await SendCommand("AV;", true);
            await SendCommand("CF000;", true);
            await SendCommand("CF100;", true);
            await SendCommand("CF001;", true);
            await SendCommand("CF101;", true);
            await SendCommand("BC0;", true);
            await SendCommand("BC1;", true);
            await SendCommand("KR;", true);
            await SendCommand("RA0;", true);
            await SendCommand("RA1;", true);
            await SendCommand("SY;", true);
            await SendCommandPause("VD;", true);
            await SendCommandPause("DT0;", true);
        }

        /// <summary>
        /// Send initialization commands quickly without waiting for individual responses.
        /// Responses are handled asynchronously via Auto Information mode.
        /// </summary>
        private async Task SendInitializationCommandsFastAsync(string[] commands)
        {
            if (_serialPort?.IsOpen != true)
                throw new InvalidOperationException("Serial port is not open.");

            const int batchSize = 10;
            const int interCommandDelayMs = 10;
            const int interBatchDelayMs = 50;

            for (int i = 0; i < commands.Length; i++)
            {
                var cmd = commands[i];
                var fullCommand = cmd.EndsWith(";") ? cmd : cmd + ";";
                var commandBytes = Encoding.ASCII.GetBytes(fullCommand);

                _serialPort.Write(commandBytes, 0, commandBytes.Length);

                await Task.Delay(interCommandDelayMs);

                if ((i + 1) % batchSize == 0)
                {
                    await Task.Delay(interBatchDelayMs);
                }
            }
        }

        public async Task InitializeRadioAsync()
        {
            _logger.LogWarning("[CatMultiplexerService] InitializeRadioAsync starting...");
            _initializationCompletionSource = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            _logger.LogWarning("[CatMultiplexerService] Sending AI1 command...");
            await SendCommandAsync("AI1;", "Initialization", CancellationToken.None);

            _logger.LogWarning("[CatMultiplexerService] Sending {Count} initialization commands (fast mode)...",
                CatCommands.InitializationCommands.Length);
            await SendInitializationCommandsFastAsync(CatCommands.InitializationCommands);

            // Settle time after the fast burst before sending DT0
            await Task.Delay(100);

            _logger.LogWarning("[CatMultiplexerService] Sending DT0 command (raw)...");

            // Send DT0 raw — do NOT use SendCommandAsync here.
            // SendCommandAsync would register "DT" in _pendingResponses, which competes
            // with OnMessageReceived dispatching to CatMessageDispatcher.
            // We only want the dispatcher path: DT0 response → HandleInitialization
            // → SignalInitializationComplete → _initializationCompletionSource.
            var dt0Bytes = Encoding.ASCII.GetBytes("DT0;");
            _serialPort!.Write(dt0Bytes, 0, dt0Bytes.Length);

            _logger.LogWarning("[CatMultiplexerService] Waiting for DT0 response...");

            var completionTask = _initializationCompletionSource.Task;
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
            var completedTask = await Task.WhenAny(completionTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _logger.LogError("[CatMultiplexerService] !!! TIMEOUT waiting for DT0 response after 5 seconds");
                throw new TimeoutException("Timeout waiting for DT0 response from radio");
            }

            _logger.LogWarning("[CatMultiplexerService] ✓ DT0 response received, initialization complete");
        }

        public async Task ShutdownRadioAsync()
        {
            // Implementation (if needed) or leave empty
        }

        public void SignalInitializationComplete()
        {
            _logger.LogWarning("[CatMultiplexerService] >>> SignalInitializationComplete called");
            if (_initializationCompletionSource != null)
            {
                _logger.LogWarning("[CatMultiplexerService] >>> Setting _initializationCompletionSource result to true");
                _initializationCompletionSource.TrySetResult(true);
            }
            else
            {
                _logger.LogWarning("[CatMultiplexerService] >>> _initializationCompletionSource is null, cannot set result");
            }
        }

        private async Task SendToSerialPortAsync(string command, CancellationToken cancellationToken)
        {
            if (_serialPort?.IsOpen != true)
                throw new InvalidOperationException("Serial port is not open.");

            var fullCommand = command.EndsWith(";") ? command : command + ";";
            var commandBytes = Encoding.ASCII.GetBytes(fullCommand);

            _serialPort.Write(commandBytes, 0, commandBytes.Length);
            await Task.Delay(15, cancellationToken);
        }
    }
}
