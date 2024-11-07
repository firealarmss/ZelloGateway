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
    /// Zello response object
    /// </summary>
    public class ZelloResponse
    {
        public string command { get; set; }
        public string channel { get; set; }
        public string text { get; set; }
        public string from { get; set; }
        public string codec_header { get; set; }
        public string refresh_token { get; set; }
        public int? stream_id { get; set; }
        public bool? success { get; set; }
        public int? seq { get; set; }
    }
}
