using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using UnityEngine;

using UniRx;

using FFmpeg.AutoGen;


namespace Assets.Scripts.Movie
{
    public unsafe class MovieClipWrapper : IDisposable
    {
        private const string MessageHeader = "MovieClipWrapper";
        private const string MessageFormat = "[" + MessageHeader + "::{0}] {1}";
        private const int FramesBufferSize = 30;

        private bool _pauseBufferPreparation = false;
        private readonly object _locker = new object();

        private AVPixelFormat _pixelFormat;
        private Queue<byte[]> _framesBuffer;
        private Subject<string> _onFramesBufferPreparedSubject;

        private AVFormatContext* _formatContext = null;
        private int _videoStreamIndex = -1;
        private int _audioStreamIndex = -1;
        private AVCodecContext* _videoCodecContext = null;
        private AVCodec* _videoCodec = null;
        private AVCodecContext* _audioCodecContext = null;
        private AVCodec* _audioCodec = null;
        private SwsContext* _imageConvertContext = null;

        public bool PauseBufferPreparation
        {
            get
            {
                return _pauseBufferPreparation;
            }
            set
            {
                lock (_locker)
                {
                    _pauseBufferPreparation = value;
                    Monitor.Pulse(_locker);
                }
            }
        }

        public Queue<byte[]> FramesBuffer { get { return _framesBuffer; } }

        public int FrameWidth { get; private set; }
        public int FrameHeight { get; private set; }

        public IObservable<string> OnFramesBufferPrepared { get { return _onFramesBufferPreparedSubject; } }


        public MovieClipWrapper(AVPixelFormat pixelFormat)
        {
            ffmpeg.av_register_all();

            _pixelFormat = pixelFormat;
        }


        public IObservable<string> LoadMovieAsync(string moviePath)
        {
            _framesBuffer = new Queue<byte[]>(FramesBufferSize);
            _onFramesBufferPreparedSubject = new Subject<string>();

            return Observable.Concat
            (
                OpenMovie(moviePath),
                FindStreamInfo(),
                FindVideoStreamIndex(),
                FindVideoCodecAndCodecContext(),
                FindAudioStreamIndex(),
                FindAudioCodecAndCodecContext(),
                GetImageConvertContext()
            ).
            SubscribeOn(Scheduler.ThreadPool).
            ObserveOnMainThread().
            Do(Debug.Log).
            DoOnError(exception =>
            {
                Debug.LogException(exception);
                Dispose();
            });
        }

        public IObservable<string> PrepareFramesBufferAsync(CancellationToken cancellationToken)
        {
            return Observable.Create<string>(observer =>
            {
                AVFrame* frame = ffmpeg.av_frame_alloc();
                AVPacket packet;

                string message = FormatMesage("PrepareFramesBufferAsync",
                    "Frames buffer prepared");

                int width = FrameWidth,
                    height = FrameHeight,
                    videoDataSize = 4 * width * height;

                bool loop = true;

                while (loop && !cancellationToken.IsCancellationRequested)
                {
                    lock (_locker)
                    {
                        if (PauseBufferPreparation)
                        {
                            Monitor.Wait(_locker);
                        }
                    }

                    lock (_framesBuffer)
                    {
                        if (_framesBuffer.Count == FramesBufferSize)
                        {
                            _onFramesBufferPreparedSubject.OnNext(message);
                            continue;
                        }
                    }

                    loop = ffmpeg.av_read_frame(_formatContext, &packet) >= 0;

                    if (!loop)
                    {
                        break;
                    }

                    if (packet.stream_index == _videoStreamIndex)
                    {
                        int frameFinished = 0;

                        ffmpeg.avcodec_decode_video2(_videoCodecContext, frame, &frameFinished, &packet);

                        if (frameFinished > 0)
                        {
                            try
                            {
                                AVPicture picture;

                                ffmpeg.avpicture_alloc(&picture, _pixelFormat, width, height);

                                ffmpeg.sws_scale(_imageConvertContext,
                                    &frame->data0, frame->linesize,
                                    0, _videoCodecContext->height,
                                    &picture.data0, picture.linesize);

                                sbyte* convertedFrameAddress = picture.data0;
                                var imageDataPtr = new IntPtr(convertedFrameAddress);

                                byte[] videoData = new byte[videoDataSize];
                                Marshal.Copy(imageDataPtr, videoData, 0, videoDataSize);

                                lock (_framesBuffer)
                                {
                                    _framesBuffer.Enqueue(videoData);
                                }

                                ffmpeg.avpicture_free(&picture);
                            }
                            catch (Exception exception)
                            {
                                observer.OnError(exception);
                            }
                        }
                    }

                    if (packet.stream_index == _audioStreamIndex)
                    {
                        int frameFinished = 0;

                        ffmpeg.avcodec_decode_audio4(_audioCodecContext, frame, &frameFinished, &packet);

                        if (frameFinished > 0)
                        {
                            try
                            {
                                int audioDataSize = ffmpeg.av_samples_get_buffer_size(null, _audioCodecContext->channels, frame->nb_samples, AVSampleFormat.AV_SAMPLE_FMT_FLT, 1);

                                sbyte* soundFrameAddress = null;

                                if (_audioCodecContext->sample_fmt == AVSampleFormat.AV_SAMPLE_FMT_FLT)
                                {
                                    soundFrameAddress = frame->extended_data[0];
                                }
                                else
                                {
                                    sbyte* convertedAudioData = (sbyte*)Marshal.AllocCoTaskMem(audioDataSize * sizeof(sbyte));

                                    SwrContext* audioConvertContext = ffmpeg.swr_alloc_set_opts(null,
                                        (long)_audioCodecContext->channel_layout,
                                        AVSampleFormat.AV_SAMPLE_FMT_FLT,
                                        _audioCodecContext->sample_rate,
                                        (long)_audioCodecContext->channel_layout,
                                        _audioCodecContext->sample_fmt,
                                        _audioCodecContext->sample_rate,
                                        0,
                                        (void*)0);

                                    int error = ffmpeg.swr_init(audioConvertContext);

                                    if(error < 0)
                                    {
                                        string errorMessage = FormatMesage("PrepareFramesBufferAsync",
                                            "Error while decoding audio!");

                                        observer.OnError(new Exception(errorMessage));
                                    }

                                    ffmpeg.swr_convert(audioConvertContext, &convertedAudioData, audioDataSize, frame->extended_data, frame->nb_samples);

                                    soundFrameAddress = convertedAudioData;

                                    ffmpeg.swr_free(&audioConvertContext);
                                }

                                var soundBufferPtr = new IntPtr(soundFrameAddress);

                                byte[] audioDataBuffer = new byte[audioDataSize];
                                Marshal.Copy(soundBufferPtr, audioDataBuffer, 0, audioDataSize);

                                float[] audioData = new float[audioDataSize / 4];
                                Buffer.BlockCopy(audioDataBuffer, 0, audioData, 0, audioDataSize);

                                Marshal.FreeCoTaskMem(soundBufferPtr);
                            }
                            catch (Exception exception)
                            {
                                observer.OnError(exception);
                            }
                        }
                    }

                    ffmpeg.av_free_packet(&packet);
                }

                ffmpeg.av_free_packet(&packet);
                ffmpeg.av_frame_free(&frame);

                message = FormatMesage("PrepareFramesBufferAsync",
                    "All frames loaded");

                _onFramesBufferPreparedSubject.OnNext(message);
                _onFramesBufferPreparedSubject.OnCompleted();

                if (!cancellationToken.IsCancellationRequested)
                {
                    observer.OnNext(message);
                    observer.OnCompleted();
                }

                return Disposable.Empty;
            }).
            SubscribeOn(Scheduler.ThreadPool).
            ObserveOnMainThread();
        }

        private IObservable<string> OpenMovie(string moviePath)
        {
            return Observable.Create<string>(observer =>
            {
                AVFormatContext* formatContext = null;

                int error = ffmpeg.avformat_open_input(&formatContext, moviePath, null, null);

                if (error < 0)
                {
                    string errorMessage = FormatMesage("OpenMovie",
                        "Could not open movie at path: " + moviePath);

                    observer.OnError(new Exception(errorMessage));
                }
                else
                {
                    string message = FormatMesage("OpenMovie",
                        "Opened successfully at path: " + moviePath);

                    _formatContext = formatContext;

                    observer.OnNext(message);
                    observer.OnCompleted();
                }

                return Disposable.Empty;
            });
        }

        private IObservable<string> FindStreamInfo()
        {
            return Observable.Create<string>(observer =>
            {
                int error = ffmpeg.avformat_find_stream_info(_formatContext, null);

                if (error < 0)
                {
                    string errorMessage = FormatMesage("FindStreamInfo",
                        "Could not find stream info!");
                    observer.OnError(new Exception(errorMessage));
                }
                else
                {
                    string message = FormatMesage("FindStreamInfo",
                        "Stream info found");

                    observer.OnNext(message);
                    observer.OnCompleted();
                }

                return Disposable.Empty;
            });
        }

        private IObservable<string> FindVideoStreamIndex()
        {
            return Observable.Create<string>(observer =>
            {
                uint streamCount = _formatContext->nb_streams;

                for (_videoStreamIndex = 0; _videoStreamIndex < streamCount; _videoStreamIndex++)
                {
                    if (_formatContext->streams[_videoStreamIndex]->codec->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                    {
                        break;
                    }
                }

                if (_videoStreamIndex == streamCount)
                {
                    string errorMessage = FormatMesage("FindVideoStreamIndex",
                       "Could not find video stream!");
                    observer.OnError(new Exception(errorMessage));
                }
                else
                {
                    string message = FormatMesage("FindVideoStreamIndex",
                        "Video stream index found");

                    observer.OnNext(message);
                    observer.OnCompleted();
                }

                return Disposable.Empty;
            });
        }

        private IObservable<string> FindVideoCodecAndCodecContext()
        {
            return Observable.Create<string>(observer =>
            {
                AVCodecContext* videoCodecContext = _formatContext->streams[_videoStreamIndex]->codec;
                AVCodec* videoCodec = ffmpeg.avcodec_find_decoder(videoCodecContext->codec_id);

                int error = ffmpeg.avcodec_open2(videoCodecContext, videoCodec, null);

                if (error < 0)
                {
                    string errorMessage = FormatMesage("FindVideoCodecAndCodecContext",
                       "Could not find codec!");
                    observer.OnError(new Exception(errorMessage));
                }
                else
                {
                    _videoCodecContext = videoCodecContext;
                    _videoCodec = videoCodec;

                    string message = FormatMesage("FindVideoCodecAndCodecContext",
                        "Codec found");

                    observer.OnNext(message);
                    observer.OnCompleted();
                }

                return Disposable.Empty;
            });
        }

        private IObservable<string> FindAudioStreamIndex()
        {
            return FindStreamIndices(AVMediaType.AVMEDIA_TYPE_AUDIO).
                First().
                Select(streamIndex =>
                {
                    _audioStreamIndex = streamIndex;

                    string message = FormatMesage("FindAudioStreamIndex",
                        "Audio stream index found");

                    return message;
                }).
                Catch<string, Exception>(exception =>
                {
                    string errorMessage = FormatMesage("FindAudioStreamIndex",
                        "Could not find audio stream!");

                    return Observable.Throw<string>(new Exception(errorMessage));
                });
        }

        private IObservable<int> FindStreamIndices(AVMediaType mediaType)
        {
            return Observable.Create<int>(observer =>
            {
                int streamIndex;
                uint streamCount = _formatContext->nb_streams;

                for (streamIndex = 0; streamIndex < streamCount; streamIndex++)
                {
                    if (_formatContext->streams[streamIndex]->codec->codec_type == mediaType)
                    {
                        observer.OnNext(streamIndex);
                    }
                }

                if (_audioStreamIndex == streamCount)
                {
                    string errorMessage = FormatMesage("FindStreamIndices",
                       "Could not find any stream!");
                    observer.OnError(new Exception(errorMessage));
                }
                else
                {
                    observer.OnCompleted();
                }

                return Disposable.Empty;
            });
        }

        private IObservable<string> FindAudioCodecAndCodecContext()
        {
            return Observable.Create<string>(observer =>
            {
                AVCodecContext* audioCodecContext = _formatContext->streams[_audioStreamIndex]->codec;
                AVCodec* audioCodec = ffmpeg.avcodec_find_decoder(audioCodecContext->codec_id);

                int error = ffmpeg.avcodec_open2(audioCodecContext, audioCodec, null);

                if (error < 0)
                {
                    string errorMessage = FormatMesage("FindAudioCodecAndCodecContext",
                       "Could not find codec!");
                    observer.OnError(new Exception(errorMessage));
                }
                else
                {
                    _audioCodecContext = audioCodecContext;
                    _audioCodec = audioCodec;

                    string message = FormatMesage("FindAudioCodecAndCodecContext",
                        "Codec found");

                    observer.OnNext(message);
                    observer.OnCompleted();
                }

                return Disposable.Empty;
            });
        }

        private IObservable<string> GetImageConvertContext()
        {
            return Observable.Create<string>(observer =>
            {
                FrameWidth = _videoCodecContext->width;
                FrameHeight = _videoCodecContext->height;

                _imageConvertContext = ffmpeg.sws_getCachedContext(null,
                    FrameWidth, FrameHeight,
                    _videoCodecContext->pix_fmt,
                    FrameWidth, FrameHeight,
                    _pixelFormat,
                    ffmpeg.SWS_BICUBIC,
                    null, null, null);

                if (_imageConvertContext == null)
                {
                    string errorMessage = FormatMesage("GetImageConvertContext",
                       "Could not create image convert context!");
                    observer.OnError(new Exception(errorMessage));
                }
                else
                {
                    string message = FormatMesage("GetImageConvertContext",
                        "Image convert context created");

                    observer.OnNext(message);
                    observer.OnCompleted();
                }

                return Disposable.Empty;
            });
        }

        public void Dispose()
        {
            if (_imageConvertContext != null)
            {
                ffmpeg.sws_freeContext(_imageConvertContext);
                _imageConvertContext = null;
            }

            if (_videoCodecContext != null)
            {
                ffmpeg.avcodec_close(_videoCodecContext);
                _videoCodecContext = null;
            }

            if (_formatContext != null)
            {
                ffmpeg.avformat_free_context(_formatContext);
                _formatContext = null;
            }
        }

        private string FormatMesage(string methodName, string messageContent)
        {
            return string.Format(MessageFormat, methodName, messageContent);
        }
    }
}
