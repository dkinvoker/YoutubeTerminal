using System.Runtime.InteropServices;
using YoutubeTerminal.Native;

namespace YoutubeTerminal.Playback;

/// <summary>Wyjście dźwięku PCM 16-bit. Pozycja odtwarzania służy jako zegar synchronizacji obrazu.</summary>
internal interface IAudioOutput : IDisposable
{
    int SampleRate { get; }
    /// <summary>Ramki (próbki na kanał) przekazane do urządzenia.</summary>
    long SubmittedFrames { get; }
    /// <summary>Ramki już odtworzone (lub pominięte przy wstrzymaniu).</summary>
    long PlayedFrames { get; }
    /// <summary>Głośność 0..1.</summary>
    void SetVolume(float volume);
    /// <summary>Zapisuje PCM; blokuje, gdy bufory urządzenia są pełne (lub odtwarzanie wstrzymane).</summary>
    void Write(ReadOnlySpan<byte> pcm, CancellationToken ct);
    /// <summary>Wysyła częściowo wypełniony bufor (koniec strumienia).</summary>
    void Flush();
    void Pause();
    void Resume();
}

internal static class AudioOutputs
{
    public static IAudioOutput Create(int sampleRate, int channels)
    {
        if (OperatingSystem.IsWindows()) return new WaveOut(sampleRate, channels, bufferCount: 8);
        if (OperatingSystem.IsMacOS()) return new AudioQueueOutput(sampleRate, channels);
        return new PulseAudioOutput(sampleRate, channels);
    }

    /// <summary>Kopiowanie z programową głośnością (dla wyjść bez regulacji głośności strumienia).</summary>
    public static void CopyWithVolume(ReadOnlySpan<byte> src, Span<byte> dst, float volume)
    {
        if (volume >= 0.999f)
        {
            src.CopyTo(dst);
            return;
        }
        var s = MemoryMarshal.Cast<byte, short>(src);
        var d = MemoryMarshal.Cast<byte, short>(dst);
        int v = (int)(Math.Clamp(volume, 0f, 1f) * 65536);
        for (int i = 0; i < s.Length; i++)
            d[i] = (short)((s[i] * v) >> 16);
    }
}
