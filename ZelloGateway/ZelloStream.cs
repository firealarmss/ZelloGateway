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
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using Serilog;

namespace ZelloGateway
{
    public class ZellStream : IDisposable
    {
        private const string ZelloServerUrl = "wss://zello.io/ws";
        private string Username = Program.Configuration.ZelloUsername;
        private string Password = Program.Configuration.ZelloPassword;
        private string Channel = Program.Configuration.ZelloChannel;

        private string LastKeyed = string.Empty;

        private ClientWebSocket _webSocket;
        private OpusDecoder _opusDecoder;
        private OpusEncoder _opusEncoder;
        private CancellationTokenSource _cancellationSource;

        private int _streamId;
        private int _sequenceCounter;

        private List<short> _accumulatedBuffer;

        public event Action<short[], string> OnPcmDataReceived;
        public event Action OnStreamEnd;

        public ZellStream()
        {
            _webSocket = new ClientWebSocket();
            _cancellationSource = new CancellationTokenSource();
            _opusDecoder = new OpusDecoder(16000, 1);
            _opusEncoder = new OpusEncoder(16000, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
            _sequenceCounter = 1;
            _accumulatedBuffer = new List<short>();
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                await _webSocket.ConnectAsync(new Uri(ZelloServerUrl), _cancellationSource.Token);
                Log.Logger.Information("Zello server connection sucessful");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to connect: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> AuthenticateAsync()
        {
            var logonJson = new
            {
                command = "logon",
                username = Username,
                password = Password,
                channel = Channel,
                auth_token = Program.Configuration.ZelloAuthToken,
            };
            return await SendJsonAsync(logonJson);
        }

        private async Task<bool> SendJsonAsync(object json)
        {
            // Log.Logger.Information("Sending JSON... " + _webSocket.State);
            try
            {
                var jsonBytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(json));
                await _webSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, true, _cancellationSource.Token);
                return true;
            }
            catch (Exception ex)
            {
                Log.Logger.Error($"Failed to send JSON data: {ex.Message}");
                return false;
            }
        }

        public async Task StartAudioStreamAsync()
        {
            Task.Run(ReceiveAudioAsync);
        }

        private async Task ReceiveAudioAsync()
        {
            var receiveBuffer = new byte[1024];
            int sampleRate = 16000;
            int outputSampleRate = 8000;
            int channelCount = 1;
            int zelloChunkSize = 960;

            try
            {
                while (_webSocket.State == WebSocketState.Open)
                {
                    var result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), _cancellationSource.Token);

                    if (result.MessageType == WebSocketMessageType.Binary && receiveBuffer[0] == 1)
                    {
                        int headerLength = 9;
                        byte[] opusData = new byte[result.Count - headerLength];
                        Array.Copy(receiveBuffer, headerLength, opusData, 0, opusData.Length);

                        try
                        {
                            short[] pcmBuffer = new short[zelloChunkSize];
                            int decodedSamples = _opusDecoder.Decode(opusData, 0, opusData.Length, pcmBuffer, 0, pcmBuffer.Length);

                            if (sampleRate != outputSampleRate)
                            {
                                short[] resampledBuffer = Resample(pcmBuffer, decodedSamples, sampleRate, outputSampleRate);
                                OnPcmDataReceived?.Invoke(resampledBuffer, LastKeyed);
                            }
                            else
                            {
                                OnPcmDataReceived?.Invoke(pcmBuffer, LastKeyed);
                            }
                        }
                        catch (OpusException ex)
                        {
                            Console.WriteLine($"Opus decoding error: {ex.Message}");
                        } catch (Exception ex)
                        {
                            Log.Logger.Error(ex.Message);
                        }
                    }
                    else if (result.MessageType == WebSocketMessageType.Text)
                    {
                        string jsonResponse = System.Text.Encoding.UTF8.GetString(receiveBuffer, 0, result.Count);
                        // Console.WriteLine("Received JSON message: " + jsonResponse);

                        try
                        {
                            var response = JsonSerializer.Deserialize<ZelloResponse>(jsonResponse);

                            if (response?.from != string.Empty)
                                LastKeyed = response.from;

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

            Log.Logger.Error("WEBSOCKET NOT OPEN");
        }

        public async Task SendAudioAsync(short[] pcmSamples)
        {
            int inputSampleRate = 8000;
            int targetSampleRate = 16000;
            int frameDurationMs = 60;
            int sampleCount = (int)(targetSampleRate * (frameDurationMs / 1000.0));

            if (inputSampleRate != targetSampleRate)
            {
                pcmSamples = Resample(pcmSamples, pcmSamples.Length, inputSampleRate, targetSampleRate);
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

        private short[] Resample(short[] input, int inputLength, int inputRate, int outputRate)
        {
            double resampleRatio = (double)outputRate / inputRate;
            int outputLength = (int)(inputLength * resampleRatio);
            short[] output = new short[outputLength];

            for (int i = 0; i < outputLength; i++)
            {
                double srcIndex = i / resampleRatio;
                int intIndex = (int)srcIndex;
                double frac = srcIndex - intIndex;

                if (intIndex + 1 < inputLength)
                {
                    output[i] = (short)((1 - frac) * input[intIndex] + frac * input[intIndex + 1]);
                }
                else
                {
                    output[i] = input[intIndex];
                }
            }

            return output;
        }

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
               // Log.Logger.Information("Started stream.");
            }
            return isSent;
        }

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

        public void Dispose()
        {
            _webSocket?.Dispose();
            _opusDecoder?.Dispose();
            _cancellationSource?.Cancel();
        }

        public class ZelloResponse
        {
            public string command { get; set; }
            public string from { get; set; }
            public int? stream_id { get; set; }
            public bool? success { get; set; }
            public int? seq { get; set; }
        }
    }
}
