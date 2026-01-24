using System;
using System.IO;
using NAudio.Wave;
using NAudio.Vorbis;

namespace OsuGrind.Services;

public sealed class SoundPlayer : IDisposable
{
    private readonly string soundsFolder;
    private IWavePlayer? waveOut;
    private VorbisWaveReader? vorbisReader;
    private const float Volume = 0.5f; // 50% volume

    public SoundPlayer()
    {
        // Sounds are in the application's Resources/Sounds subfolder
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        soundsFolder = Path.Combine(appDir, "Resources", "Sounds");
    }


    public void PlayPass()
    {
        PlaySound("pass.ogg");
    }

    public void PlayFail()
    {
        PlaySound("fail.ogg");
    }

    private void PlaySound(string fileName)
    {
        try
        {
            StopCurrentSound();

            var path = Path.Combine(soundsFolder, fileName);
            if (!File.Exists(path))
                return;

            vorbisReader = new VorbisWaveReader(path);
            waveOut = new WaveOutEvent();
            waveOut.Init(vorbisReader);
            waveOut.Volume = Volume; // Set to 50% volume
            waveOut.Play();
        }
        catch
        {
            // Ignore sound errors - don't crash the app
        }
    }

    private void StopCurrentSound()
    {
        try
        {
            waveOut?.Stop();
            waveOut?.Dispose();
            vorbisReader?.Dispose();
        }
        catch { }
        finally
        {
            waveOut = null;
            vorbisReader = null;
        }
    }

    public void Dispose()
    {
        StopCurrentSound();
    }
}
