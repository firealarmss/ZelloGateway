// SPDX-License-Identifier: AGPL-3.0-only
/**
* Digital Voice Modem - Audio Bridge
* AGPLv3 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*
* @package DVM / Audio Bridge
* @license AGPLv3 License (https://opensource.org/licenses/AGPL-3.0)
*
*   Copyright (C) 2024 Caleb, K4PHP
*
*/

using System;
using System.Timers;

namespace ZelloGateway
{
    /// <summary>
    /// Zello helper class for keep alive ping/pong
    /// </summary>
    public class KeepAlive
    {
        private readonly double PingInterval;
        private readonly Timer PingTimer;
        private DateTime LastPingTime;
        private bool AwaitingPong;

        public event Action Ping;

        public bool Connected { get; set; }
        public int PingCount { get; set; }

        /// <summary>
        /// Creates an instance of <see cref="KeepAlive"/>
        /// </summary>
        /// <param name="pingInterval"></param>
        public KeepAlive(double pingInterval = 5000)
        {
            PingInterval = pingInterval;
            PingTimer = new Timer(PingInterval);
            PingTimer.Elapsed += OnPingTimerElapsed;
            PingTimer.AutoReset = true;
            PingTimer.Start();

            Connected = false;
            AwaitingPong = false;
        }

        /// <summary>
        /// Ping Timer Elapsed callback
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnPingTimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (AwaitingPong)
            {
                Connected = false;
            }
            else
            {
                SendPing();
            }
        }

        /// <summary>
        /// Send ping
        /// </summary>
        private void SendPing()
        {
            PingCount++;
            LastPingTime = DateTime.Now;
            // AwaitingPong = true; // TODO: Use this? Idk yet

            Ping.Invoke();
        }

        /// <summary>
        /// Receive pong
        /// </summary>
        public void ReceivePong()
        {
            if (AwaitingPong)
            {
                AwaitingPong = false;
                // TODO: Use this? Idk yet
            }
        }

        /// <summary>
        /// Cleanup
        /// </summary>
        public void Stop()
        {
            PingTimer.Stop();
        }
    }
}
