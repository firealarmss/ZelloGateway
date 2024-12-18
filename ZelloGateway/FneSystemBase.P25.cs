﻿// SPDX - License - Identifier: AGPL - 3.0 - only
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
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

using Serilog;

using fnecore;
using fnecore.P25;

using NAudio.Wave;

using vocoder;
using System.Windows.Forms;
using System.Linq;
using fnecore.P25.LC.TSBK;
using Org.BouncyCastle.Asn1.Ocsp;

namespace ZelloGateway
{
    /// <summary>
    /// Implements a FNE system base.
    /// </summary>
    public abstract partial class FneSystemBase : fnecore.FneSystemBase
    {
        private const int IMBE_BUF_LEN = 11;

        private MBEDecoderManaged p25Decoder;
        private MBEEncoderManaged p25Encoder;

        private byte[] netLDU1;
        private byte[] netLDU2;
        private uint p25SeqNo = 0;
        private byte p25N = 0;

        private bool ignoreCall = false;
        private byte callAlgoId = P25Defines.P25_ALGO_UNENCRYPT;

        private List<short> pcmAccumulator = new List<short>();

        /*
        ** Methods
        */

        /// <summary>
        /// Callback used to validate incoming P25 data.
        /// </summary>
        /// <param name="peerId">Peer ID</param>
        /// <param name="srcId">Source Address</param>
        /// <param name="dstId">Destination Address</param>
        /// <param name="callType">Call Type (Group or Private)</param>
        /// <param name="duid">P25 DUID</param>
        /// <param name="frameType">Frame Type</param>
        /// <param name="streamId">Stream ID</param>
        /// <param name="message">Raw message data</param>
        /// <returns>True, if data stream is valid, otherwise false.</returns>
        protected override bool P25DataValidate(uint peerId, uint srcId, uint dstId, CallType callType, P25DUID duid, FrameType frameType, uint streamId, byte[] message)
        {
            return true;
        }

        /// <summary>
        /// Event handler used to pre-process incoming P25 data.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected override void P25DataPreprocess(object sender, P25DataReceivedEvent e)
        {
            return;
        }

        /// <summary>
        /// Helper to send a P25 TDU message.
        /// </summary>
        /// <param name="grantDemand"></param>
        private void SendP25TDU(bool grantDemand = false)
        {
            uint srcId = (uint)Program.Configuration.SourceId;
            if (srcIdOverride != 0 && Program.Configuration.OverrideSourceIdFromUDP)
                srcId = srcIdOverride;
            uint dstId = (uint)Program.Configuration.DestinationId;

            RemoteCallData callData = new RemoteCallData()
            {
                SrcId = srcId,
                DstId = dstId,
                LCO = P25Defines.LC_GROUP
            };

            SendP25TDU(callData, grantDemand);

            p25SeqNo = 0;
            p25N = 0;
        }

        /// <summary>
        /// Encode a logical link data unit 1.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="offset"></param>
        /// <param name="imbe"></param>
        /// <param name="frameType"></param>
        /// <param name="srcIdOverride"></param>
        private void EncodeLDU1(ref byte[] data, int offset, byte[] imbe, byte frameType, uint srcIdOverride = 0)
        {
            if (data == null)
                throw new ArgumentNullException("data");
            if (imbe == null)
                throw new ArgumentNullException("imbe");

            // determine the LDU1 DFSI frame length, its variable
            uint frameLength = P25DFSI.P25_DFSI_LDU1_VOICE1_FRAME_LENGTH_BYTES;
            switch (frameType)
            {
                case P25DFSI.P25_DFSI_LDU1_VOICE1:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE1_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE2:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE2_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE3:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE3_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE4:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE4_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE5:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE5_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE6:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE6_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE7:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE7_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE8:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE8_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE9:
                    frameLength = P25DFSI.P25_DFSI_LDU1_VOICE9_FRAME_LENGTH_BYTES;
                    break;
                default:
                    return;
            }

            byte[] dfsiFrame = new byte[frameLength];

            dfsiFrame[0U] = frameType;                                                      // Frame Type

            uint dstId = (uint)Program.Configuration.DestinationId;
            uint srcId = (uint)Program.Configuration.SourceId;
            if (srcIdOverride > 0 && srcIdOverride != (uint)Program.Configuration.SourceId)
                srcId = srcIdOverride;

            // different frame types mean different things
            switch (frameType)
            {
                case P25DFSI.P25_DFSI_LDU1_VOICE2:
                    {
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 1, IMBE_BUF_LEN);              // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE3:
                    {
                        dfsiFrame[1U] = P25Defines.LC_GROUP;                                // LCO
                        dfsiFrame[2U] = 0;                                                  // MFId
                        dfsiFrame[3U] = 0;                                                  // Service Options
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, IMBE_BUF_LEN);              // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE4:
                    {
                        dfsiFrame[1U] = (byte)((dstId >> 16) & 0xFFU);                      // Talkgroup Address
                        dfsiFrame[2U] = (byte)((dstId >> 8) & 0xFFU);
                        dfsiFrame[3U] = (byte)((dstId >> 0) & 0xFFU);
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, IMBE_BUF_LEN);              // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE5:
                    {
                        dfsiFrame[1U] = (byte)((srcId >> 16) & 0xFFU);                      // Source Address
                        dfsiFrame[2U] = (byte)((srcId >> 8) & 0xFFU);
                        dfsiFrame[3U] = (byte)((srcId >> 0) & 0xFFU);
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, IMBE_BUF_LEN);              // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE6:
                    {
                        dfsiFrame[1U] = 0;                                                  // RS (24,12,13)
                        dfsiFrame[2U] = 0;                                                  // RS (24,12,13)
                        dfsiFrame[3U] = 0;                                                  // RS (24,12,13)
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, IMBE_BUF_LEN);              // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE7:
                    {
                        dfsiFrame[1U] = 0;                                                  // RS (24,12,13)
                        dfsiFrame[2U] = 0;                                                  // RS (24,12,13)
                        dfsiFrame[3U] = 0;                                                  // RS (24,12,13)
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, IMBE_BUF_LEN);              // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE8:
                    {
                        dfsiFrame[1U] = 0;                                                  // RS (24,12,13)
                        dfsiFrame[2U] = 0;                                                  // RS (24,12,13)
                        dfsiFrame[3U] = 0;                                                  // RS (24,12,13)
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, IMBE_BUF_LEN);              // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU1_VOICE9:
                    {
                        dfsiFrame[1U] = 0;                                                  // LSD MSB
                        dfsiFrame[2U] = 0;                                                  // LSD LSB
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 4, IMBE_BUF_LEN);              // IMBE
                    }
                    break;

                case P25DFSI.P25_DFSI_LDU1_VOICE1:
                default:
                    {
                        dfsiFrame[6U] = 0;                                                  // RSSI
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 10, IMBE_BUF_LEN);             // IMBE
                    }
                    break;
            }

            Buffer.BlockCopy(dfsiFrame, 0, data, offset, (int)frameLength);
        }

        /// <summary>
        /// Creates an P25 LDU1 frame message.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="srcId"></param>
        private void CreateP25LDU1Message(ref byte[] data, uint srcId = 0)
        {
            // pack DFSI data
            int count = P25_MSG_HDR_SIZE;
            byte[] imbe = new byte[IMBE_BUF_LEN];

            Buffer.BlockCopy(netLDU1, 10, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU1(ref data, 24, imbe, P25DFSI.P25_DFSI_LDU1_VOICE1, srcId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE1_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 26, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU1(ref data, 46, imbe, P25DFSI.P25_DFSI_LDU1_VOICE2, srcId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE2_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 55, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU1(ref data, 60, imbe, P25DFSI.P25_DFSI_LDU1_VOICE3, srcId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE3_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 80, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU1(ref data, 77, imbe, P25DFSI.P25_DFSI_LDU1_VOICE4, srcId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE4_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 105, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU1(ref data, 94, imbe, P25DFSI.P25_DFSI_LDU1_VOICE5, srcId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE5_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 130, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU1(ref data, 111, imbe, P25DFSI.P25_DFSI_LDU1_VOICE6, srcId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE6_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 155, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU1(ref data, 128, imbe, P25DFSI.P25_DFSI_LDU1_VOICE7, srcId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE7_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 180, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU1(ref data, 145, imbe, P25DFSI.P25_DFSI_LDU1_VOICE8, srcId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE8_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU1, 204, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU1(ref data, 162, imbe, P25DFSI.P25_DFSI_LDU1_VOICE9, srcId);
            count += (int)P25DFSI.P25_DFSI_LDU1_VOICE9_FRAME_LENGTH_BYTES;

            data[23U] = (byte)count;
        }

        /// <summary>
        /// Encode a logical link data unit 2.
        /// </summary>
        /// <param name="data"></param>
        /// <param name="offset"></param>
        /// <param name="imbe"></param>
        /// <param name="frameType"></param>
        private void EncodeLDU2(ref byte[] data, int offset, byte[] imbe, byte frameType)
        {
            if (data == null)
                throw new ArgumentNullException("data");
            if (imbe == null)
                throw new ArgumentNullException("imbe");

            // determine the LDU2 DFSI frame length, its variable
            uint frameLength = P25DFSI.P25_DFSI_LDU2_VOICE10_FRAME_LENGTH_BYTES;
            switch (frameType)
            {
                case P25DFSI.P25_DFSI_LDU2_VOICE10:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE10_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE11:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE11_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE12:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE12_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE13:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE13_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE14:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE14_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE15:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE15_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE16:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE16_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE17:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE17_FRAME_LENGTH_BYTES;
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE18:
                    frameLength = P25DFSI.P25_DFSI_LDU2_VOICE18_FRAME_LENGTH_BYTES;
                    break;
                default:
                    return;
            }

            byte[] dfsiFrame = new byte[frameLength];

            dfsiFrame[0U] = frameType;                                                      // Frame Type

            // different frame types mean different things
            switch (frameType)
            {
                case P25DFSI.P25_DFSI_LDU2_VOICE11:
                    {
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 1, IMBE_BUF_LEN);              // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE12:
                    {
                        dfsiFrame[1U] = 0;                                                  // Message Indicator
                        dfsiFrame[2U] = 0;
                        dfsiFrame[3U] = 0;
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, IMBE_BUF_LEN);              // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE13:
                    {
                        dfsiFrame[1U] = 0;                                                  // Message Indicator
                        dfsiFrame[2U] = 0;
                        dfsiFrame[3U] = 0;
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, IMBE_BUF_LEN);              // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE14:
                    {
                        dfsiFrame[1U] = 0;                                                  // Message Indicator
                        dfsiFrame[2U] = 0;
                        dfsiFrame[3U] = 0;
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, IMBE_BUF_LEN);              // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE15:
                    {
                        dfsiFrame[1U] = P25Defines.P25_ALGO_UNENCRYPT;                      // Algorithm ID
                        dfsiFrame[2U] = 0;                                                  // Key ID
                        dfsiFrame[3U] = 0;
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, IMBE_BUF_LEN);              // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE16:
                    {
                        // first 3 bytes of frame are supposed to be
                        // part of the RS(24, 16, 9) of the VOICE12, 13, 14, 15
                        // control bytes
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, IMBE_BUF_LEN);              // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE17:
                    {
                        // first 3 bytes of frame are supposed to be
                        // part of the RS(24, 16, 9) of the VOICE12, 13, 14, 15
                        // control bytes
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 5, IMBE_BUF_LEN);              // IMBE
                    }
                    break;
                case P25DFSI.P25_DFSI_LDU2_VOICE18:
                    {
                        dfsiFrame[1U] = 0;                                                  // LSD MSB
                        dfsiFrame[2U] = 0;                                                  // LSD LSB
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 4, IMBE_BUF_LEN);              // IMBE
                    }
                    break;

                case P25DFSI.P25_DFSI_LDU2_VOICE10:
                default:
                    {
                        dfsiFrame[6U] = 0;                                                  // RSSI
                        Buffer.BlockCopy(imbe, 0, dfsiFrame, 10, IMBE_BUF_LEN);             // IMBE
                    }
                    break;
            }

            Buffer.BlockCopy(dfsiFrame, 0, data, offset, (int)frameLength);
        }

        /// <summary>
        /// Creates an P25 LDU2 frame message.
        /// </summary>
        /// <param name="data"></param>
        private void CreateP25LDU2Message(ref byte[] data)
        {
            // pack DFSI data
            int count = P25_MSG_HDR_SIZE;
            byte[] imbe = new byte[IMBE_BUF_LEN];

            Buffer.BlockCopy(netLDU2, 10, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU2(ref data, 24, imbe, P25DFSI.P25_DFSI_LDU2_VOICE10);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE10_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 26, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU2(ref data, 46, imbe, P25DFSI.P25_DFSI_LDU2_VOICE11);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE11_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 55, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU2(ref data, 60, imbe, P25DFSI.P25_DFSI_LDU2_VOICE12);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE12_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 80, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU2(ref data, 77, imbe, P25DFSI.P25_DFSI_LDU2_VOICE13);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE13_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 105, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU2(ref data, 94, imbe, P25DFSI.P25_DFSI_LDU2_VOICE14);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE14_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 130, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU2(ref data, 111, imbe, P25DFSI.P25_DFSI_LDU2_VOICE15);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE15_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 155, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU2(ref data, 128, imbe, P25DFSI.P25_DFSI_LDU2_VOICE16);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE16_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 180, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU2(ref data, 145, imbe, P25DFSI.P25_DFSI_LDU2_VOICE17);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE17_FRAME_LENGTH_BYTES;

            Buffer.BlockCopy(netLDU2, 204, imbe, 0, IMBE_BUF_LEN);
            EncodeLDU2(ref data, 162, imbe, P25DFSI.P25_DFSI_LDU2_VOICE18);
            count += (int)P25DFSI.P25_DFSI_LDU2_VOICE18_FRAME_LENGTH_BYTES;

            data[23U] = (byte)count;
        }

        /// <summary>
        /// Helper to encode and transmit PCM audio as P25 IMBE frames.
        /// </summary>
        /// <param name="pcm"></param>
        /// <param name="forcedSrcId"></param>
        /// <param name="forcedDstId"></param>
        private void P25EncodeAudioFrame(byte[] pcm, uint forcedSrcId = 0, uint forcedDstId = 0)
        {
            if (p25N > 17)
                p25N = 0;
            if (p25N == 0)
                FneUtils.Memset(netLDU1, 0, 9 * 25);
            if (p25N == 9)
                FneUtils.Memset(netLDU2, 0, 9 * 25);

            // Log.Logger.Debug($"BYTE BUFFER {FneUtils.HexDump(pcm)}");

            // pre-process: apply gain to PCM audio frames
            if (Program.Configuration.TxAudioGain != 1.0f)
            {
                BufferedWaveProvider buffer = new BufferedWaveProvider(waveFormat);
                buffer.AddSamples(pcm, 0, pcm.Length);

                VolumeWaveProvider16 gainControl = new VolumeWaveProvider16(buffer);
                gainControl.Volume = Program.Configuration.TxAudioGain;
                gainControl.Read(pcm, 0, pcm.Length);
            }

            int smpIdx = 0;
            short[] samples = new short[MBE_SAMPLES_LENGTH];
            for (int pcmIdx = 0; pcmIdx < pcm.Length; pcmIdx += 2)
            {
                samples[smpIdx] = (short)((pcm[pcmIdx + 1] << 8) + pcm[pcmIdx + 0]);
                smpIdx++;
            }

            // Log.Logger.Debug($"SAMPLE BUFFER {FneUtils.HexDump(samples)}");

            // encode PCM samples into IMBE codewords
            byte[] imbe = null;
#if WIN32
            if (extFullRateVocoder != null)
                extFullRateVocoder.encode(samples, out imbe);
            else
                p25Encoder.encode(samples, out imbe);
#else
            p25Encoder.encode(samples, out imbe);
#endif
            // Log.Logger.Debug($"IMBE {FneUtils.HexDump(imbe)}");
#if ENCODER_LOOPBACK_TEST
            short[] samp2 = null;
            int errs = p25Decoder.decode(imbe, out samp2);
            if (samples != null)
            {
                Log.Logger.Debug($"LOOPBACK_TEST IMBE {FneUtils.HexDump(imbe)}");
                Log.Logger.Debug($"LOOPBACK_TEST SAMPLE BUFFER {FneUtils.HexDump(samp2)}");

                int pcmIdx = 0;
                byte[] pcm2 = new byte[samp2.Length * 2];
                for (int smpIdx2 = 0; smpIdx2 < samp2.Length; smpIdx2++)
                {
                    pcm2[pcmIdx + 0] = (byte)(samp2[smpIdx2] & 0xFF);
                    pcm2[pcmIdx + 1] = (byte)((samp2[smpIdx2] >> 8) & 0xFF);
                    pcmIdx += 2;
                }

                Log.Logger.Debug($"LOOPBACK_TEST BYTE BUFFER {FneUtils.HexDump(pcm2)}");
                waveProvider.AddSamples(pcm2, 0, pcm2.Length);
            }
#else
            // fill the LDU buffers appropriately
            switch (p25N)
            {
                // LDU1
                case 0:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 10, IMBE_BUF_LEN);
                    break;
                case 1:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 26, IMBE_BUF_LEN);
                    break;
                case 2:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 55, IMBE_BUF_LEN);
                    break;
                case 3:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 80, IMBE_BUF_LEN);
                    break;
                case 4:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 105, IMBE_BUF_LEN);
                    break;
                case 5:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 130, IMBE_BUF_LEN);
                    break;
                case 6:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 155, IMBE_BUF_LEN);
                    break;
                case 7:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 180, IMBE_BUF_LEN);
                    break;
                case 8:
                    Buffer.BlockCopy(imbe, 0, netLDU1, 204, IMBE_BUF_LEN);
                    break;

                // LDU2
                case 9:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 10, IMBE_BUF_LEN);
                    break;
                case 10:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 26, IMBE_BUF_LEN);
                    break;
                case 11:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 55, IMBE_BUF_LEN);
                    break;
                case 12:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 80, IMBE_BUF_LEN);
                    break;
                case 13:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 105, IMBE_BUF_LEN);
                    break;
                case 14:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 130, IMBE_BUF_LEN);
                    break;
                case 15:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 155, IMBE_BUF_LEN);
                    break;
                case 16:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 180, IMBE_BUF_LEN);
                    break;
                case 17:
                    Buffer.BlockCopy(imbe, 0, netLDU2, 204, IMBE_BUF_LEN);
                    break;
            }

            uint srcId = (uint)Program.Configuration.SourceId;
            if (srcIdOverride != 0 && Program.Configuration.OverrideSourceIdFromUDP)
                srcId = srcIdOverride;
            if (forcedSrcId > 0 && forcedSrcId != (uint)Program.Configuration.SourceId)
                srcId = forcedSrcId;
            uint dstId = (uint)Program.Configuration.DestinationId;
            if (forcedDstId > 0 && forcedSrcId != (uint)Program.Configuration.DestinationId)
                dstId = forcedDstId;

            FnePeer peer = (FnePeer)fne;
            RemoteCallData callData = new RemoteCallData()
            {
                SrcId = srcId,
                DstId = dstId,
                LCO = P25Defines.LC_GROUP
            };

            // send P25 LDU1
            if (p25N == 8U)
            {
                ushort pktSeq = 0;
                if (p25SeqNo == 0U)
                    pktSeq = peer.pktSeq(true);
                else
                    pktSeq = peer.pktSeq();

                // Log.Logger.Information($"({SystemName}) P25D: Traffic *VOICE FRAME    * PEER {fne.PeerId} SRC_ID {srcId} TGID {dstId} [STREAM ID {txStreamId}]");

                byte[] payload = new byte[200];
                CreateP25MessageHdr((byte)P25DUID.LDU1, callData, ref payload);
                CreateP25LDU1Message(ref payload, srcId);

                peer.SendMaster(new Tuple<byte, byte>(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_P25), payload, pktSeq, txStreamId);
            }

            // send P25 LDU2
            if (p25N == 17U)
            {
                ushort pktSeq = 0;
                if (p25SeqNo == 0U)
                    pktSeq = peer.pktSeq(true);
                else
                    pktSeq = peer.pktSeq();

                // Log.Logger.Information($"({SystemName}) P25D: Traffic *VOICE FRAME    * PEER {fne.PeerId} SRC_ID {srcId} TGID {dstId} [STREAM ID {txStreamId}]");

                byte[] payload = new byte[200];
                CreateP25MessageHdr((byte)P25DUID.LDU2, callData, ref payload);
                CreateP25LDU2Message(ref payload);

                peer.SendMaster(new Tuple<byte, byte>(Constants.NET_FUNC_PROTOCOL, Constants.NET_PROTOCOL_SUBFUNC_P25), payload, pktSeq, txStreamId);
            }

            p25SeqNo++;
            p25N++;
#endif
        }

        /// <summary>
        /// Helper to decode and playback P25 IMBE frames as PCM audio.
        /// </summary>
        /// <param name="ldu"></param>
        /// <param name="e"></param>
        private void P25DecodeAudioFrame(byte[] ldu, P25DataReceivedEvent e)
        {
            try
            {
                // decode 9 IMBE codewords into PCM samples
                for (int n = 0; n < 9; n++)
                {
                    byte[] imbe = new byte[IMBE_BUF_LEN];
                    switch (n)
                    {
                        case 0:
                            Buffer.BlockCopy(ldu, 10, imbe, 0, IMBE_BUF_LEN);
                            break;
                        case 1:
                            Buffer.BlockCopy(ldu, 26, imbe, 0, IMBE_BUF_LEN);
                            break;
                        case 2:
                            Buffer.BlockCopy(ldu, 55, imbe, 0, IMBE_BUF_LEN);
                            break;
                        case 3:
                            Buffer.BlockCopy(ldu, 80, imbe, 0, IMBE_BUF_LEN);
                            break;
                        case 4:
                            Buffer.BlockCopy(ldu, 105, imbe, 0, IMBE_BUF_LEN);
                            break;
                        case 5:
                            Buffer.BlockCopy(ldu, 130, imbe, 0, IMBE_BUF_LEN);
                            break;
                        case 6:
                            Buffer.BlockCopy(ldu, 155, imbe, 0, IMBE_BUF_LEN);
                            break;
                        case 7:
                            Buffer.BlockCopy(ldu, 180, imbe, 0, IMBE_BUF_LEN);
                            break;
                        case 8:
                            Buffer.BlockCopy(ldu, 204, imbe, 0, IMBE_BUF_LEN);
                            break;
                    }

                    short[] samples = null;
                    int errs = 0;
#if WIN32
                    if (extFullRateVocoder != null)
                        errs = extFullRateVocoder.decode(imbe, out samples);
                    else
                        errs = p25Decoder.decode(imbe, out samples);
#else
                    errs = p25Decoder.decode(imbe, out samples);
#endif
                    if (samples != null)
                    {
                        if (Program.Configuration.RxAudioGain != 1.0f)
                        {
                            for (int i = 0; i < samples.Length; i++)
                            {
                                samples[i] = (short)(samples[i] * Program.Configuration.RxAudioGain);
                            }
                        }

                        pcmAccumulator.AddRange(samples);
                        int targetFrameSize = 960;

                        while (pcmAccumulator.Count >= targetFrameSize)
                        {
                            short[] frame = pcmAccumulator.Take(targetFrameSize).ToArray();
                            pcmAccumulator.RemoveRange(0, targetFrameSize);

                            Task.Run(() => ZelloStream.SendAudioAsync(frame));
                        }
                    
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Logger.Error($"Audio Decode Exception: {ex.Message}");
            }
        }

        /// <summary>
        /// Event handler used to process incoming P25 data.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        protected override void P25DataReceived(object sender, P25DataReceivedEvent e)
        {
            DateTime pktTime = DateTime.Now;

            if (Program.Configuration.TxMode != TX_MODE_P25)
                return;

            if (e.DUID == P25DUID.TSDU)
            {
                byte[] tsbk = new byte[P25Defines.P25_TSBK_LENGTH_BYTES];
                Array.Copy(e.Data, 4, tsbk, 0, tsbk.Length);

                // Console.WriteLine(FneUtils.HexDump(tsbk));

                switch (e.Data[4U])
                {
                    case P25Defines.TSBK_IOSP_CALL_ALRT:
                        IOSP_CALL_ALRT callAlert = new IOSP_CALL_ALRT();
                        callAlert.Decode(tsbk, true);
                        Log.Logger.Information($"({SystemName}) P25D: TSBK *Call Alert     * PEER {e.PeerId} SRC_ID {callAlert.SrcId} DST_ID {callAlert.DstId}");
                        //SendZelloCallAlert(callAlert.SrcId, callAlert.DstId);
                        break;
                    case P25Defines.TSBK_IOSP_ACK_RSP:
                        IOSP_ACK_RSP ackRsp = new IOSP_ACK_RSP();
                        ackRsp.Decode(tsbk, true);
                        Log.Logger.Information($"({SystemName}) P25D: TSBK *Ack Response   * PEER {e.PeerId} SRC_ID {ackRsp.SrcId} DST_ID {ackRsp.DstId} SERVICE {ackRsp.Service}");
                        //SendZelloAck(ackRsp.SrcId, ackRsp.DstId);
                        break;
                }
            }

            if (e.DUID == P25DUID.HDU || e.DUID == P25DUID.TSDU || e.DUID == P25DUID.PDU)
                return;

            uint sysId = (uint)((e.Data[11U] << 8) | (e.Data[12U] << 0));
            uint netId = FneUtils.Bytes3ToUInt32(e.Data, 16);
            byte control = e.Data[14U];

            byte len = e.Data[23];
            byte[] data = new byte[len];
            for (int i = 24; i < len; i++)
                data[i - 24] = e.Data[i];

            if (e.CallType == CallType.GROUP)
            {
                if (e.SrcId == 0)
                    return;

                if ((e.DUID == P25DUID.TDU) || (e.DUID == P25DUID.TDULC))
                {
                    // ignore TDU's that are grant demands
                    if ((control & 0x80U) == 0x80U)
                        return;
                }

                // ensure destination ID matches
                if (e.DstId != Program.Configuration.DestinationId)
                    return;

                // is this a new call stream?
                if (e.StreamId != status[P25_FIXED_SLOT].RxStreamId && ((e.DUID != P25DUID.TDU) && (e.DUID != P25DUID.TDULC)))
                {
                    callInProgress = true;
                    callAlgoId = P25Defines.P25_ALGO_UNENCRYPT;
                    status[P25_FIXED_SLOT].RxStart = pktTime;
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    ZelloStream.StartStreamAsync();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    Log.Logger.Information($"({SystemName}) P25D: Traffic *CALL START     * PEER {e.PeerId} SRC_ID {e.SrcId} TGID {e.DstId} [STREAM ID {e.StreamId}]");
                }

                if (((e.DUID == P25DUID.TDU) || (e.DUID == P25DUID.TDULC)) && (status[P25_FIXED_SLOT].RxType != FrameType.TERMINATOR))
                {
                    ignoreCall = false;
                    callAlgoId = P25Defines.P25_ALGO_UNENCRYPT;
                    callInProgress = false;
                    TimeSpan callDuration = pktTime - status[P25_FIXED_SLOT].RxStart;
                    status[P25_FIXED_SLOT].RxStreamId = 0;
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    ZelloStream.StopStreamAsync();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                    Log.Logger.Information($"({SystemName}) P25D: Traffic *CALL END       * PEER {e.PeerId} SRC_ID {e.SrcId} TGID {e.DstId} DUR {callDuration} [STREAM ID {e.StreamId}]");
                    return;
                }

                if (ignoreCall && callAlgoId == P25Defines.P25_ALGO_UNENCRYPT)
                    ignoreCall = false;

                // if this is an LDU1 see if this is the first LDU with HDU encryption data
                if (e.DUID == P25DUID.LDU1 && !ignoreCall)
                {
                    byte frameType = e.Data[180];
                    if (frameType == P25Defines.P25_FT_HDU_VALID)
                        callAlgoId = e.Data[181];
                }

                if (e.DUID == P25DUID.LDU2 && !ignoreCall)
                    callAlgoId = data[88];

                if (ignoreCall)
                    return;

                if (callAlgoId != P25Defines.P25_ALGO_UNENCRYPT)
                {
                    if (status[P25_FIXED_SLOT].RxType != FrameType.TERMINATOR)
                    {
                        callInProgress = false;
                        TimeSpan callDuration = pktTime - status[P25_FIXED_SLOT].RxStart;
                        Log.Logger.Information($"({SystemName}) P25D: Traffic *CALL END (T)    * PEER {e.PeerId} SRC_ID {e.SrcId} TGID {e.DstId} DUR {callDuration} [STREAM ID {e.StreamId}]");
                    }

                    ignoreCall = true;
                    return;
                }

                int count = 0;
                switch (e.DUID)
                {
                    case P25DUID.LDU1:
                        {
                            // The '62', '63', '64', '65', '66', '67', '68', '69', '6A' records are LDU1
                            if ((data[0U] == 0x62U) && (data[22U] == 0x63U) &&
                                (data[36U] == 0x64U) && (data[53U] == 0x65U) &&
                                (data[70U] == 0x66U) && (data[87U] == 0x67U) &&
                                (data[104U] == 0x68U) && (data[121U] == 0x69U) &&
                                (data[138U] == 0x6AU))
                            {
                                // The '62' record - IMBE Voice 1
                                Buffer.BlockCopy(data, count, netLDU1, 0, 22);
                                count += 22;

                                // The '63' record - IMBE Voice 2
                                Buffer.BlockCopy(data, count, netLDU1, 25, 14);
                                count += 14;

                                // The '64' record - IMBE Voice 3 + Link Control
                                Buffer.BlockCopy(data, count, netLDU1, 50, 17);
                                count += 17;

                                // The '65' record - IMBE Voice 4 + Link Control
                                Buffer.BlockCopy(data, count, netLDU1, 75, 17);
                                count += 17;

                                // The '66' record - IMBE Voice 5 + Link Control
                                Buffer.BlockCopy(data, count, netLDU1, 100, 17);
                                count += 17;

                                // The '67' record - IMBE Voice 6 + Link Control
                                Buffer.BlockCopy(data, count, netLDU1, 125, 17);
                                count += 17;

                                // The '68' record - IMBE Voice 7 + Link Control
                                Buffer.BlockCopy(data, count, netLDU1, 150, 17);
                                count += 17;

                                // The '69' record - IMBE Voice 8 + Link Control
                                Buffer.BlockCopy(data, count, netLDU1, 175, 17);
                                count += 17;

                                // The '6A' record - IMBE Voice 9 + Low Speed Data
                                Buffer.BlockCopy(data, count, netLDU1, 200, 16);
                                count += 16;

                                // decode 9 IMBE codewords into PCM samples
                                P25DecodeAudioFrame(netLDU1, e);
                            }
                        }
                        break;
                    case P25DUID.LDU2:
                        {
                            // The '6B', '6C', '6D', '6E', '6F', '70', '71', '72', '73' records are LDU2
                            if ((data[0U] == 0x6BU) && (data[22U] == 0x6CU) &&
                                (data[36U] == 0x6DU) && (data[53U] == 0x6EU) &&
                                (data[70U] == 0x6FU) && (data[87U] == 0x70U) &&
                                (data[104U] == 0x71U) && (data[121U] == 0x72U) &&
                                (data[138U] == 0x73U))
                            {
                                // The '6B' record - IMBE Voice 10
                                Buffer.BlockCopy(data, count, netLDU2, 0, 22);
                                count += 22;

                                // The '6C' record - IMBE Voice 11
                                Buffer.BlockCopy(data, count, netLDU2, 25, 14);
                                count += 14;

                                // The '6D' record - IMBE Voice 12 + Encryption Sync
                                Buffer.BlockCopy(data, count, netLDU2, 50, 17);
                                count += 17;

                                // The '6E' record - IMBE Voice 13 + Encryption Sync
                                Buffer.BlockCopy(data, count, netLDU2, 75, 17);
                                count += 17;

                                // The '6F' record - IMBE Voice 14 + Encryption Sync
                                Buffer.BlockCopy(data, count, netLDU2, 100, 17);
                                count += 17;

                                // The '70' record - IMBE Voice 15 + Encryption Sync
                                Buffer.BlockCopy(data, count, netLDU2, 125, 17);
                                count += 17;

                                // The '71' record - IMBE Voice 16 + Encryption Sync
                                Buffer.BlockCopy(data, count, netLDU2, 150, 17);
                                count += 17;

                                // The '72' record - IMBE Voice 17 + Encryption Sync
                                Buffer.BlockCopy(data, count, netLDU2, 175, 17);
                                count += 17;

                                // The '73' record - IMBE Voice 18 + Low Speed Data
                                Buffer.BlockCopy(data, count, netLDU2, 200, 16);
                                count += 16;

                                // decode 9 IMBE codewords into PCM samples
                                P25DecodeAudioFrame(netLDU2, e);
                            }
                        }
                        break;
                }

                status[P25_FIXED_SLOT].RxRFS = e.SrcId;
                status[P25_FIXED_SLOT].RxType = e.FrameType;
                status[P25_FIXED_SLOT].RxTGId = e.DstId;
                status[P25_FIXED_SLOT].RxTime = pktTime;
                status[P25_FIXED_SLOT].RxStreamId = e.StreamId;
            }

            return;
        }
    } // public abstract partial class FneSystemBase : fnecore.FneSystemBase
} // namespace dvmbridge