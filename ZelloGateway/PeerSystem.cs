// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Audio Bridge
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Audio Bridge
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2023 Bryan Biedenkapp, N2PLL
*   Copyright (C) 2024 Caleb, KO4UYJ
*
*/
using System;
using System.Net;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;
using System.Threading.Tasks;

using Serilog;

using fnecore;
using ZelloGateway;
using YamlDotNet.Core.Tokens;
using System.IO;
using fnecore.P25;
using fnecore.P25.LC.TSBK;

namespace ZelloGateway
{
    /// <summary>
    /// Implements a peer FNE router system.
    /// </summary>
    public class PeerSystem : FneSystemBase
    {
        protected FnePeer peer;

        private UdpClient udpAudioClient;
        private IPEndPoint endPoint;

        private static ZellStream zelloStream;

        /*
        ** Methods
        */

        /// <summary>
        /// Initializes a new instance of the <see cref="PeerSystem"/> class.
        /// </summary>
        public PeerSystem() : base(Create(), GetOrCreateZelloStream())
        {
            this.peer = (FnePeer)fne;
        }

        private static ZellStream GetOrCreateZelloStream()
        {
            string token = string.Empty;

            if (zelloStream == null)
            {
                if (Program.Configuration.ZelloPemFilePath != null)
                {
                    if (!File.Exists(Program.Configuration.ZelloPemFilePath))
                    {
                        throw new FileNotFoundException("PEM file not found", Program.Configuration.ZelloPemFilePath);
                    }
                    else
                    {
                        token = ZelloToken.CreateJwt(Program.Configuration.ZelloIssuer, File.ReadAllText(Program.Configuration.ZelloPemFilePath));
                    }
                }
                else
                {
                    token = null;
                }

                zelloStream = new ZellStream(token);
            }
            return zelloStream;
        }

        /// <summary>
        /// Internal helper to instantiate a new instance of <see cref="FnePeer"/> class.
        /// </summary>
        /// <param name="config">Peer stanza configuration</param>
        /// <returns><see cref="FnePeer"/></returns>
        private static FnePeer Create()
        {
            IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, Program.Configuration.Port);
            string presharedKey = Program.Configuration.Encrypted ? Program.Configuration.PresharedKey : null;

            if (Program.Configuration.Address == null)
                throw new NullReferenceException("address");
            if (Program.Configuration.Address == string.Empty)
                throw new ArgumentException("address");

            // handle using address as IP or resolving from hostname to IP
            try
            {
                endpoint = new IPEndPoint(IPAddress.Parse(Program.Configuration.Address), Program.Configuration.Port);
            }
            catch (FormatException)
            {
                IPAddress[] addresses = Dns.GetHostAddresses(Program.Configuration.Address);
                if (addresses.Length > 0)
                    endpoint = new IPEndPoint(addresses[0], Program.Configuration.Port);
            }

            Log.Logger.Information($"    Peer ID: {Program.Configuration.PeerId}");
            Log.Logger.Information($"    Master Addresss: {Program.Configuration.Address}");
            Log.Logger.Information($"    Master Port: {Program.Configuration.Port}");
            Log.Logger.Information($"    PCM Rx Audio Gain: {Program.Configuration.RxAudioGain}");
            Log.Logger.Information($"    Vocoder Decoder Gain (audio from network): {Program.Configuration.VocoderDecoderAudioGain}");
            string decoderAutoGainEnabled = (Program.Configuration.VocoderDecoderAutoGain) ? "yes" : "no";
            Log.Logger.Information($"    Vocoder Decoder Automatic Gain: {decoderAutoGainEnabled}");
            Log.Logger.Information($"    PCM Tx Audio Gain: {Program.Configuration.TxAudioGain}");
            Log.Logger.Information($"    Vocoder Encoder Gain (audio to network): {Program.Configuration.VocoderEncoderAudioGain}");
            switch (Program.Configuration.TxMode)
            {
                case 1:
                    Log.Logger.Information($"    Tx Audio Mode: DMR");
                    break;
                case 2:
                    Log.Logger.Information($"    Tx Audio Mode: P25");
                    break;
            }
            string grantDemandEnabled = (Program.Configuration.GrantDemand) ? "yes" : "no";
            Log.Logger.Information($"    Grant Demand: {grantDemandEnabled}");
            Log.Logger.Information($"    Source Radio ID: {Program.Configuration.SourceId}");
            string overrideSourceIdFromUDPEnabled = (Program.Configuration.OverrideSourceIdFromUDP) ? "yes" : "no";
            Log.Logger.Information($"    Override Source Radio ID from UDP: {overrideSourceIdFromUDPEnabled}");
            Log.Logger.Information($"    Destination ID: {Program.Configuration.DestinationId}");
            Log.Logger.Information($"    Destination DMR Slot: {Program.Configuration.Slot}");

            FnePeer peer = new FnePeer(Program.Configuration.Name, Program.Configuration.PeerId, endpoint, presharedKey);

            // set configuration parameters
            peer.RawPacketTrace = Program.Configuration.RawPacketTrace;

            peer.PingTime = Program.Configuration.PingTime;
            peer.Passphrase = Program.Configuration.Passphrase;
            peer.Information.Details = ConfigurationObject.ConvertToDetails(Program.Configuration);

            peer.PeerConnected += Peer_PeerConnected;

            return peer;
        }

        /// <summary>
        /// Event action that handles when a peer connects.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <exception cref="NotImplementedException"></exception>
        private static void Peer_PeerConnected(object sender, PeerConnectedEvent e)
        {
            // fake a group affiliation
            FnePeer peer = (FnePeer)sender;
            peer.SendMasterGroupAffiliation(1, (uint)Program.Configuration.DestinationId);
        }

        /// <summary>
        /// Start UDP audio listener
        /// </summary>
        public override async Task StartListeningAsync()
        {
                if (!await zelloStream.ConnectAsync() || !await zelloStream.AuthenticateAsync())
                {
                    Log.Logger.Information("Failed to connect or authenticate with Zello.");
                    return;
                }
                else
                    Log.Logger.Information("Zello server connection sucessful");

                zelloStream.OnPcmDataReceived += ProcessAudioData;
                zelloStream.OnStreamEnd += HandleZelloEnd;
                zelloStream.OnRadioCommand += HandleZelloRadioCommand;

                try
                {
                    await zelloStream.StartAudioStreamAsync();
                } catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="command"></param>
        /// <param name="srcId"></param>
        /// <param name="dstId"></param>
        private void HandleZelloRadioCommand(string command, uint srcId, uint dstId)
        {
            switch (command)
            {
                case "page":
                    Log.Logger.Information($"Zello Call Alert SrcId: {srcId} DstId: {dstId}");
                    SendP25Page(srcId, dstId);
                    break;
            }
        }

        private void SendP25Page(uint srcId, uint dstId)
        {
            byte[] tsbk = new byte[P25Defines.P25_TSBK_LENGTH_BYTES];
            byte[] payload = new byte[P25Defines.P25_TSBK_LENGTH_BYTES];

            RemoteCallData remoteCallData = new RemoteCallData();
            remoteCallData.SrcId = srcId;
            remoteCallData.DstId = dstId;
            remoteCallData.LCO = P25Defines.TSBK_IOSP_CALL_ALRT;

            IOSP_CALL_ALRT callAlert = new IOSP_CALL_ALRT(dstId, srcId);

            callAlert.Encode(ref tsbk, ref payload, true, true);

            SendP25TSBK(remoteCallData, tsbk);
        }

        /// <summary>
        /// Helper to send a activity transfer message to the master.
        /// </summary>
        /// <param name="message">Message to send</param>
        public void SendActivityTransfer(string message)
        {
            /* stub */
        }

        /// <summary>
        /// Helper to send a diagnostics transfer message to the master.
        /// </summary>
        /// <param name="message">Message to send</param>
        public void SendDiagnosticsTransfer(string message)
        {
            /* stub */
        }
    } // public class PeerSystem
} // namespace dvmbridge