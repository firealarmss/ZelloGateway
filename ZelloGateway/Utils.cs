using System;
using System.Collections.Generic;
using System.Text;

namespace ZelloGateway
{
    /// <summary>
    /// Helper functions
    /// </summary>
    internal static class Utils
    {
        /// <summary>
        /// Helper to decode zello codec header
        /// </summary>
        /// <param name="codecHeaderBase64"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        internal static CodecAttributes DecodeCodecHeader(string codecHeaderBase64)
        {
            byte[] codecHeaderBytes = Convert.FromBase64String(codecHeaderBase64);

            if (codecHeaderBytes.Length != 4)
            {
                throw new ArgumentException("Invalid codec header length.");
            }

            int sampleRateHz = BitConverter.ToUInt16(codecHeaderBytes, 0);
            int framesPerPacket = codecHeaderBytes[2];
            int frameSizeMs = codecHeaderBytes[3];

            return new CodecAttributes
            {
                SampleRateHz = sampleRateHz,
                FramesPerPacket = framesPerPacket,
                FrameSizeMs = frameSizeMs
            };
        }

        /// <summary>
        /// Helper to resample audio
        /// </summary>
        /// <param name="input"></param>
        /// <param name="inputLength"></param>
        /// <param name="inputRate"></param>
        /// <param name="outputRate"></param>
        /// <returns></returns>
        internal static short[] Resample(short[] input, int inputLength, int inputRate, int outputRate)
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
    }
}
