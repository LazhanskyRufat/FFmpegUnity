using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;


namespace Assets.Scripts.Movie
{
    public class MovieFrame
    {
        public int FrameNumber { get; private set; }
        public byte[] VideoData { get; private set; }
        public float[] AudioData { get; private set; }

        public MovieFrame(int frameNumber, byte[] videoData, float[] audioData)
        {
            FrameNumber = frameNumber;
            VideoData = videoData;
            AudioData = audioData;
        }
    }
}
