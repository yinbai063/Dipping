using System.Runtime.InteropServices;
using System.Text;
using MvVSControlSDKNet;

namespace Dipping.WPF.Services
{
    public static class VisionCameraEnumService
    {
        public static VisionCameraEnumResult Enumerate()
        {
            VisionSdkGate.Process.Wait();
            try
            {
                return EnumerateCore();
            }
            finally
            {
                VisionSdkGate.Process.Release();
            }
        }

        private static VisionCameraEnumResult EnumerateCore()
        {
            var sdkVersion = string.Empty;
            try
            {
                var v = CSystem.GetSDKVersion();
                sdkVersion = $"V{(v >> 24) & 0xFF}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";
            }
            catch (Exception ex)
            {
                return Fail($"无法加载 SDK：{ex.Message}", sdkVersion);
            }

            try
            {
                var deviceList = new MV_VS_DEVICE_INFO_LIST { nDeviceNum = 0 };
                var nRet = CSystem.EnumDevices(ref deviceList);
                if (nRet != CErrorCode.MV_VS_OK)
                    return Fail($"Enum devices failed: 0x{nRet:X}", sdkVersion);

                if (deviceList.nDeviceNum == 0 || deviceList.pDeviceInfo == null)
                {
                    return new VisionCameraEnumResult
                    {
                        Success = true,
                        SdkVersion = sdkVersion,
                        Message = "未找到相机"
                    };
                }

                var devices = new List<DiscoveredCameraItem>();
                var count = Math.Min(deviceList.nDeviceNum, (uint)deviceList.pDeviceInfo.Length);
                for (uint i = 0; i < count; i++)
                {
                    var ptr = deviceList.pDeviceInfo[i];
                    if (ptr == IntPtr.Zero)
                        continue;

                    var info = Marshal.PtrToStructure<MV_VS_DEVICE_INFO>(ptr);
                    var ip = FormatIp(info.nCurrentIp);
                    devices.Add(new DiscoveredCameraItem
                    {
                        DeviceInfo = info,
                        Ip = ip,
                        Model = info.chModelName ?? string.Empty,
                        SerialNumber = info.chSerialNumber ?? string.Empty,
                        DisplayName = $"[{i}] {info.chModelName} ({ip})"
                    });
                }

                return new VisionCameraEnumResult
                {
                    Success = true,
                    SdkVersion = sdkVersion,
                    Message = $"找到 {devices.Count} 台相机",
                    Devices = devices
                };
            }
            catch (DllNotFoundException ex)
            {
                return Fail($"找不到原生 SDK（{ex.Message}）。", sdkVersion);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message, sdkVersion);
            }
        }

        private static VisionCameraEnumResult Fail(string message, string sdkVersion) => new()
        {
            Success = false,
            SdkVersion = sdkVersion,
            Message = message
        };

        public static string FormatIp(uint ip) =>
            $"{(ip >> 24) & 0xFF}.{(ip >> 16) & 0xFF}.{(ip >> 8) & 0xFF}.{ip & 0xFF}";
    }

    public sealed class VisionCameraEnumResult
    {
        public bool Success { get; init; }
        public string SdkVersion { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public IReadOnlyList<DiscoveredCameraItem> Devices { get; init; } = Array.Empty<DiscoveredCameraItem>();
    }

    public sealed class DiscoveredCameraItem
    {
        public MV_VS_DEVICE_INFO DeviceInfo { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public string Ip { get; init; } = string.Empty;
        public string Model { get; init; } = string.Empty;
        public string SerialNumber { get; init; } = string.Empty;
    }
}
