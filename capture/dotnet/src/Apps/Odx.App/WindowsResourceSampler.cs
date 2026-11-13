using System.Diagnostics;
using System.Runtime.InteropServices;
using Odx.Core;

namespace Odx.App;

/// <summary>
/// Windows telemetry provider. CPU/RAM use native system counters; GPU 3D/Video Encode and adapter memory
/// use English PDH counters; NVIDIA VRAM/temperature/encoder are read through optional NVML.
/// Unsupported sensors are returned as null, never as a fabricated zero.
/// </summary>
public sealed class WindowsResourceSampler : IDisposable
{
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;
    private const uint NvmlGpuTemperature = 0;

    private IntPtr _pdhQuery, _gpuEngineCounter, _dedicatedUsageCounter, _dedicatedLimitCounter;
    private IntPtr _nvmlLibrary;
    private NvmlInit? _nvmlInit;
    private NvmlShutdown? _nvmlShutdown;
    private NvmlGetDevice? _nvmlGetDevice;
    private NvmlGetEncoderUtilization? _nvmlGetEncoderUtilization;
    private NvmlGetMemoryInfo? _nvmlGetMemoryInfo;
    private NvmlGetTemperature? _nvmlGetTemperature;
    private bool _nvmlReady;
    private bool _hasCpuSample;
    private ulong _lastIdle, _lastKernel, _lastUser;

    public WindowsResourceSampler()
    {
        if (!OperatingSystem.IsWindows()) return;
        InitializePdh();
        InitializeNvml();
    }

    public ResourceSnapshot Sample(double? freshFps, double? encoderStarvationPercent)
    {
        double? cpu = ReadCpuPercent();
        var memory = ReadMemoryPercent();
        if (_pdhQuery != IntPtr.Zero) _ = PdhCollectQueryData(_pdhQuery);

        var gpuItems = ReadCounterItems(_gpuEngineCounter);
        double? gpu3D = SumEngine(gpuItems, "engtype_3D");
        double? gpuEncodePdh = SumEngine(gpuItems, "engtype_VideoEncode");
        double? vramPdh = ReadDedicatedVramPercent();

        var nvml = ReadNvml();
        return new ResourceSnapshot(
            cpu,
            memory.UsedPercent,
            memory.AvailablePercent,
            gpu3D,
            gpuEncodePdh ?? nvml.EncoderPercent,
            vramPdh ?? nvml.VramPercent,
            nvml.TemperatureC,
            freshFps,
            encoderStarvationPercent);
    }

    private void InitializePdh()
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _pdhQuery) != 0) { _pdhQuery = IntPtr.Zero; return; }
            _gpuEngineCounter = AddCounter(@"\GPU Engine(*)\Utilization Percentage");
            _dedicatedUsageCounter = AddCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
            _dedicatedLimitCounter = AddCounter(@"\GPU Adapter Memory(*)\Dedicated Limit");
            _ = PdhCollectQueryData(_pdhQuery);
        }
        catch (EntryPointNotFoundException)
        {
            if (_pdhQuery != IntPtr.Zero) PdhCloseQuery(_pdhQuery);
            _pdhQuery = IntPtr.Zero;
        }
        catch (DllNotFoundException)
        {
            _pdhQuery = IntPtr.Zero;
        }
    }

    private IntPtr AddCounter(string path)
    {
        return PdhAddEnglishCounterW(_pdhQuery, path, 0, out var counter) == 0 ? counter : IntPtr.Zero;
    }

    private void InitializeNvml()
    {
        try
        {
            if (!NativeLibrary.TryLoad("nvml.dll", out _nvmlLibrary)) return;
            _nvmlInit = GetExport<NvmlInit>("nvmlInit_v2") ?? GetExport<NvmlInit>("nvmlInit");
            _nvmlShutdown = GetExport<NvmlShutdown>("nvmlShutdown");
            _nvmlGetDevice = GetExport<NvmlGetDevice>("nvmlDeviceGetHandleByIndex_v2") ??
                             GetExport<NvmlGetDevice>("nvmlDeviceGetHandleByIndex");
            _nvmlGetEncoderUtilization = GetExport<NvmlGetEncoderUtilization>("nvmlDeviceGetEncoderUtilization");
            _nvmlGetMemoryInfo = GetExport<NvmlGetMemoryInfo>("nvmlDeviceGetMemoryInfo");
            _nvmlGetTemperature = GetExport<NvmlGetTemperature>("nvmlDeviceGetTemperature");
            _nvmlReady = _nvmlInit?.Invoke() == 0 && _nvmlGetDevice is not null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _nvmlReady = false;
        }
    }

    private T? GetExport<T>(string name) where T : Delegate
    {
        if (_nvmlLibrary == IntPtr.Zero || !NativeLibrary.TryGetExport(_nvmlLibrary, name, out var address)) return null;
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private double? ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        ulong idleNow = ToUInt64(idle), kernelNow = ToUInt64(kernel), userNow = ToUInt64(user);
        if (!_hasCpuSample)
        {
            _lastIdle = idleNow; _lastKernel = kernelNow; _lastUser = userNow; _hasCpuSample = true;
            return null;
        }
        ulong idleDelta = idleNow - _lastIdle;
        ulong kernelDelta = kernelNow - _lastKernel;
        ulong userDelta = userNow - _lastUser;
        _lastIdle = idleNow; _lastKernel = kernelNow; _lastUser = userNow;
        ulong total = kernelDelta + userDelta;
        if (total == 0) return null;
        return Math.Clamp((1.0 - (double)idleDelta / total) * 100.0, 0, 100);
    }

    private static (double? UsedPercent, double? AvailablePercent) ReadMemoryPercent()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0) return (null, null);
        double available = Math.Clamp(100.0 * status.AvailablePhysical / status.TotalPhysical, 0, 100);
        return (100.0 - available, available);
    }

    private static double? SumEngine(IReadOnlyList<CounterValue> items, string engineType)
    {
        var matches = items.Where(x => x.Name.Contains(engineType, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value).Where(double.IsFinite).ToArray();
        return matches.Length == 0 ? null : Math.Clamp(matches.Sum(), 0, 100);
    }

    private double? ReadDedicatedVramPercent()
    {
        var usage = ReadCounterItems(_dedicatedUsageCounter);
        var limit = ReadCounterItems(_dedicatedLimitCounter);
        if (usage.Count == 0 || limit.Count == 0) return null;

        var usedByAdapter = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in usage)
        {
            string key = AdapterKey(item.Name);
            usedByAdapter[key] = usedByAdapter.GetValueOrDefault(key) + Math.Max(0, item.Value);
        }
        var limitByAdapter = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in limit)
        {
            string key = AdapterKey(item.Name);
            limitByAdapter[key] = Math.Max(limitByAdapter.GetValueOrDefault(key), Math.Max(0, item.Value));
        }
        double totalUsed = usedByAdapter.Values.Sum();
        double totalLimit = limitByAdapter.Values.Sum();
        return totalLimit > 0 ? Math.Clamp(totalUsed / totalLimit * 100.0, 0, 100) : null;
    }

    private static string AdapterKey(string instance)
    {
        int luid = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (luid < 0) return instance;
        int phys = instance.IndexOf("_phys_", luid, StringComparison.OrdinalIgnoreCase);
        if (phys < 0) return instance[luid..];
        int end = instance.IndexOf('_', phys + 6);
        return end < 0 ? instance[luid..] : instance[luid..end];
    }

    private static IReadOnlyList<CounterValue> ReadCounterItems(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return [];
        uint bytes = 0, count = 0;
        uint status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref bytes, ref count, IntPtr.Zero);
        if (status != PdhMoreData || bytes == 0) return [];
        IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref bytes, ref count, buffer);
            if (status != 0) return [];
            int size = Marshal.SizeOf<PdhFormattedItem>();
            var values = new List<CounterValue>((int)count);
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhFormattedItem>(IntPtr.Add(buffer, i * size));
                if (item.Value.Status != 0 || item.Name == IntPtr.Zero) continue;
                string? name = Marshal.PtrToStringUni(item.Name);
                if (!string.IsNullOrWhiteSpace(name) && double.IsFinite(item.Value.DoubleValue))
                    values.Add(new CounterValue(name, item.Value.DoubleValue));
            }
            return values;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private (double? EncoderPercent, double? VramPercent, double? TemperatureC) ReadNvml()
    {
        if (!_nvmlReady || _nvmlGetDevice is null) return (null, null, null);
        double? maxEncoder = null, maxTemperature = null;
        ulong totalMemory = 0, usedMemory = 0;
        bool gotMemory = false;
        for (uint index = 0; index < 16; index++)
        {
            if (_nvmlGetDevice(index, out var device) != 0) break;
            if (_nvmlGetEncoderUtilization?.Invoke(device, out uint encoder, out _) == 0)
                maxEncoder = Math.Max(maxEncoder ?? 0, encoder);
            if (_nvmlGetTemperature?.Invoke(device, NvmlGpuTemperature, out uint temperature) == 0)
                maxTemperature = Math.Max(maxTemperature ?? 0, temperature);
            if (_nvmlGetMemoryInfo?.Invoke(device, out var memory) == 0 && memory.Total > 0)
            {
                totalMemory += memory.Total;
                usedMemory += memory.Used;
                gotMemory = true;
            }
        }
        double? vram = gotMemory && totalMemory > 0 ? Math.Clamp(100.0 * usedMemory / totalMemory, 0, 100) : null;
        return (maxEncoder, vram, maxTemperature);
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    public void Dispose()
    {
        if (_pdhQuery != IntPtr.Zero) { PdhCloseQuery(_pdhQuery); _pdhQuery = IntPtr.Zero; }
        if (_nvmlReady) _nvmlShutdown?.Invoke();
        _nvmlReady = false;
        if (_nvmlLibrary != IntPtr.Zero) { NativeLibrary.Free(_nvmlLibrary); _nvmlLibrary = IntPtr.Zero; }
    }

    private sealed record CounterValue(string Name, double Value);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedValue { public uint Status; public double DoubleValue; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedItem { public IntPtr Name; public PdhFormattedValue Value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory { public ulong Total, Free, Used; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlShutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetDevice(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetEncoderUtilization(IntPtr device, out uint utilization, out uint samplingPeriodUs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetMemoryInfo(IntPtr device, out NvmlMemory memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlGetTemperature(IntPtr device, uint sensor, out uint temperature);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, ulong userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
