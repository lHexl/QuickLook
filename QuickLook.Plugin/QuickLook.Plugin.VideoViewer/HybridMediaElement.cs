// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLook program.

using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using SharpDX.Mathematics.Interop;
using SharpDX.MediaFoundation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using WPFMediaKit.DirectShow.Controls;
using WPFMediaKit.DirectShow.MediaPlayers;

namespace QuickLook.Plugin.VideoViewer;

/// <summary>
/// Plays formats supported by Windows through the native IMFMediaEngine and
/// falls back to the existing DirectShow/LAV player for everything else.
/// </summary>
public sealed class HybridMediaElement : Grid, IDisposable
{
    private static readonly HashSet<string> MediaFoundationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".3gp", ".3g2", ".mkv", ".webm"
    };

    private static readonly object MediaFoundationStartupLock = new();
    private static bool _mediaFoundationStartupAttempted;
    private static Exception _mediaFoundationStartupException;

    private readonly MediaUriElement _directShowElement;
    private readonly NativeMediaFoundationHost _mediaFoundationHost;
    private readonly DispatcherTimer _positionTimer;

    private MediaEngineClassFactory _mediaEngineFactory;
    private MediaEngineAttributes _mediaEngineAttributes;
    private MediaEngine _mediaEngine;
    private MediaEngineEx _mediaEngineEx;
    private MediaEngineNotifyDelegate _mediaEngineNotify;
    private Uri _source;
    private Uri _loadedMediaFoundationSource;
    private volatile bool _usingMediaFoundation;
    private bool _mediaFoundationOpened;
    private bool _mediaFoundationInitializationFailed;
    private bool _mediaFoundationIsPlaying;
    private volatile bool _directShowOpened;
    private volatile bool _directShowIsPlaying;
    private bool _playRequested;
    private bool _loop;
    private bool _disposed;
    private bool _updatingPosition;
    private double _volume = 1d;

    public HybridMediaElement()
    {
        _directShowElement = new MediaUriElement();
        Children.Add(_directShowElement);

        // IMFMediaEngine renders straight into this child HWND. This avoids
        // XAML Islands and their package/WinRT activation requirements.
        _mediaFoundationHost = new NativeMediaFoundationHost
        {
            Visibility = Visibility.Collapsed,
            // HwndHost has WPF airspace restrictions. Keep the bottom control
            // strip outside the native child window so it remains interactive.
            Margin = new Thickness(0, 0, 0, 32),
        };
        _mediaFoundationHost.HandleCreated += MediaFoundationHostHandleCreated;
        _mediaFoundationHost.HandleDestroyed += MediaFoundationHostHandleDestroyed;
        _mediaFoundationHost.SizeChanged += MediaFoundationHostSizeChanged;
        Children.Add(_mediaFoundationHost);

        _directShowElement.MediaOpened += DirectShowMediaOpened;
        _directShowElement.MediaEnded += DirectShowMediaEnded;
        _directShowElement.MediaFailed += DirectShowMediaFailed;
        _directShowElement.MediaUriPlayer.PlayerStateChanged += DirectShowPlayerStateChanged;

        _positionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _positionTimer.Tick += UpdatePosition;
    }

    public MediaUriPlayer MediaUriPlayer => _directShowElement.MediaUriPlayer;

    public ContextObject PerformanceContext { get; set; }

    public bool IsUsingMediaFoundation => _usingMediaFoundation;

    public bool Loop
    {
        get => _loop;
        set
        {
            _loop = value;
            if (_mediaEngine != null)
                _mediaEngine.Loop = value;
            _directShowElement.MediaUriPlayer.Loop = value;
        }
    }

    public Uri Source
    {
        get => _source;
        set
        {
            _source = value;
            OpenSource(value);
        }
    }

    public bool IsPlaying => _usingMediaFoundation ? _mediaFoundationIsPlaying : _directShowIsPlaying;

    public bool HasVideo { get; private set; }

    public long MediaPosition
    {
        get => (long)GetValue(MediaPositionProperty);
        set => SetCurrentValue(MediaPositionProperty, value);
    }

    public static readonly DependencyProperty MediaPositionProperty = DependencyProperty.Register(
        nameof(MediaPosition), typeof(long), typeof(HybridMediaElement),
        new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, MediaPositionChanged));

    public long MediaDuration
    {
        get => (long)GetValue(MediaDurationProperty);
        private set => SetValue(MediaDurationPropertyKey, value);
    }

    private static readonly DependencyPropertyKey MediaDurationPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(MediaDuration), typeof(long), typeof(HybridMediaElement), new FrameworkPropertyMetadata(0L));

    public static readonly DependencyProperty MediaDurationProperty = MediaDurationPropertyKey.DependencyProperty;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Max(0d, Math.Min(1d, value));
            if (_mediaEngine != null)
                _mediaEngine.Volume = _volume;
            _directShowElement.Volume = _volume;
        }
    }

    public event RoutedEventHandler MediaOpened;
    public event RoutedEventHandler MediaEnded;
    public event EventHandler<HybridMediaFailedEventArgs> MediaFailed;
    public event EventHandler PlaybackStateChanged;

    public void Play()
    {
        _playRequested = true;

        if (!_usingMediaFoundation)
        {
            // MediaUriElement opens the graph asynchronously. Calling Play
            // before MediaOpened can leave the fallback in a false Playing
            // state with position 0, so remember the request and start later.
            if (_directShowOpened)
                _directShowElement.Play();
            return;
        }

        if (_mediaEngine != null)
        {
            PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.Play.Calling");
            _mediaEngine.Play();
            PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.Play.Returned");
        }
    }

    public void Pause()
    {
        _playRequested = false;

        if (_usingMediaFoundation)
            PauseMediaFoundation();
        else if (_directShowOpened)
            _directShowElement.Pause();
    }

    public void Close()
    {
        _playRequested = false;
        _positionTimer.Stop();
        PauseMediaFoundation();
        _directShowElement.Close();
        _usingMediaFoundation = false;
        _mediaFoundationOpened = false;
        _mediaFoundationIsPlaying = false;
        _directShowOpened = false;
        _directShowIsPlaying = false;
        _loadedMediaFoundationSource = null;
        HasVideo = false;
        MediaDuration = 0L;
        UpdatePositionValue(0L);
    }

    private void OpenSource(Uri source)
    {
        Close();
        if (source == null)
            return;

        if (CanUseMediaFoundation(source) && !_mediaFoundationInitializationFailed)
        {
            _usingMediaFoundation = true;
            _directShowElement.Visibility = Visibility.Collapsed;
            _mediaFoundationHost.Visibility = Visibility.Visible;
            PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.Selected",
                $"path={source.LocalPath}; hwnd=0x{_mediaFoundationHost.Handle.ToInt64():X}");

            // Showing an HwndHost normally creates the handle immediately when
            // the visual is loaded. If it is not loaded yet, HandleCreated will
            // continue initialization without blocking this setter.
            if (_mediaFoundationHost.Handle != IntPtr.Zero)
            {
                EnsureMediaFoundationEngine();
                if (_mediaEngine != null)
                    OpenMediaFoundationSource(source);
            }
            return;
        }

        OpenWithDirectShow(source,
            _mediaFoundationInitializationFailed ? "media-foundation-initialization-failed" : "unsupported-extension");
    }

    private static bool CanUseMediaFoundation(Uri source) =>
        source.IsFile && MediaFoundationExtensions.Contains(Path.GetExtension(source.LocalPath));

    private static void MediaPositionChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var element = (HybridMediaElement)dependencyObject;
        if (element._updatingPosition || element._source == null)
            return;

        long position = Math.Max(0L, (long)args.NewValue);
        if (element._usingMediaFoundation)
        {
            if (element._mediaEngine != null)
                element._mediaEngine.CurrentTime = TimeSpan.FromTicks(position).TotalSeconds;
        }
        else
        {
            element._directShowElement.MediaPosition = position;
        }
    }

    private void UpdatePositionValue(long position)
    {
        _updatingPosition = true;
        try
        {
            SetCurrentValue(MediaPositionProperty, position);
        }
        finally
        {
            _updatingPosition = false;
        }
    }

    private void MediaFoundationHostHandleCreated(object sender, EventArgs e)
    {
        if (_disposed)
            return;

        PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.Hwnd.Created",
            $"hwnd=0x{_mediaFoundationHost.Handle.ToInt64():X}");
        EnsureMediaFoundationEngine();

        if (_usingMediaFoundation && _mediaEngine != null && _source != null)
            OpenMediaFoundationSource(_source);
    }

    private void MediaFoundationHostHandleDestroyed(object sender, EventArgs e)
    {
        PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.Hwnd.Destroyed");
        ReleaseMediaFoundationEngine();
    }

    private void EnsureMediaFoundationEngine()
    {
        if (_mediaEngine != null || _mediaFoundationInitializationFailed || _disposed)
            return;

        try
        {
            EnsureMediaFoundationStarted();

            _mediaEngineNotify = MediaFoundationEvent;
            _mediaEngineFactory = new MediaEngineClassFactory();
            _mediaEngineAttributes = new MediaEngineAttributes(2);
            _mediaEngineAttributes.Set(MediaEngineAttributeKeys.PlaybackHwnd, _mediaFoundationHost.Handle);
            _mediaEngine = new MediaEngine(
                _mediaEngineFactory,
                _mediaEngineAttributes,
                MediaEngineCreateFlags.None,
                _mediaEngineNotify)
            {
                AutoPlay = false,
                Loop = _loop,
                Volume = _volume,
            };
            _mediaEngineEx = _mediaEngine.QueryInterface<MediaEngineEx>();

            PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.Engine.Created",
                $"hwnd=0x{_mediaFoundationHost.Handle.ToInt64():X}");
        }
        catch (Exception exception)
        {
            _mediaFoundationInitializationFailed = true;
            PreviewPerformanceLogger.WriteGlobal("VideoPanel.MediaFoundation.InitializationFailed", exception.ToString());
            ReleaseMediaFoundationEngine();

            if (_usingMediaFoundation && _source != null)
                OpenWithDirectShow(_source, $"initialization: {exception.Message}");
        }
    }

    private static void EnsureMediaFoundationStarted()
    {
        lock (MediaFoundationStartupLock)
        {
            if (!_mediaFoundationStartupAttempted)
            {
                _mediaFoundationStartupAttempted = true;
                try
                {
                    MediaManager.Startup();
                }
                catch (Exception exception)
                {
                    _mediaFoundationStartupException = exception;
                }
            }

            if (_mediaFoundationStartupException != null)
                throw new InvalidOperationException("Media Foundation could not be started.", _mediaFoundationStartupException);
        }
    }

    private void OpenMediaFoundationSource(Uri source)
    {
        if (_mediaEngine == null || !_usingMediaFoundation || !Equals(source, _source) ||
            Equals(source, _loadedMediaFoundationSource))
            return;

        try
        {
            // Visibility=Visible can synchronously create HwndHost and enter
            // this method through HandleCreated. Remember the source before
            // calling COM so the outer path cannot submit the same load twice.
            _loadedMediaFoundationSource = source;
            _mediaFoundationOpened = false;
            _mediaFoundationIsPlaying = false;
            HasVideo = false;
            string mediaFoundationSource = GetMediaFoundationSource(source);
            PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.Source.Assigning",
                $"source={mediaFoundationSource}");
            _mediaEngine.Source = mediaFoundationSource;
            _mediaEngine.Load();
            PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.Load.Returned");

            if (_playRequested)
                _mediaEngine.Play();
        }
        catch (Exception exception)
        {
            _loadedMediaFoundationSource = null;
            OpenWithDirectShow(source, $"open: {exception.Message}");
        }
    }

    private static string GetMediaFoundationSource(Uri source)
    {
        // IMFMediaEngine ultimately passes this value to the Media Foundation
        // source resolver. Uri.AbsoluteUri percent-encodes non-ASCII path
        // segments (for example, Cyrillic as %D0...), which the resolver on
        // some Windows versions treats as a literal filesystem path and then
        // reports ERROR_PATH_NOT_FOUND. It accepts a Unicode local path in the
        // BSTR directly, preserving every filename character and UNC paths.
        return source.IsFile ? source.LocalPath : source.AbsoluteUri;
    }

    private void MediaFoundationEvent(MediaEngineEvent mediaEngineEvent, long param1, int param2)
    {
        PreviewPerformanceLogger.Mark(PerformanceContext, $"VideoPanel.MediaFoundation.Event.{mediaEngineEvent}",
            $"param1={param1}; param2=0x{param2:X8}");

        if (_disposed)
            return;

        Dispatcher.BeginInvoke(new Action(() => ProcessMediaFoundationEvent(mediaEngineEvent, param1, param2)));
    }

    private void ProcessMediaFoundationEvent(MediaEngineEvent mediaEngineEvent, long param1, int param2)
    {
        if (_disposed || !_usingMediaFoundation || _mediaEngine == null)
            return;

        switch (mediaEngineEvent)
        {
            case MediaEngineEvent.LoadedMetadata:
                UpdateMediaFoundationMetadata();
                UpdateMediaFoundationVideo();
                CompleteMediaFoundationOpen("LoadedMetadata");
                break;

            case MediaEngineEvent.LoadedData:
            case MediaEngineEvent.CanPlay:
                UpdateMediaFoundationMetadata();
                UpdateMediaFoundationVideo();
                CompleteMediaFoundationOpen(mediaEngineEvent.ToString());
                if (_playRequested && !_mediaFoundationIsPlaying)
                    _mediaEngine.Play();
                break;

            case MediaEngineEvent.Playing:
                SetMediaFoundationPlaying(true);
                break;

            case MediaEngineEvent.Pause:
                SetMediaFoundationPlaying(false);
                break;

            case MediaEngineEvent.DurationChange:
                MediaDuration = SecondsToTicks(_mediaEngine.Duration);
                break;

            case MediaEngineEvent.FirstFrameReady:
                UpdateMediaFoundationVideo();
                break;

            case MediaEngineEvent.Ended:
                SetMediaFoundationPlaying(false);
                MediaEnded?.Invoke(this, new RoutedEventArgs());
                break;

            case MediaEngineEvent.Error:
                HandleMediaFoundationError(param1, param2, "media-engine");
                break;

            case MediaEngineEvent.StreamRenderingerror:
                // This event explicitly means one stream failed while another
                // can continue. Falling back after playback started would tear
                // down a valid video because of, for example, one bad audio track.
                if (!_mediaFoundationOpened && _source != null)
                    OpenWithDirectShow(_source,
                        $"Media Foundation stream error: stream={param1}, HRESULT=0x{param2:X8}.");
                break;
        }
    }

    private void UpdateMediaFoundationMetadata()
    {
        MediaDuration = SecondsToTicks(_mediaEngine.Duration);
        HasVideo = _mediaEngine.HasVideo();
    }

    private void CompleteMediaFoundationOpen(string eventName)
    {
        if (_mediaFoundationOpened)
            return;

        _mediaFoundationOpened = true;
        _positionTimer.Start();
        PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.MediaOpened",
            $"event={eventName}; duration={MediaDuration}; hasVideo={HasVideo}");
        MediaOpened?.Invoke(this, new RoutedEventArgs());
    }

    private void MediaFoundationHostSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateMediaFoundationVideo();

    private void UpdateMediaFoundationVideo()
    {
        if (!_usingMediaFoundation || !HasVideo || _mediaEngineEx == null)
            return;

        if (!_mediaFoundationHost.TryGetClientSize(out int width, out int height) || width <= 0 || height <= 0)
            return;

        try
        {
            var destination = new RawRectangle(0, 0, width, height);
            _mediaEngineEx.UpdateVideoStream(null, destination, default(RawColorBGRA));
        }
        catch (Exception exception)
        {
            PreviewPerformanceLogger.Mark(PerformanceContext,
                "VideoPanel.MediaFoundation.VideoDestination.Failed", exception.Message);
        }
    }

    private void SetMediaFoundationPlaying(bool isPlaying)
    {
        if (_mediaFoundationIsPlaying == isPlaying)
            return;

        _mediaFoundationIsPlaying = isPlaying;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleMediaFoundationError(long param1, int param2, string stage)
    {
        string message = $"Media Foundation {stage} error: mediaError={param1}, HRESULT=0x{param2:X8}.";
        PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.MediaFailed", message);

        // Codec/container rejection is normally reported before the source has
        // opened. Retry it through the bundled LAV filters to retain format coverage.
        if (!_mediaFoundationOpened && _source != null)
        {
            OpenWithDirectShow(_source, message);
            return;
        }

        MediaFailed?.Invoke(this, new HybridMediaFailedEventArgs(new InvalidOperationException(message)));
    }

    private void OpenWithDirectShow(Uri source, string reason)
    {
        PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.DirectShow.Fallback",
            $"reason={reason}; path={source?.LocalPath}");
        _usingMediaFoundation = false;
        _mediaFoundationOpened = false;
        _mediaFoundationIsPlaying = false;
        _directShowOpened = false;
        _directShowIsPlaying = false;
        _loadedMediaFoundationSource = null;
        _positionTimer.Stop();
        PauseMediaFoundation();
        _mediaFoundationHost.Visibility = Visibility.Collapsed;
        _directShowElement.Visibility = Visibility.Visible;
        _directShowElement.Volume = _volume;
        _directShowElement.Source = source;
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PauseMediaFoundation()
    {
        if (_mediaEngine == null)
            return;

        try
        {
            _mediaEngine.Pause();
        }
        catch (Exception exception)
        {
            PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.Pause.Failed", exception.Message);
        }
    }

    private void UpdatePosition(object sender, EventArgs e)
    {
        if (!_usingMediaFoundation)
        {
            UpdatePositionValue(_directShowElement.MediaPosition);
            if (_directShowElement.MediaDuration != MediaDuration)
                MediaDuration = _directShowElement.MediaDuration;
            return;
        }

        if (_mediaEngine == null)
            return;

        try
        {
            UpdatePositionValue(SecondsToTicks(_mediaEngine.CurrentTime));
            long duration = SecondsToTicks(_mediaEngine.Duration);
            if (duration != MediaDuration)
                MediaDuration = duration;
        }
        catch (Exception exception)
        {
            PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.Position.ReadFailed", exception.Message);
        }
    }

    private static long SecondsToTicks(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds <= 0d)
            return 0L;

        double ticks = seconds * TimeSpan.TicksPerSecond;
        return ticks >= long.MaxValue ? long.MaxValue : (long)ticks;
    }

    private void DirectShowMediaOpened(object sender, RoutedEventArgs e)
    {
        if (_usingMediaFoundation)
            return;

        _directShowOpened = true;
        HasVideo = _directShowElement.HasVideo;
        MediaDuration = _directShowElement.MediaDuration;
        _positionTimer.Start();
        PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.DirectShow.MediaOpened",
            $"duration={MediaDuration}; hasVideo={HasVideo}");
        MediaOpened?.Invoke(this, e);

        if (_playRequested)
            _directShowElement.Play();
    }

    private void DirectShowMediaEnded(object sender, RoutedEventArgs e)
    {
        if (_usingMediaFoundation)
            return;

        _directShowIsPlaying = false;
        MediaEnded?.Invoke(this, e);
    }

    private void DirectShowMediaFailed(object sender, WPFMediaKit.DirectShow.MediaPlayers.MediaFailedEventArgs e)
    {
        if (!_usingMediaFoundation)
        {
            _directShowOpened = false;
            _directShowIsPlaying = false;
            PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.DirectShow.MediaFailed",
                e.Exception?.ToString());
            MediaFailed?.Invoke(this, new HybridMediaFailedEventArgs(e.Exception));
        }
    }

    private void DirectShowPlayerStateChanged(PlayerState oldState, PlayerState newState)
    {
        // WPFMediaKit raises this event from its graph worker. Only update
        // thread-safe state here; ViewerPanel marshals UI notifications to its
        // dispatcher separately.
        _directShowIsPlaying = !_usingMediaFoundation && _directShowOpened && newState == PlayerState.Playing;
    }

    private void ReleaseMediaFoundationEngine()
    {
        _mediaEngineEx?.Dispose();
        _mediaEngineEx = null;

        if (_mediaEngine != null)
        {
            try
            {
                _mediaEngine.Shutdown();
            }
            catch (Exception exception)
            {
                PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.Shutdown.Failed", exception.Message);
            }
            _mediaEngine.Dispose();
            _mediaEngine = null;
        }

        _mediaEngineAttributes?.Dispose();
        _mediaEngineAttributes = null;
        _mediaEngineFactory?.Dispose();
        _mediaEngineFactory = null;
        _mediaEngineNotify = null;
        _loadedMediaFoundationSource = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _positionTimer.Stop();
        _positionTimer.Tick -= UpdatePosition;
        _directShowElement.MediaOpened -= DirectShowMediaOpened;
        _directShowElement.MediaEnded -= DirectShowMediaEnded;
        _directShowElement.MediaFailed -= DirectShowMediaFailed;
        _directShowElement.MediaUriPlayer.PlayerStateChanged -= DirectShowPlayerStateChanged;
        _mediaFoundationHost.HandleCreated -= MediaFoundationHostHandleCreated;
        _mediaFoundationHost.HandleDestroyed -= MediaFoundationHostHandleDestroyed;
        _mediaFoundationHost.SizeChanged -= MediaFoundationHostSizeChanged;
        ReleaseMediaFoundationEngine();
        _mediaFoundationHost.Dispose();
        _directShowElement.MediaUriPlayer.Dispose();
    }
}

/// <summary>
/// Minimal native child window used as IMFMediaEngine's playback target.
/// </summary>
internal sealed class NativeMediaFoundationHost : HwndHost
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int SsBlackRect = 0x00000004;

    private IntPtr _handle;

    public new IntPtr Handle => _handle;

    public event EventHandler HandleCreated;
    public event EventHandler HandleDestroyed;

    public bool TryGetClientSize(out int width, out int height)
    {
        width = 0;
        height = 0;
        if (_handle == IntPtr.Zero || !GetClientRect(_handle, out NativeRect rectangle))
            return false;

        width = rectangle.Right - rectangle.Left;
        height = rectangle.Bottom - rectangle.Top;
        return true;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _handle = CreateWindowEx(
            0,
            "Static",
            string.Empty,
            WsChild | WsVisible | WsClipSiblings | WsClipChildren | SsBlackRect,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the Media Foundation playback window.");

        HandleCreated?.Invoke(this, EventArgs.Empty);
        return new HandleRef(this, _handle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_handle == IntPtr.Zero)
            return;

        HandleDestroyed?.Invoke(this, EventArgs.Empty);
        DestroyWindow(_handle);
        _handle = IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

public sealed class HybridMediaFailedEventArgs : EventArgs
{
    public HybridMediaFailedEventArgs(Exception exception) => Exception = exception;

    public Exception Exception { get; }
}
