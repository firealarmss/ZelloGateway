// SPDX-License-Identifier: AGPL-3.0-only
/**
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2024 Caleb, K4PHP
*
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using Serilog;
using YamlDotNet.Core.Tokens;

namespace ZelloGateway
{
    /// <summary>
    /// Zello interfacing class
    /// </summary>
    public class ZelloStream : IDisposable
    {
        private string ZelloServerUrl = Program.Configuration.ZelloUrl;
        private string Username = Program.Configuration.ZelloUsername;
        private string Password = Program.Configuration.ZelloPassword;
        private string Channel = Program.Configuration.ZelloChannel;
        private string zelloToken;

        private string LastKeyed = string.Empty;

        private int _streamId;
        private int _sequenceCounter;

        bool stopReconnect = false;
        bool authenticated = false;
        bool refreshed = false;

        private ClientWebSocket _webSocket;
        private OpusDecoder _opusDecoder;
        private OpusEncoder _opusEncoder;
        private CancellationTokenSource _cancellationSource;
        private KeepAlive _keepAlive;

        private List<short> _accumulatedBuffer;
        private List<short> _playbackBuffer = new List<short>();

        public event Action<short[], string> OnPcmDataReceived;
        public event Action<string, uint, uint> OnRadioCommand;
        public event Action OnStreamEnd;

        private Dictionary<int, CodecAttributes> _codecHeaders = new Dictionary<int, CodecAttributes>();

        /// <summary>
        /// Creates an instance of <see cref="ZelloStream"/>
        /// </summary>
        /// <param name="zelloToken"></param>
        public ZelloStream()
        {
            _webSocket = new ClientWebSocket();
            _cancellationSource = new CancellationTokenSource();
            _opusDecoder = new OpusDecoder(16000, 1);
            _opusEncoder = new OpusEncoder(16000, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
            _sequenceCounter = 1;
            _accumulatedBuffer = new List<short>();
            _keepAlive = new KeepAlive(Program.Configuration.ZelloPingInterval);

            _keepAlive.Ping += SendPing;
        }

        /// <summary>
        /// Connect to the Zello websocket server
        /// </summary>
        /// <returns></returns>
        public async Task<bool> ConnectAsync()
        {
            try
            {
                await _webSocket.ConnectAsync(new Uri(ZelloServerUrl), _cancellationSource.Token);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to connect: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Helper to tear down and reconnect and re authenticate to the Zello server
        /// </summary>
        /// <param name="maxRetries"></param>
        /// <param name="delayMilliseconds"></param>
        /// <returns></returns>
        public async Task<bool> ReconnectAsync(int maxRetries = 3, int delayMilliseconds = 5000)
        {
            if (_webSocket == null || stopReconnect)
                return false;

            if (_webSocket.State != WebSocketState.Open && _webSocket.State != WebSocketState.Connecting)
            {
                _webSocket.Dispose();
                _webSocket = new ClientWebSocket();
            }

            int attempt = 0;

            while (attempt < maxRetries && !stopReconnect)
            {
                attempt++;
                Log.Logger.Information($"Attempting to reconnect, attempt {attempt}/{maxRetries}...");

                try
                {
                    if (await ConnectAsync())
                    {
                        Log.Logger.Information("Reconnected successfully to the Zello server.");

                        if (await AuthenticateAsync())
                        {
                            if (authenticated)
                            {
                                Log.Logger.Information("Re-authenticated successfully after reconnection.");
                                return true;
                            } else
                            {
                                Log.Logger.Warning("Reconnection successful, but authentication failed.");
                            }
                        }
                        else
                        {
                            Log.Logger.Warning("Reconnection successful, but authentication failed.");
                        }
                    }
                    else
                    {
                        Log.Logger.Warning("Failed to reconnect on attempt " + attempt);
                    }
                }
                catch (Exception ex)
                {
                    Log.Logger.Error($"Error during reconnect attempt {attempt}: {ex.Message}");
                }

                if (attempt < maxRetries)
                {
                    Log.Logger.Information($"Waiting {delayMilliseconds / 1000} seconds before next attempt...");
                    await Task.Delay(delayMilliseconds);
                }
            }

            Log.Logger.Error("Max reconnection attempts reached. Stopping reconnection attempts.");
            stopReconnect = true;
            return false;
        }

        /// <summary>
        /// Sends JSON message to Zello websocket
        /// </summary>
        /// <param name="json"></param>
        /// <returns></returns>
        private async Task<bool> SendJsonAsync(object json)
        {
            // Log.Logger.Information("Sending JSON... " + _webSocket.State);

            if (_webSocket == null)
            {
                Log.Logger.Error("websocket null when trying to send??");
                return false;
            }

            try
            {
                byte[] jsonBytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(json));
                await _webSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, true, _cancellationSource.Token);
                return true;
            }
            catch (Exception ex)
            {
                Log.Logger.Error($"Failed to send JSON data: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Authenticate to the Zello websocket
        /// </summary>
        /// <returns></returns>
        public async Task<bool> AuthenticateAsync()
        {
            string token = string.Empty;
            string refreshToken = string.Empty;

            if (!refreshed && !authenticated)
            {

                if (Program.Configuration.ZelloAuthToken != null)
                {
                    token = Program.Configuration.ZelloAuthToken;
                    Log.Logger.Warning("Zello developer token used!");
                }
                else
                {
                    this.zelloToken = GetToken();
                    token = zelloToken;
                }
            } else
            {
                token = null;
                refreshToken = this.zelloToken;
            }

            var logonJson = new
            {
                command = "logon",
                username = Username,
                password = Password,
                channel = Channel,
                auth_token = token,
                refresh_token = refreshToken
            };
            return await SendJsonAsync(logonJson);
        }

        /// <summary>
        /// Start audio stream
        /// </summary>
        /// <returns></returns>
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        public async Task StartAudioStreamAsync()
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            Task.Run(ReceiveAudioAsync);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        }

        /// <summary>
        /// Main receive loop
        /// </summary>
        /// <returns></returns>
        private async Task ReceiveAudioAsync()
        {
            byte[] receiveBuffer = new byte[1024];
            int defaultOutputSampleRate = 8000;

            try
            {
                while (_webSocket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), _cancellationSource.Token);
                    if (result.MessageType == WebSocketMessageType.Binary && receiveBuffer[0] == 1)
                    {
                        int headerLength = 9;
                        byte[] opusData = new byte[result.Count - headerLength];
                        Array.Copy(receiveBuffer, headerLength, opusData, 0, opusData.Length);

                        try
                        {
                            if (!_codecHeaders.TryGetValue(_streamId, out CodecAttributes codecAttributes))
                            {
                                Log.Logger.Warning("No codec header found, defaulting to standard 16kHz Opus decoding.");
                                codecAttributes = new CodecAttributes
                                {
                                    SampleRateHz = 16000,
                                    FramesPerPacket = 1,
                                    FrameSizeMs = 60
                                };
                            }

                            // If someone can figure out what zello settings actually cause the codec information to change, please inform.
                            // Only one case of this being changed from "default" was with Nathaniel.

                            int zelloChunkSize = codecAttributes.SampleRateHz * codecAttributes.FrameSizeMs / 1000 * codecAttributes.FramesPerPacket;

                            if (_opusDecoder.SampleRate != codecAttributes.SampleRateHz)
                            {
                                Log.Logger.Information("Updated OPUS decoder sample rate changed");
                                _opusDecoder = new OpusDecoder(codecAttributes.SampleRateHz, 1);
                            }

                            short[] pcmBuffer = new short[zelloChunkSize];
                            int decodedSamples = _opusDecoder.Decode(opusData, 0, opusData.Length, pcmBuffer, 0, pcmBuffer.Length);

                            short[] outputBuffer;
                            if (codecAttributes.SampleRateHz != defaultOutputSampleRate)
                            {
                                outputBuffer = Utils.Resample(pcmBuffer, decodedSamples, codecAttributes.SampleRateHz, defaultOutputSampleRate);
                            }
                            else
                            {
                                outputBuffer = pcmBuffer;
                            }

                            _playbackBuffer.AddRange(outputBuffer);

                            int playbackThreshold = defaultOutputSampleRate * codecAttributes.FrameSizeMs / 1000;
                            if (_playbackBuffer.Count >= playbackThreshold)
                            {
                                OnPcmDataReceived?.Invoke(_playbackBuffer.ToArray(), LastKeyed);
                                _playbackBuffer.Clear();
                            }
                        }
                        catch (OpusException ex)
                        {
                            Console.WriteLine($"Opus decoding error: {ex.Message}");
                        }
                        catch (Exception ex)
                        {
                            Log.Logger.Error(ex.Message);
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string jsonResponse = System.Text.Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);
                        Log.Logger.Debug("Received Zello JSON: " + jsonResponse);

                        try
                        {
                            ZelloResponse response = JsonSerializer.Deserialize<ZelloResponse>(jsonResponse);

                            if (response?.from != string.Empty)
                                LastKeyed = response.from;

                            if (response?.codec_header != null)
                            {
                                CodecAttributes codecAttributes = Utils.DecodeCodecHeader(response.codec_header);
                                _codecHeaders[response.stream_id.Value] = codecAttributes;
                            }

                            if (response?.command == "on_alert")
                            {
                                if (response.text.Substring(0, 4) == "page")
                                {
                                    uint dstId = 0;
                                    uint srcId = 0;

                                    try
                                    {
                                        dstId = UInt32.Parse(response.text.Substring(5));
                                        srcId = (uint)Program.Configuration.SourceId;
                                    }
                                    catch (Exception ex)
                                    {
                                        Log.Logger.Error(ex.Message);
                                        return;
                                    }

                                    OnRadioCommand?.Invoke(response.text.Substring(0, 4), srcId, dstId);
                                }
                            }

                            if (response?.command == "on_channel_status")
                            {
                                authenticated = true;
                            }

                            if (response?.refresh_token != null)
                            {
                                refreshed = true;
                                this.zelloToken = response.refresh_token;
                            }

                            if (response?.command == "on_stream_stop" && response.stream_id.HasValue)
                            {
                                //Console.WriteLine("Stream stopped with stream_id: " + response.stream_id);
                                OnStreamEnd?.Invoke();
                            }
                            else if (response.stream_id.HasValue)
                            {
                                _streamId = response.stream_id.Value;
                                // Console.WriteLine("Set stream id to " + _streamId);
                            }
                        }
                        catch (JsonException ex)
                        {
                            Console.WriteLine($"Failed to parse JSON response: {ex.Message}");
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Console.WriteLine("WebSocket closed by server.");
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by server", CancellationToken.None);
                        break;
                    }
                }
            }
            catch (WebSocketException wsEx)
            {
                Console.WriteLine($"WebSocket error: {wsEx.Message}");
            }

            Log.Logger.Warning("WebSocket connection reconnecting....");

            authenticated = false;
            if (await ReconnectAsync())
            {
                Log.Logger.Information("Zello reconnected");
                stopReconnect = false;
            }
        }

        /// <summary>
        /// Helper to send PCM audio to Zello
        /// </summary>
        /// <param name="pcmSamples"></param>
        /// <returns></returns>
        public async Task SendAudioAsync(short[] pcmSamples)
        {
            int inputSampleRate = 8000;
            int targetSampleRate = 16000;
            int frameDurationMs = 60;
            int sampleCount = (int)(targetSampleRate * (frameDurationMs / 1000.0));

            if (inputSampleRate != targetSampleRate)
            {
                pcmSamples = Utils.Resample(pcmSamples, pcmSamples.Length, inputSampleRate, targetSampleRate);
            }

            _accumulatedBuffer.AddRange(pcmSamples);

            while (_accumulatedBuffer.Count >= sampleCount)
            {
                short[] frameToEncode = _accumulatedBuffer.Take(sampleCount).ToArray();
                _accumulatedBuffer.RemoveRange(0, sampleCount);

                byte[] opusBuffer = new byte[1275];
                int encodedBytes = _opusEncoder.Encode(frameToEncode, 0, sampleCount, opusBuffer, 0, opusBuffer.Length);

                if (encodedBytes > 0)
                {
                    byte[] sendData = new byte[9 + encodedBytes];
                    sendData[0] = 1;

                    byte[] streamIdBytes = BitConverter.GetBytes(_streamId);
                    if (BitConverter.IsLittleEndian) Array.Reverse(streamIdBytes);
                    Buffer.BlockCopy(streamIdBytes, 0, sendData, 1, 4);

                    Buffer.BlockCopy(opusBuffer, 0, sendData, 9, encodedBytes);

                    await _webSocket.SendAsync(new ArraySegment<byte>(sendData, 0, sendData.Length), WebSocketMessageType.Binary, true, _cancellationSource.Token);
                }
            }
        }

        /// <summary>
        /// Send Zello stream start
        /// </summary>
        /// <returns></returns>
        public async Task<bool> StartStreamAsync()
        {
            // Log.Logger.Information("Starting stream... " + _webSocket.State);

            var startStreamJson = new
            {
                command = "start_stream",
                channel = Channel,
                seq = _sequenceCounter++,
                type = "audio",
                codec = "opus",
                codec_header = Convert.ToBase64String(new byte[] { 0x80, 0x3E, 0x01, 0x3C }),
                packet_duration = 60
            };

            bool isSent = await SendJsonAsync(startStreamJson);
            if (isSent)
            {
                Log.Logger.Information($"Zello call start; streamId: {_streamId}");
            }
            return isSent;
        }

        public void SendPing()
        {
            var startStreamJson = new
            {
                command = "send_text_message",
                channel = Channel,
                text = "ping",
                @for = Username,
                seq = _sequenceCounter++,
            };

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            SendJsonAsync(startStreamJson);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            Log.Logger.Information($"Zello Ping sent SEQ {_sequenceCounter} PING {_keepAlive.PingCount}");
        }

        /// <summary>
        /// Send Zello stop stream command
        /// </summary>
        /// <returns></returns>
        public async Task<bool> StopStreamAsync()
        {
            var stopStreamJson = new { command = "stop_stream", seq = _sequenceCounter++, stream_id = _streamId };
            bool isSent = await SendJsonAsync(stopStreamJson);
            if (isSent)
            {
                Log.Logger.Information($"Zello call end; streamId: {_streamId}");
            }
            return isSent;
        }

        /// <summary>
        /// Helper to generate a new zello token
        /// </summary>
        /// <returns></returns>
        /// <exception cref="FileNotFoundException"></exception>
        public string GetToken()
        {
            if (Program.Configuration.ZelloPemFilePath != null)
            {
                if (!File.Exists(Program.Configuration.ZelloPemFilePath))
                {
                    throw new FileNotFoundException("PEM file not found", Program.Configuration.ZelloPemFilePath);
                }
                else
                {
                    return ZelloToken.CreateJwt(Program.Configuration.ZelloIssuer, File.ReadAllText(Program.Configuration.ZelloPemFilePath));
                }
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Cleanup
        /// </summary>
        public void Dispose()
        {
            _webSocket?.Dispose();
            _opusDecoder?.Dispose();
            _cancellationSource?.Cancel();
        }
    }
}
