using System;
using System.Runtime.InteropServices;

namespace Interstellar.Android;

internal static class StarlightVoiceNative
{
    private const string LibraryName = "libstarlight.so";

    private static bool? _isAvailable;

    public static bool IsAvailable
    {
        get
        {
            if (_isAvailable.HasValue)
                return _isAvailable.Value;

            try
            {
                GetSampleRate();
                _isAvailable = true;
            }
            catch (DllNotFoundException) { _isAvailable = false; }
            catch (EntryPointNotFoundException) { _isAvailable = false; }
            catch { _isAvailable = false; }

            return _isAvailable.Value;
        }
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "starlight_voice_has_record_audio_permission")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool HasRecordAudioPermission();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "starlight_voice_request_record_audio_permission")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool RequestRecordAudioPermission();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "starlight_voice_start_capture")]
    public static extern int StartCapture(int sampleRate, int bufferFrames);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "starlight_voice_read_float")]
    public static extern int ReadFloat([Out] float[] destination, int length);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "starlight_voice_stop_capture")]
    public static extern void StopCapture();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "starlight_voice_is_capture_running")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool IsCaptureRunning();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "starlight_voice_get_sample_rate")]
    public static extern int GetSampleRate();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "starlight_voice_get_buffer_frames")]
    public static extern int GetBufferFrames();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "starlight_voice_get_last_error")]
    private static extern IntPtr GetLastErrorPtr();

    public static string GetLastError()
    {
        IntPtr ptr = GetLastErrorPtr();
        return ptr == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringAnsi(ptr) ?? string.Empty);
    }
}
