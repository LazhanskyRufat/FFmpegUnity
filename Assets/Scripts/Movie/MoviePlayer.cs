using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using UnityEngine;
using UnityEngine.UI;

using UniRx;

using FFmpeg.AutoGen;


namespace Assets.Scripts.Movie
{
    public unsafe class MoviePlayer : MonoBehaviour, ICancelable
    {
        private const int FramesBufferSize = 30;

        [SerializeField]
        private RawImage _movieFrameImage;
        [SerializeField]
        private Image _backgroundImage;

        private Texture2D _movieFrameTexture;
        private AspectRatioFitter _aspectRatioFitter;

        private MovieClipWrapper _movieClipWrapper;

        private IDisposable _disposable;

        public bool IsDisposed { get; private set; }


        private void Awake()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playmodeStateChanged += OnPlaymodeStateChanged;
#endif

            _aspectRatioFitter = _movieFrameImage.GetComponent<AspectRatioFitter>();

            _movieClipWrapper = new MovieClipWrapper(AVPixelFormat.AV_PIX_FMT_ARGB);

            _disposable = Observable.Concat(IterateMovies().Select(moviePath => LoadAndPlayMovie(moviePath))).Subscribe();
        }

        private void OnDisable()
        {
            Dispose();
        }

        public IEnumerable<string> IterateMovies()
        {
            yield return Path.Combine(Application.streamingAssetsPath, "i-moolt_logo_ru.mp4");
            yield return Path.Combine(Application.streamingAssetsPath, "PatrolQuest_intro.mp4");
            yield return Path.Combine(Application.streamingAssetsPath, "PatrolQuest_outro.mp4");
        }

        public IObservable<string> LoadAndPlayMovie(string moviePath)
        {
            return Observable.Concat
            (
                _movieClipWrapper.LoadMovieAsync(moviePath),
                CreateTexture().Do(Debug.Log),
                Observable.Concat
                (
                    _movieClipWrapper.OnFramesBufferPrepared.First().Do(Debug.Log),
                    PlayMovie()
                ).
                SkipUntil(_movieClipWrapper.PrepareFramesBufferAsync(new CancellationToken(this))).
                First().
                Select(_ => "Finished").
                Do(Debug.Log)
            ).
            DoOnCompleted(_movieClipWrapper.Dispose);
        }

        public void Dispose()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playmodeStateChanged -= OnPlaymodeStateChanged;
#endif
            _disposable.Dispose();
            IsDisposed = true;
        }

        private IObservable<string> CreateTexture()
        {
            return Observable.Create<string>(observer =>
            {
                int width = _movieClipWrapper.FrameWidth, 
                    height = _movieClipWrapper.FrameHeight;

                _aspectRatioFitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
                _aspectRatioFitter.aspectRatio = (float)width / height;

                _movieFrameTexture = new Texture2D(width, height, TextureFormat.ARGB32, false);
                _movieFrameTexture.filterMode = FilterMode.Bilinear;
                _movieFrameTexture.wrapMode = TextureWrapMode.Clamp;

                observer.OnNext("Render texture prepared!");
                observer.OnCompleted();

                return Disposable.Empty;
            });
        }

        private IObservable<string> PlayMovie()
        {
            return Observable.Timer(TimeSpan.FromSeconds(0f), TimeSpan.FromMilliseconds(10)).
                SubscribeOnMainThread().
                ObserveOnMainThread().
                Select(_ =>
                {
                    lock (_movieClipWrapper.FramesBuffer)
                    {
                        if (_movieClipWrapper.FramesBuffer.Count > 0)
                        {
                            byte[] videoData = _movieClipWrapper.FramesBuffer.Dequeue();

                            if (_movieFrameTexture != _movieFrameImage.texture)
                            {
                                _movieFrameImage.texture = _movieFrameTexture;
                            }

                            _movieFrameTexture.LoadRawTextureData(videoData);
                            _movieFrameTexture.Apply();

                            videoData = null;

                            return false;
                        }
                        else
                        {
                            return true;
                        }
                    }
                }).
                Where(bufferIsEmpty => bufferIsEmpty).
                Select(_ => "Buffer is empty");
        }

        private void OnApplicationPause(bool pause)
        {
#if UNITY_EDITOR
            if(UnityEditor.EditorApplication.isPaused)
            {
                return;
            }
#endif

            _movieClipWrapper.PauseBufferPreparation = pause;
        }

#if UNITY_EDITOR
        private void OnPlaymodeStateChanged()
        {
            _movieClipWrapper.PauseBufferPreparation = UnityEditor.EditorApplication.isPaused;
        }
#endif
    }
}
