using System.IO;
using System.Media;

namespace Pegline.App.Platform;

internal enum Sound
{
    /// <summary>Something new hangs on the shelves.</summary>
    Hang,
    /// <summary>A card is taken down or moved to Trash.</summary>
    TakeDown,
}

/// <summary>Two short, quiet cues synthesized once, so no sound files ship with the app.</summary>
internal static class Sounds
{
    private const int Rate = 44100;
    private static readonly Dictionary<Sound, SoundPlayer> Players = [];

    public static void Play(Sound sound)
    {
        try
        {
            if (!Players.TryGetValue(sound, out var player))
            {
                player = new SoundPlayer(sound == Sound.Hang ? Tink() : Pop());
                player.Load();
                Players[sound] = player;
            }
            player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            App.Log(ex);
        }
    }

    /// <summary>A small bell: two high partials with a fast decay.</summary>
    private static MemoryStream Tink() => Wave(0.16, t =>
        0.30 * Math.Exp(-t * 32) * (Math.Sin(2 * Math.PI * 2093 * t) + 0.35 * Math.Sin(2 * Math.PI * 5274 * t)));

    /// <summary>A soft pop: a falling tone that dies away almost at once.</summary>
    private static MemoryStream Pop() => Wave(0.09, t =>
    {
        var frequency = 520 - 2600 * t;
        return 0.25 * Math.Exp(-t * 48) * Math.Sin(2 * Math.PI * frequency * t);
    });

    /// <summary>16-bit mono PCM in a RIFF container, with a 3 ms fade-in so the start never clicks.</summary>
    private static MemoryStream Wave(double seconds, Func<double, double> sample)
    {
        var count = (int)(seconds * Rate);
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + count * 2);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(Rate);
            writer.Write(Rate * 2);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(count * 2);
            for (var i = 0; i < count; i++)
            {
                var t = (double)i / Rate;
                var fade = Math.Min(1, t / 0.003);
                writer.Write((short)Math.Clamp(sample(t) * fade * short.MaxValue, short.MinValue, short.MaxValue));
            }
        }
        stream.Position = 0;
        return stream;
    }
}
