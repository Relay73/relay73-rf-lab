using System.Runtime.InteropServices;

namespace FakeRadio.AudioNode;

/// <summary>
/// Optional native IONOS-derived HF channel. Does not fall back to the legacy
/// processor on native load failures (that would make a test misleading).
/// </summary>
internal sealed class IonosNativeProcessor : IDisposable
{
    private const string Library = "ionos_channel";
    private IntPtr _handle;
    private long _revision = long.MinValue;

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ionos_create")]
    private static extern IntPtr Create(int sampleRate, int mode, int paths, double snrDb, uint seed, double offsetHz);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ionos_process")]
    private static extern int ProcessNative(IntPtr handle, float[] input, [Out] float[] output, int length);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ionos_destroy")]
    private static extern void Destroy(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ionos_abi_version")]
    private static extern int AbiVersion();

    public static void AssertAvailable()
    {
        try
        {
            if (AbiVersion() != 1)
                throw new InvalidOperationException("Unexpected IONOS library ABI; rebuild ionos_channel.");
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "IONOS profile requires ionos_channel.dll alongside FakeRadio.exe (or libionos_channel.so on Linux). " +
                "The native engine is missing; refusing to substitute a different channel model.", ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new InvalidOperationException("IONOS native library is outdated; rebuild ionos_channel.", ex);
        }
    }

    public float[] Process(float[] input, ChannelSnapshot settings)
    {
        if (settings.IonosMode == "OFF")
            throw new InvalidOperationException("IONOS processor called on a legacy channel.");
        if (settings.Revision != _revision)
        {
            DisposeHandle();
            AssertAvailable();
            var mode = settings.IonosMode switch
            {
                "WGN" => 0,
                "MPG" => 1,
                "MPM" => 2,
                "MPP" => 3,
                "MPD" => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(settings.IonosMode))
            };
            _handle = Create(48_000, mode, settings.IonosPaths, settings.SnrDb,
                unchecked((uint)settings.NoiseSeed), settings.IonosOffsetHz);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException("Failed to configure IONOS native channel.");
            _revision = settings.Revision;
        }

        var output = new float[input.Length];
        if (ProcessNative(_handle, input, output, input.Length) != 0)
            throw new InvalidOperationException("IONOS native channel failed to process received audio.");
        return output;
    }

    private void DisposeHandle()
    {
        if (_handle == IntPtr.Zero)
            return;
        Destroy(_handle);
        _handle = IntPtr.Zero;
    }

    public void Dispose()
    {
        DisposeHandle();
        _revision = long.MinValue;
    }
}
