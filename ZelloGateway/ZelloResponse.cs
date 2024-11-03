using System;
using System.Collections.Generic;
using System.Text;

namespace ZelloGateway
{
    public class ZelloResponse
    {
        public string command { get; set; }
        public string channel { get; set; }
        public string text { get; set; }
        public string from { get; set; }
        public string codec_header { get; set; }
        public int? stream_id { get; set; }
        public bool? success { get; set; }
        public int? seq { get; set; }
    }
}
