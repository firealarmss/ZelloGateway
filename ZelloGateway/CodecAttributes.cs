using System;
using System.Collections.Generic;
using System.Text;

namespace ZelloGateway
{
    internal struct CodecAttributes
    {
        public int SampleRateHz { get; set; }
        public int FramesPerPacket { get; set; }
        public int FrameSizeMs { get; set; }
    }
}
