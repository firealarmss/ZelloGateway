// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Audio Bridge
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Audio Bridge
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2022-2024 Bryan Biedenkapp, N2PLL
*   Copyright (C) 2024 Caleb, K4PHP
*
*/
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Linq;

using Serilog;

using fnecore;
using fnecore.DMR;

using vocoder;

using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Data;
using System.Windows.Forms;

namespace ZelloGateway
{
    /// <summary>
    /// Represents the individual timeslot data status.
    /// </summary>
    public class SlotStatus
    {
        /// <summary>
        /// Rx Start Time
        /// </summary>
        public DateTime RxStart = DateTime.Now;

        /// <summary>
        /// 
        /// </summary>
        public uint RxSeq = 0;

        /// <summary>
        /// Rx RF Source
        /// </summary>
        public uint RxRFS = 0;
        /// <summary>
        /// Tx RF Source
        /// </summary>
        public uint TxRFS = 0;

        /// <summary>
        /// Rx Stream ID
        /// </summary>
        public uint RxStreamId = 0;
        /// <summary>
        /// Tx Stream ID
        /// </summary>
        public uint TxStreamId = 0;

        /// <summary>
        /// Rx TG ID
        /// </summary>
        public uint RxTGId = 0;
        /// <summary>
        /// Tx TG ID
        /// </summary>
        public uint TxTGId = 0;
        /// <summary>
        /// Tx Privacy TG ID
        /// </summary>
        public uint TxPITGId = 0;

        /// <summary>
        /// Rx Time
        /// </summary>
        public DateTime RxTime = DateTime.Now;
        /// <summary>
        /// Tx Time
        /// </summary>
        public DateTime TxTime = DateTime.Now;

        /// <summary>
        /// Rx Type
        /// </summary>
        public FrameType RxType = FrameType.TERMINATOR;

        /** DMR Data */
        /// <summary>
        /// Rx Link Control Header
        /// </summary>
        public LC DMR_RxLC = null;
        /// <summary>
        /// Rx Privacy Indicator Link Control Header
        /// </summary>
        public PrivacyLC DMR_RxPILC = null;
        /// <summary>
        /// Tx Link Control Header
        /// </summary>
        public LC DMR_TxHLC = null;
        /// <summary>
        /// Tx Privacy Link Control Header
        /// </summary>
        public PrivacyLC DMR_TxPILC = null;
        /// <summary>
        /// Tx Terminator Link Control
        /// </summary>
        public LC DMR_TxTLC = null;
    } // public class SlotStatus

    /// <summary>
    /// Implements a FNE system.
    /// </summary>
    public abstract partial class FneSystemBase : fnecore.FneSystemBase
    {
        private const string LOCAL_CALL = "Local Traffic";
        private const string UDP_CALL = "UDP Traffic";

        public abstract Task StartListeningAsync();

        private const int P25_FIXED_SLOT = 2;

        public const int SAMPLE_RATE = 8000;
        public const int BITS_PER_SECOND = 16;

        private const int MBE_SAMPLES_LENGTH = 160;

        private const int AUDIO_BUFFER_MS = 20;
        private const int AUDIO_NO_BUFFERS = 2;
        private const int AFSK_AUDIO_BUFFER_MS = 60;
        private const int AFSK_AUDIO_NO_BUFFERS = 4;

        private const int TX_MODE_DMR = 1;
        private const int TX_MODE_P25 = 2;

        private bool callInProgress = false;

        private SlotStatus[] status;

#if WIN32
        private AmbeVocoder extFullRateVocoder;
        private AmbeVocoder extHalfRateVocoder;
#endif

        private WaveFormat waveFormat;
        private BufferedWaveProvider waveProvider;

        private Task waveInRecorder;
        private WaveInEvent waveIn;

        private Stopwatch dropAudio;
        private int dropTimeMs;
        bool audioDetect;
        bool trafficFromUdp;

        private BufferedWaveProvider meterInternalBuffer;
        private SampleChannel sampleChannel;
        private MeteringSampleProvider meterProvider;

        private WaveOut waveOut;

        private Random rand;
        private uint txStreamId;

        private uint srcIdOverride = 0;
        private uint udpSrcId = 0;
        private uint udpDstId = 0;

        private UdpClient udpClient;

        protected ZellStream ZelloStream { get; private set; }
        protected ZelloAliasLookup ZelloAliasLookup { get; private set; }


        /*
        ** Methods
        */

        /// <summary>
        /// Initializes a new instance of the <see cref="FneSystemBase"/> class.
        /// </summary>
        /// <param name="fne">Instance of <see cref="FneMaster"/> or <see cref="FnePeer"/></param>
        public FneSystemBase(FnePeer fne, ZellStream zelloStream) : base(fne, Program.FneLogLevel)
        {
            this.fne = fne;
            this.ZelloStream = zelloStream;

            if (!string.IsNullOrEmpty(Program.Configuration.ZelloAliasFile))
                this.ZelloAliasLookup = new ZelloAliasLookup(Program.Configuration.ZelloAliasFile);

            this.rand = new Random(Guid.NewGuid().GetHashCode());

            // initialize slot statuses
            this.status = new SlotStatus[3];
            this.status[0] = new SlotStatus();  // DMR Slot 1
            this.status[1] = new SlotStatus();  // DMR Slot 2
            this.status[2] = new SlotStatus();  // P25

            // hook logger callback
            this.fne.Logger = (LogLevel level, string message) =>
            {
                switch (level)
                {
                    case LogLevel.WARNING:
                        Log.Logger.Warning(message);
                        break;
                    case LogLevel.ERROR:
                        Log.Logger.Error(message);
                        break;
                    case LogLevel.DEBUG:
                        Log.Logger.Debug(message);
                        break;
                    case LogLevel.FATAL:
                        Log.Logger.Fatal(message);
                        break;
                    case LogLevel.INFO:
                    default:
                        Log.Logger.Information(message);
                        break;
                }
            };

            this.udpClient = new UdpClient();

            this.dropAudio = new Stopwatch();
            this.dropTimeMs = Program.Configuration.DropTimeMs;

            this.audioDetect = false;
            this.trafficFromUdp = false;

            this.waveFormat = new WaveFormat(SAMPLE_RATE, BITS_PER_SECOND, 1);

            this.meterInternalBuffer = new BufferedWaveProvider(waveFormat);
            this.meterInternalBuffer.DiscardOnBufferOverflow = true;

            this.sampleChannel = new SampleChannel(meterInternalBuffer);
            this.meterProvider = new MeteringSampleProvider(sampleChannel);

            // initialize DMR vocoders
            dmrDecoder = new MBEDecoderManaged(MBEMode.DMRAMBE);
            dmrDecoder.GainAdjust = Program.Configuration.VocoderDecoderAudioGain;
            dmrDecoder.AutoGain = Program.Configuration.VocoderDecoderAutoGain;
            dmrEncoder = new MBEEncoderManaged(MBEMode.DMRAMBE);
            dmrEncoder.GainAdjust = Program.Configuration.VocoderEncoderAudioGain;

            // initialize P25 vocoders
            p25Decoder = new MBEDecoderManaged(MBEMode.IMBE);
            p25Decoder.GainAdjust = Program.Configuration.VocoderDecoderAudioGain;
            p25Decoder.AutoGain = Program.Configuration.VocoderDecoderAutoGain;
            p25Encoder = new MBEEncoderManaged(MBEMode.IMBE);
            p25Encoder.GainAdjust = Program.Configuration.VocoderEncoderAudioGain;
#if WIN32
            // initialize external AMBE vocoder
            string codeBase = Assembly.GetExecutingAssembly().CodeBase;
            UriBuilder uri = new UriBuilder(codeBase);
            string path = Uri.UnescapeDataString(uri.Path);

            // if the assembly executing directory contains the external DVSI USB-3000 interface DLL
            // setup the external vocoder code
            if (File.Exists(Path.Combine(new string[] { Path.GetDirectoryName(path), "AMBE.DLL" })))
            {
                extFullRateVocoder = new AmbeVocoder();
                extHalfRateVocoder = new AmbeVocoder(false);
                Log.Logger.Information($"({SystemName}) Using external USB vocoder.");
            }
#endif
            embeddedData = new EmbeddedData();
            ambeBuffer = new byte[27];

            netLDU1 = new byte[9 * 25];
            netLDU2 = new byte[9 * 25];
        }

        public void ProcessAudioData(short[] pcmShortData, string lastHeard)
        {
            int encoderChunkSize = 160;
            int pcmChunkCount = pcmShortData.Length / encoderChunkSize;

            if (!string.IsNullOrEmpty(Program.Configuration.ZelloAliasFile))
                srcIdOverride = ZelloAliasLookup.GetRidByAlias(lastHeard);

            for (int i = 0; i < pcmChunkCount; i++)
            {
                short[] pcmChunk = new short[encoderChunkSize];
                Array.Copy(pcmShortData, i * encoderChunkSize, pcmChunk, 0, encoderChunkSize);

                byte[] pcmBytes = new byte[pcmChunk.Length * sizeof(short)];
                Buffer.BlockCopy(pcmChunk, 0, pcmBytes, 0, pcmBytes.Length);

                meterInternalBuffer.AddSamples(pcmBytes, 0, pcmBytes.Length);

                float[] temp = new float[meterInternalBuffer.BufferedBytes];
                meterProvider.Read(temp, 0, temp.Length);
                trafficFromUdp = true;

                if (!audioDetect && !callInProgress)
                {
                    StartCall();
                }

                EncodeAndTransmit(pcmBytes);
            }
        }

        /// <summary>
        /// Encodes and transmits the given PCM data chunk based on the selected mode.
        /// </summary>
        private void EncodeAndTransmit(byte[] pcmChunk)
        {
            switch (Program.Configuration.TxMode)
            {
                case TX_MODE_DMR:
                    DMREncodeAudioFrame(pcmChunk, srcIdOverride);
                    break;
                case TX_MODE_P25:
                    P25EncodeAudioFrame(pcmChunk, srcIdOverride);
                    break;
            }
        }

        /// <summary>
        /// Starts a call if not already in progress.
        /// </summary>
        private void StartCall()
        {
            audioDetect = true;
            txStreamId = (uint)rand.Next(int.MinValue, int.MaxValue);
            Log.Logger.Information($"({SystemName}) ZELLO *CALL START* PEER {fne.PeerId} SRC_ID {srcIdOverride} TGID {udpDstId} [STREAM ID {txStreamId}]");

            if (Program.Configuration.GrantDemand)
            {
                if (Program.Configuration.TxMode == TX_MODE_P25)
                    SendP25TDU(true);
            }

            dropAudio.Reset();
            dropTimeMs = Program.Configuration.DropTimeMs * 2;

            if (!dropAudio.IsRunning)
                dropAudio.Start();
        }


        /// <summary>
        /// Stops the main execution loop for this <see cref="FneSystemBase"/>.
        /// </summary>
        public override void Stop()
        {
            if (udpClient != null)
                udpClient.Dispose();

            ShutdownAudio();

            base.Stop();
        }

        /// <summary>
        /// Shuts down the audio resources.
        /// </summary>
        private void ShutdownAudio()
        {
            if (this.waveOut != null)
            {
                if (waveOut.PlaybackState == PlaybackState.Playing)
                    waveOut.Stop();
                waveOut.Dispose();
                waveOut = null;
            }

            if (waveInRecorder != null)
            {

                if (this.waveIn != null)
                {
                    waveIn.StopRecording();
                    waveIn.Dispose();
                    waveIn = null;
                }

                try
                {
                    waveInRecorder.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { /* stub */ }
            }
        }

        /// <summary>
        /// Callback used to process whether or not a peer is being ignored for traffic.
        /// </summary>
        /// <param name="peerId">Peer ID</param>
        /// <param name="srcId">Source Address</param>
        /// <param name="dstId">Destination Address</param>
        /// <param name="slot">Slot Number</param>
        /// <param name="callType">Call Type (Group or Private)</param>
        /// <param name="frameType">Frame Type</param>
        /// <param name="dataType">DMR Data Type</param>
        /// <param name="streamId">Stream ID</param>
        /// <returns>True, if peer is ignored, otherwise false.</returns>
        protected override bool PeerIgnored(uint peerId, uint srcId, uint dstId, byte slot, CallType callType, FrameType frameType, DMRDataType dataType, uint streamId)
        {
            return false;
        }

        /// <summary>
        /// Event handler used to handle a peer connected event.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected override void PeerConnected(object sender, PeerConnectedEvent e)
        {
            return;
        }
    } // public abstract partial class FneSystemBase : fnecore.FneSystemBase
} // namespace dvmbridge