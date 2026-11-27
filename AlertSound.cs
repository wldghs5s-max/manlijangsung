using System.Runtime.InteropServices;

static class AlertSound
{
    private static readonly byte[] AlarmWav = BuildAlarm();
    private static readonly object Gate = new();

    public static string EnableLoudOutput()
    {
        if (!OperatingSystem.IsWindows())
            return "알림음은 Windows에서만 납니다.";

        bool wave = waveOutSetVolume(0, 0xFFFFFFFF) == 0;
        bool endpoint = TrySetEndpointFullVolume();
        if (endpoint)
            return "허풍선 알림은 헤드셋(기본 소리 장치)으로 내고, 그 음량을 최대로 올렸습니다. 게임 소리도 함께 커집니다.";
        if (wave)
            return "허풍선 알림은 헤드셋으로 냅니다. Windows 볼륨이 낮으면 작을 수 있습니다.";
        return "허풍선 알림음을 재생합니다. Windows 볼륨을 올려 두세요.";
    }

    public static void Play()
    {
        if (!OperatingSystem.IsWindows())
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (!Monitor.TryEnter(Gate))
                return;
            try
            {
                waveOutSetVolume(0, 0xFFFFFFFF);
                PlaySound(AlarmWav, 0, 0x0004 | 0x0002);
            }
            catch (Exception)
            {
            }
            finally
            {
                Monitor.Exit(Gate);
            }
        });
    }

    private static bool TrySetEndpointFullVolume()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice device) != 0 || device is null)
                return false;

            Guid iid = new("5CDF2C82-841E-4546-9722-0CF74078229A");
            if (device.Activate(ref iid, 23, 0, out object activated) != 0 || activated is not IAudioEndpointVolume volume)
                return false;

            Guid context = Guid.Empty;
            volume.SetMute(false, ref context);
            return volume.SetMasterVolumeLevelScalar(1f, ref context) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static byte[] BuildAlarm()
    {
        const int rate = 44100;
        var samples = new List<short>(rate * 2);
        for (int round = 0; round < 3; round++)
        {
            AddTone(samples, rate, 1760, 150);
            AddSilence(samples, rate, 40);
            AddTone(samples, rate, 1760, 150);
            AddSilence(samples, rate, 40);
            AddTone(samples, rate, 2480, 220);
            AddSilence(samples, rate, 90);
        }

        var wav = new byte[44 + (samples.Count * 2)];
        wav[0] = (byte)'R';
        wav[1] = (byte)'I';
        wav[2] = (byte)'F';
        wav[3] = (byte)'F';
        WriteInt(wav, 4, wav.Length - 8);
        wav[8] = (byte)'W';
        wav[9] = (byte)'A';
        wav[10] = (byte)'V';
        wav[11] = (byte)'E';
        wav[12] = (byte)'f';
        wav[13] = (byte)'m';
        wav[14] = (byte)'t';
        wav[15] = (byte)' ';
        WriteInt(wav, 16, 16);
        wav[20] = 1;
        wav[22] = 1;
        WriteInt(wav, 24, rate);
        WriteInt(wav, 28, rate * 2);
        wav[32] = 2;
        wav[34] = 16;
        wav[36] = (byte)'d';
        wav[37] = (byte)'a';
        wav[38] = (byte)'t';
        wav[39] = (byte)'a';
        WriteInt(wav, 40, samples.Count * 2);
        for (int i = 0; i < samples.Count; i++)
        {
            short sample = samples[i];
            wav[44 + (i * 2)] = (byte)sample;
            wav[45 + (i * 2)] = (byte)(sample >> 8);
        }
        return wav;
    }

    private static void AddTone(List<short> samples, int rate, double hz, int milliseconds)
    {
        int count = rate * milliseconds / 1000;
        for (int i = 0; i < count; i++)
        {
            double fade = Math.Min(1, Math.Min(i, count - 1 - i) / (rate * 0.004));
            double wave = Math.Sin(2 * Math.PI * hz * i / rate) >= 0 ? 1 : -1;
            samples.Add((short)(wave * fade * 32000));
        }
    }

    private static void AddSilence(List<short> samples, int rate, int milliseconds)
    {
        int count = rate * milliseconds / 1000;
        for (int i = 0; i < count; i++)
            samples.Add(0);
    }

    private static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    [DllImport("winmm.dll")]
    private static extern uint waveOutSetVolume(nint device, uint volume);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[] sound, nint module, uint flags);

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint classContext, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint client);
        [PreserveSig] int UnregisterControlChangeNotify(nint client);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute(out bool mute);
    }
}

static class MotherboardBeep
{
    public static string Probe()
    {
        if (!OperatingSystem.IsWindows())
            return "본체 스피커 확인은 Windows에서만 됩니다.";

        nint handle = CreateFile(@"\\.\Beep", 0xC0000000, 0, 0, 3, 0, 0);
        if (handle == 0 || handle == new nint(-1))
        {
            int error = Marshal.GetLastWin32Error();
            return $"본체 스피커는 사용할 수 없습니다. (장치 오류 {error}) Windows 7부터 프로그램 비프는 메인보드 스피커가 아니라 기본 소리 장치로만 나갑니다.";
        }

        try
        {
            var tone = new BeepParam { Frequency = 880, Duration = 120 };
            bool played = DeviceIoControl(handle, 0x00010000, ref tone, 8, 0, 0, out _, 0);
            if (!played)
                return $"본체 스피커 장치는 열려 있으나 소리를 내지 못했습니다. (오류 {Marshal.GetLastWin32Error()}) 알림은 헤드셋으로 냅니다.";
            return "본체 스피커로 짧은 음을 보냈습니다. 케이스에서 안 들리면 보드에 피에조가 없는 것입니다. 허풍선 알림은 헤드셋으로 냅니다.";
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(nint device, uint code, ref BeepParam input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BeepParam
    {
        public uint Frequency;
        public uint Duration;
    }
}
