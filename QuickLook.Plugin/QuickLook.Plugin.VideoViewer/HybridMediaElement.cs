// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLook program.

using Microsoft.Toolkit.Wpf.UI.Controls;
using QuickLook.Common.Helpers;
using QuickLook.Common.Plugin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Windows.Media.Core;
using Windows.Media.Playback;
using WPFMediaKit.DirectShow.Controls;
using WPFMediaKit.DirectShow.MediaPlayers;

namespace QuickLook.Plugin.VideoViewer;

/// <summary>
/// Uses the Windows Media Foundation based MediaPlayer for common video files
/// and transparently falls back to the existing DirectShow/LAV player.
/// </summary>
public sealed class HybridMediaElement : Grid, IDisposable
{
    private static readonly HashSet<string> MediaFoundationExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".3gp", ".3g2", ".mkv", ".webm"
    };

    private readonly MediaUriElement _directShowElement;
    private readonly MediaPlayerElement _mediaFoundationElement;
    private readonly MediaPlayer _mediaFoundationPlayer;
    private readonly DispatcherTimer _positionTimer;

    private Uri _source;
    private bool _usingMediaFoundation;
    private bool _mediaFoundationOpened;
    private bool _playRequested;
    private bool _disposed;
    private bool _updatingPosition;
    private double _volume = 1d;

    public HybridMediaElement()
    {
        _directShowElement = new MediaUriElement();
        Children.Add(_directShowElement);

        _mediaFoundationPlayer = new MediaPlayer
        {
            AutoPlay = false,
            IsLoopingEnabled = false,
            Volume = _volume,
        };
        _mediaFoundationElement = new MediaPlayerElement
        {
            AreTransportControlsEnabled = false,
            AutoPlay = false,
            Visibility = Visibility.Collapsed,
            // HwndHost has WPF airspace restrictions. Keep the bottom control
            // strip outside its native child window so the buttons remain usable.
            Margin = new Thickness(0, 0, 0, 32),
        };
        _mediaFoundationElement.SetMediaPlayer(_mediaFoundationPlayer);
        Children.Add(_mediaFoundationElement);

        _directShowElement.MediaOpened += DirectShowMediaOpened;
        _directShowElement.MediaEnded += DirectShowMediaEnded;
        _directShowElement.MediaFailed += DirectShowMediaFailed;

        _mediaFoundationPlayer.MediaOpened += MediaFoundationMediaOpened;
        _mediaFoundationPlayer.MediaEnded += MediaFoundationMediaEnded;
        _mediaFoundationPlayer.MediaFailed += MediaFoundationMediaFailed;
        _mediaFoundationPlayer.PlaybackSession.PlaybackStateChanged += MediaFoundationPlaybackStateChanged;
        _mediaFoundationPlayer.PlaybackSession.NaturalVideoSizeChanged += MediaFoundationNaturalVideoSizeChanged;

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
        get => _mediaFoundationPlayer.IsLoopingEnabled;
        set
        {
            _mediaFoundationPlayer.IsLoopingEnabled = value;
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

    public bool IsPlaying => _usingMediaFoundation
        ? _mediaFoundationPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
        : _directShowElement.IsPlaying;

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
        get => _usingMediaFoundation ? _mediaFoundationPlayer.Volume : _directShowElement.Volume;
        set
        {
            _volume = Math.Max(0d, Math.Min(1d, value));
            _mediaFoundationPlayer.Volume = _volume;
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
        if (_usingMediaFoundation)
            _mediaFoundationPlayer.Play();
        else
            _directShowElement.Play();
    }

    public void Pause()
    {
        _playRequested = false;
        if (_usingMediaFoundation)
            _mediaFoundationPlayer.Pause();
        else
            _directShowElement.Pause();
    }

    public void Close()
    {
        _playRequested = false;
        _positionTimer.Stop();
        _mediaFoundationPlayer.Pause();
        _mediaFoundationPlayer.Source = null;
        _directShowElement.Close();
        MediaDuration = 0L;
        UpdatePositionValue(0L);
    }

    private void OpenSource(Uri source)
    {
        Close();
        if (source == null)
            return;

        if (CanUseMediaFoundation(source))
        {
            _usingMediaFoundation = true;
            _mediaFoundationOpened = false;
            _directShowElement.Visibility = Visibility.Collapsed;
            _mediaFoundationElement.Visibility = Visibility.Visible;
            PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.Source.Assigning", $"path={source.LocalPath}");
            _mediaFoundationPlayer.Source = MediaSource.CreateFromUri(source);
            return;
        }

        OpenWithDirectShow(source, "unsupported-extension");
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
            element._mediaFoundationPlayer.PlaybackSession.Position = TimeSpan.FromTicks(position);
        else
            element._directShowElement.MediaPosition = position;
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

    private void OpenWithDirectShow(Uri source, string reason)
    {
        PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.DirectShow.Fallback", $"reason={reason}; path={source?.LocalPath}");
        _usingMediaFoundation = false;
        _mediaFoundationOpened = false;
        _positionTimer.Stop();
        _mediaFoundationPlayer.Pause();
        _mediaFoundationPlayer.Source = null;
        _mediaFoundationElement.Visibility = Visibility.Collapsed;
        _directShowElement.Visibility = Visibility.Visible;
        _directShowElement.Volume = _volume;
        _directShowElement.Source = source;
        if (_playRequested)
            _directShowElement.Play();
    }

    private void MediaFoundationMediaOpened(MediaPlayer sender, object args)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_usingMediaFoundation || sender.Source == null)
                return;

            _mediaFoundationOpened = true;
            MediaDuration = sender.PlaybackSession.NaturalDuration.Ticks;
            HasVideo = sender.PlaybackSession.NaturalVideoWidth > 0 && sender.PlaybackSession.NaturalVideoHeight > 0;
            _positionTimer.Start();
            PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.MediaOpened",
                $"duration={MediaDuration}; width={sender.PlaybackSession.NaturalVideoWidth}; height={sender.PlaybackSession.NaturalVideoHeight}");
            MediaOpened?.Invoke(this, new RoutedEventArgs());
            if (_playRequested)
                sender.Play();
        }));
    }

    private void MediaFoundationNaturalVideoSizeChanged(MediaPlaybackSession sender, object args)
    {
        Dispatcher.BeginInvoke(new Action(() =>
            HasVideo = sender.NaturalVideoWidth > 0 && sender.NaturalVideoHeight > 0));
    }

    private void MediaFoundationMediaEnded(MediaPlayer sender, object args) =>
        Dispatcher.BeginInvoke(new Action(() => MediaEnded?.Invoke(this, new RoutedEventArgs())));

    private void MediaFoundationMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_usingMediaFoundation)
                return;

            string message = $"{args.Error}: {args.ErrorMessage} (0x{args.ExtendedErrorCode?.HResult:X8})";
            PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.MediaFailed", message);

            // A failure before MediaOpened normally means an unsupported codec or
            // container. Preserve broad format support by retrying through LAV.
            if (!_mediaFoundationOpened && _source != null)
            {
                OpenWithDirectShow(_source, message);
                return;
            }

            MediaFailed?.Invoke(this, new HybridMediaFailedEventArgs(new InvalidOperationException(message)));
        }));
    }

    private void MediaFoundationPlaybackStateChanged(MediaPlaybackSession sender, object args) =>
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PreviewPerformanceLogger.Mark(PerformanceContext, "VideoPanel.MediaFoundation.PlaybackStateChanged", $"state={sender.PlaybackState}");
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        }));

    private void UpdatePosition(object sender, EventArgs e)
    {
        long position = _usingMediaFoundation
            ? _mediaFoundationPlayer.PlaybackSession.Position.Ticks
            : _directShowElement.MediaPosition;
        UpdatePositionValue(position);

        long duration = _usingMediaFoundation
            ? _mediaFoundationPlayer.PlaybackSession.NaturalDuration.Ticks
            : _directShowElement.MediaDuration;
        if (duration != MediaDuration)
            MediaDuration = duration;
    }

    private void DirectShowMediaOpened(object sender, RoutedEventArgs e)
    {
        HasVideo = _directShowElement.HasVideo;
        MediaDuration = _directShowElement.MediaDuration;
        _positionTimer.Start();
        MediaOpened?.Invoke(this, e);
    }

    private void DirectShowMediaEnded(object sender, RoutedEventArgs e) => MediaEnded?.Invoke(this, e);

    private void DirectShowMediaFailed(object sender, WPFMediaKit.DirectShow.MediaPlayers.MediaFailedEventArgs e) =>
        MediaFailed?.Invoke(this, new HybridMediaFailedEventArgs(e.Exception));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _positionTimer.Stop();
        _positionTimer.Tick -= UpdatePosition;
        _mediaFoundationPlayer.MediaOpened -= MediaFoundationMediaOpened;
        _mediaFoundationPlayer.MediaEnded -= MediaFoundationMediaEnded;
        _mediaFoundationPlayer.MediaFailed -= MediaFoundationMediaFailed;
        _mediaFoundationPlayer.PlaybackSession.PlaybackStateChanged -= MediaFoundationPlaybackStateChanged;
        _mediaFoundationPlayer.PlaybackSession.NaturalVideoSizeChanged -= MediaFoundationNaturalVideoSizeChanged;
        _mediaFoundationPlayer.Dispose();
        _mediaFoundationElement.Dispose();
        _directShowElement.MediaUriPlayer.Dispose();
    }
}

public sealed class HybridMediaFailedEventArgs : EventArgs
{
    public HybridMediaFailedEventArgs(Exception exception) => Exception = exception;

    public Exception Exception { get; }
}
