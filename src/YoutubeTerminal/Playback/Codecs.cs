using YoutubeTerminal.Graphics;
using YoutubeTerminal.Native;

namespace YoutubeTerminal.Playback;

/// <summary>Czy klatkę o danym czasie warto przekonwertować (spóźnione i ponad limit FPS pomijamy przed konwersją).</summary>
internal delegate bool VideoFrameFilter(TimeSpan time);

/// <summary>Zdekodowana klatka przeskalowana do rozmiaru wyjściowego (ARGB). Bufor jest ważny tylko w trakcie wywołania.</summary>
internal delegate void VideoFrameSink(TimeSpan time, uint[] pixels);

/// <summary>Zdekodowany PCM 16-bit (przeplatany), z czasem pierwszej próbki w jednostkach 100 ns.</summary>
internal delegate void PcmSink(ReadOnlySpan<byte> pcm, long time, int sampleRate, int channels);

internal interface IVideoDecoder : IDisposable
{
    void Decode(in MediaSample sample);
    /// <summary>Koniec danych: oddaje klatki zalegające w dekoderze.</summary>
    void EndOfStream();
    /// <summary>Przerwanie z innego wątku (anulowanie) — odblokowuje Decode.</summary>
    void Abort();
}

internal interface IAudioDecoder : IDisposable
{
    void Decode(in MediaSample sample);
    void EndOfStream();
    void Abort();
}

/// <summary>
/// Wybór dekoderów: w Windows systemowe Media Foundation (bez dodatkowych plików),
/// w Linuksie i macOS — ffmpeg (systemowy albo wbudowany w program).
/// </summary>
internal static class Codecs
{
    public static IVideoDecoder CreateVideo(IMediaSource track, int width, int height, VideoFrameFilter filter, VideoFrameSink sink)
    {
        if (OperatingSystem.IsWindows())
        {
            try { return new MfVideoDecoder(track, width, height, filter, sink); }
            catch (Exception ex) when (Ffmpeg.Find() != null)
            {
                Storage.Log.Error("Media Foundation H.264 → ffmpeg", ex);
            }
        }
        return new FfmpegVideoDecoder(Ffmpeg.Require(), track, width, height, filter, sink);
    }

    public static IAudioDecoder CreateAudio(IMediaSource track, PcmSink sink)
    {
        if (OperatingSystem.IsWindows())
        {
            try { return new MfAudioDecoder(track, sink); }
            catch (Exception ex) when (Ffmpeg.Find() != null)
            {
                Storage.Log.Error("Media Foundation AAC → ffmpeg", ex);
            }
        }
        return new FfmpegAudioDecoder(Ffmpeg.Require(), track, sink);
    }
}

/// <summary>H.264 przez Media Foundation: NV12 → ARGB → skalowanie do rozmiaru wyjściowego.</summary>
internal sealed unsafe class MfVideoDecoder : IVideoDecoder
{
    private readonly H264Decoder _dec;
    private uint[] _full = [];
    private readonly uint[] _scaled;

    public MfVideoDecoder(IMediaSource track, int width, int height, VideoFrameFilter filter, VideoFrameSink sink)
    {
        MF.EnsureStarted();
        byte[]? avc = track.AnnexB ? null : track.AvcConfig ?? throw new InvalidDataException(L.T("Brak konfiguracji AVC", "Missing AVC configuration"));
        _dec = new H264Decoder(avc, track.Width, track.Height);
        _scaled = new uint[Math.Max(1, width * height)];
        _dec.OnFrame = (time, y, uv, stride) =>
        {
            var ts = TimeSpan.FromTicks(time);
            if (!filter(ts)) return;
            int dw = _dec.DisplayWidth, dh = _dec.DisplayHeight;
            if (_full.Length < dw * dh) _full = new uint[dw * dh];
            ColorConvert.Nv12ToArgb(y, uv, stride, _dec.CropX, _dec.CropY, dw, dh, _full, true);
            fixed (uint* f = _full)
                Scaler.Resize(f, dw, dh, dw, _scaled, width, height);
            sink(ts, _scaled);
        };
    }

    public void Decode(in MediaSample sample) => _dec.Decode(sample);
    public void EndOfStream() => _dec.EndOfStream();
    public void Abort() { }
    public void Dispose() => _dec.Dispose();
}

/// <summary>AAC przez Media Foundation.</summary>
internal sealed class MfAudioDecoder : IAudioDecoder
{
    private readonly AacDecoder _dec;

    public MfAudioDecoder(IMediaSource track, PcmSink sink)
    {
        MF.EnsureStarted();
        _dec = new AacDecoder(track.AudioConfig, track.SampleRate, track.Channels);
        _dec.OnPcm = (pcm, time) => sink(pcm, time, _dec.SampleRate, _dec.Channels);
    }

    public void Decode(in MediaSample sample) => _dec.Decode(sample);
    public void EndOfStream() => _dec.EndOfStream();
    public void Abort() { }
    public void Dispose() => _dec.Dispose();
}
