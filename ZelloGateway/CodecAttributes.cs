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

namespace ZelloGateway
{
    /// <summary>
    /// Zello Codec Attributes
    /// </summary>
    internal struct CodecAttributes
    {
        public int SampleRateHz { get; set; }
        public int FramesPerPacket { get; set; }
        public int FrameSizeMs { get; set; }
    }
}
